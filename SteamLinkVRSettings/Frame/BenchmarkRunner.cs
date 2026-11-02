using System.Diagnostics;
using System.Text.Json;
using SteamLinkVRSettings.Services;

namespace SteamLinkVRSettings.Frame;

internal sealed record BenchmarkReport(int Version, DateTimeOffset Started, string Outcome,
    BenchmarkOptions Options, string Path, int Width, int Fps, string? PresetName,
    TuningProfile? Profile, BenchmarkResult? Result, string? Error)
{
    public IReadOnlyList<BenchmarkSavedOption> Choices { get; init; }=[];
    public BenchmarkProgressState? Progress { get; init; }
}
internal sealed record BenchmarkSavedOption(string Name,TuningProfile Profile,BenchmarkRecommendation Recommendation);

internal delegate Task<BenchmarkResult> BenchmarkPlan(BenchmarkOptions options,int ceiling,bool initialAsync,
    Func<BenchmarkCandidate,int,CancellationToken,Task<BenchmarkTrial>> measure,Action<string> progress,CancellationToken token,
    Action<BenchmarkTrial>? completed,BenchmarkQuality? initialQuality,BenchmarkCandidate? reference);

internal sealed class BenchmarkRunner(SteamPaths paths, FramePreferences preferences, EncoderProbe probe, TemporaryPowerLease? sharedPower = null,
    BenchmarkPlan? testPlan = null,Action<BenchmarkProgressState>? progressChanged=null)
{
    private readonly BenchmarkProgressTracker _progress=new(progressChanged);
    public BenchmarkProgressState Progress=>_progress.Current;
    public static string ReportsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamLinkVRSettings", "Benchmarks");
    public string? ReportPath { get; private set; }
    public string? PresetName { get; private set; }
    public TuningProfile? Recommended { get; private set; }
    public bool RecoveryRequired { get; private set; }
    public IReadOnlyList<BenchmarkSavedOption> Choices { get; private set; }=[];
    internal static bool MeasurementStreamMatches(EncoderSnapshot encoder,string generation,int? runtimePid) =>
        encoder.Generation==generation && encoder.Installed && !encoder.PairedCodecTrialPresent &&
        encoder.Codec=="HEVC" && encoder.CodecObserved && !encoder.CodecReadFailed &&
        encoder.ProcessId>0 && encoder.ProcessId==runtimePid;

    public async Task<BenchmarkResult> RunAsync(BenchmarkOptions options, Action<string> progress, CancellationToken token)
    {
        void Report(string text){_progress.Activity(text);progress(text);}
        options.Validate();
        using var connection = new BenchmarkOpenVrConnection(paths);
        var lease = new BenchmarkSettingsLease(paths, probe);
        var display=new BenchmarkDisplayLease(paths,preferences);
        if (lease.Pending || display.Pending) throw new IOException("An interrupted benchmark needs recovery. Use Restore starting settings first.");
        var patch = await new BitratePatcher().InspectAsync(paths.Driver);
        if (!patch.IsSupported) throw new IOException("This SteamVR driver is not supported by the benchmark monitor.");
        ValidateModeComparison(options, patch.IsPatched);
        var owner = await new CodecController().ReadStatusAsync();
        if (owner.Live) throw new IOException("Switch back to HEVC and end the paired codec session before benchmarking.");
        await using var activity = new BenchmarkActivityLease(preferences);
        if (options.Unattended)
        {
            Report("Keeping the Frame awake for unattended testing…");
            await activity.StartAsync(token);
            await Task.Delay(1000, token);
        }
        var before = await probe.GetSnapshotAsync();
        if (before.RequiresSteamVrRestart) throw new IOException("Use Reconnect Frame to finish the helper update, then start the benchmark.");
        if (!before.Installed) before = await probe.StartAsync();
        if (!before.Active || !before.CodecObserved || before.Codec != "HEVC" || before.PairedCodecTrialPresent || before.CodecReadFailed)
            throw new IOException("Start a normal HEVC stream to the Frame before benchmarking. This version requires NVIDIA NVENC.");
        if (before.EncodingPolicy is not { Observed: true, Pending: false, NeedsNewSession: false, Unsupported: false, InitializationRejected: false } policy ||
            policy.RateControl is not (1 or 2) || policy.Width is < 512 or > 4096)
            throw new IOException("Apply the current HEVC encoder policy and reconnect before benchmarking. Accepted rate control and width are required.");
        if (before.AdvancedEncoding is { } advanced && (advanced.PendingFlags != 0 || advanced.RejectedFlags != 0))
            throw new IOException("Resolve pending or rejected advanced encoder settings before benchmarking.");

        Report("Checking headset counters and the wireless route…");
        var headset = await BenchmarkHeadsetReader.ReadAsync(preferences, token);
        var donglePresent = await Task.Run(() => new FrameTrafficMonitor().Sample().Any(a => a.Up && a.LinkType == 2), token);
        int ceiling = patch.IsPatched ? 500 : donglePresent ? 232 : 350;
        var compositor = new CompositorTiming(paths);
        var health = StreamingHealth.FromSteamRoot(paths.SteamRoot);
        var receiverHealth = new FrameReceiverDiagnostics(preferences);
        var currentPc = await compositor.PollAsync(token);
        if (!currentPc.Available) throw new IOException(currentPc.Message);
        if (currentPc.ScenePid is > 0)
        {
            using var active = Process.GetProcessById(currentPc.ScenePid.Value);
            var file = active.MainModule?.FileName ?? "";
            if (!file.StartsWith(paths.SteamVrRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(file).Equals("steamtours.exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Close the running VR game before starting. The benchmark opens its own headset scene.");
        }
        var driver = new SettingsService { UserConfigPath = paths.Config, DefaultsPath = paths.Defaults };
        var video = new SettingsService { UserConfigPath = paths.Config, DefaultsPath = Path.Combine(paths.SteamVrRoot, "resources", "settings", "default.vrsettings"), Section = "steamvr" };
        var baseline = await new StockProfiles(driver, video, StockProfiles.DefaultPath).CaptureAsync(preferences, patch.IsPatched);
        // Freeze the tested values, including resolved defaults, so a later preset
        // cannot silently acquire unrelated settings from a different starting setup.
        var resolvedDriver = await driver.LoadMergedAsync();
        var resolvedVideo = await video.LoadMergedAsync();
        baseline = baseline with
        {
            Driver = resolvedDriver.Where(p => SettingOwnership.TuningKeys.Contains(p.Key)).ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
            Video = resolvedVideo.Where(p => SettingOwnership.VideoKeys.Contains(p.Key)).ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
            Codec = StreamCodec.Hevc, QualityEnabled = before.Enabled, GazeOffset = before.CenterOffset, PeripheralOffset = before.PeripheralOffset,
            RateControl = policy.RateControl == 1 ? EncoderRateControl.Vbr : EncoderRateControl.Cbr, MultiPass = policy.Requested.MultiPass,
            Advanced = before.AdvancedEncoding?.Requested ?? new()
        };
        StockProfiles.Validate(baseline);
        var initial = await Task.Run(() => BenchmarkSettingsLease.ReadLive(paths), token);
        var activeAsync = await probe.ReadActiveSendModeAsync();
        if (activeAsync != initial["asyncSend"].GetBoolean())
            throw new IOException("The requested send mode is not active. Use Reconnect Frame, then start auto tune. Sender mode is fixed for each connection.");
        int width = (int)policy.Width, fps = 0, initialWidth=width, initialFps=0;
        var started = DateTimeOffset.Now;
        Directory.CreateDirectory(ReportsDirectory);
        ReportPath = Path.Combine(ReportsDirectory, "benchmark-" + started.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        BenchmarkResult? result = null; string? error = null; string outcome = "Interrupted";
        string stage = "Starting the benchmark";
        var scene = new BenchmarkSceneProcess();
        var receiverMonitor = new BenchmarkReceiverMonitor();
        var power = sharedPower ?? new TemporaryPowerLease(new OpenVrPowerSettings(paths));
        bool ownsPower = false;
        using var observersStop = new CancellationTokenSource();
        Task? pcTask = null;
        int observing=1;
        string? runtimeHost=await BenchmarkHostConnection.CaptureAsync(preferences,token);
        Task<BenchmarkReceiverMonitor> StartReceiver(CancellationToken ct)=>BenchmarkReceiverStartup.ConnectAsync(async attemptToken=>
        {
            var next=new BenchmarkReceiverMonitor();
            try {await next.StartAsync(preferences,options.Unattended,attemptToken,Report);return next;}
            catch {await next.DisposeAsync();throw;}
        },async retryToken=>
        {
            if(lease.Pending)await lease.CheckControlsAsync();
            if(options.Unattended)await BenchmarkHeadsetReader.SetActivityAsync(preferences,60,retryToken);
            await BenchmarkHostConnection.ResumeAsync(preferences,runtimeHost,retryToken);
        },Report,ct);
        async Task RestartRuntime(CancellationToken ct,bool resumeMeasurement)
        {
            Interlocked.Exchange(ref observing,0);
            await receiverMonitor.DisposeAsync();await scene.DisposeAsync();connection.Release();
            if(options.Unattended)await BenchmarkHeadsetReader.SetActivityAsync(preferences,60,ct);
            await BenchmarkRuntimeRestart.RunAsync(paths,preferences,probe,lease,Report,ct,runtimeHost);
            connection.Retain();
            if(resumeMeasurement)
            {
                receiverMonitor=await StartReceiver(ct);
                scene=new BenchmarkSceneProcess();await scene.StartAsync(paths,options.Unattended,ct);
                if(scene.Current.Fps!=fps)throw new IOException("The display rate changed after restart. Retune at the new Hz.");
                Interlocked.Exchange(ref observing,1);
            }
        }
        try
        {
            token.ThrowIfCancellationRequested();
            await receiverMonitor.DisposeAsync();
            receiverMonitor=await StartReceiver(token);
            if (options.Unattended) await activity.HandOffToMonitorAsync();
            if (options.Unattended && !power.Enabled) { ownsPower = true; await power.EnableAsync(); }
            Report(options.Unattended ? "Opening the moving test room. The Frame can stay on your desk…" : "Opening the test room. Keep the Frame on and look around normally…");
            await scene.StartAsync(paths, options.Unattended, token);
            fps = scene.Current.Fps;
            initialFps=fps;
            if (fps is not (90 or 120)) throw new IOException("The benchmark requires an active 90 or 120 Hz display mode.");
            for (int attempt = 0; !options.Unattended && attempt < 20 && !scene.Current.Tracked; attempt++)
            {
                Report($"Detected {fps} Hz. Waiting for head tracking—wear the Frame and look around…");
                await Task.Delay(500, token);
            }
            if (!options.Unattended && !scene.Current.Tracked) throw new IOException("Head tracking is unavailable. Wear the Frame, wait for tracking, then start again. No bitrate trials were run.");
            await lease.BeginAsync(before);
            if(options.TestRefreshRates)await display.BeginAsync(fps,token);
            // SteamVR may return only 128 entries even when asked for 256. Keep ample
            // overlap at both 90 and 120 Hz, independently of SSH and UI refreshes.
            pcTask = Task.Run(async () =>
            {
                while (!observersStop.IsCancellationRequested)
                { if(Volatile.Read(ref observing)!=0)await compositor.PollAsync(observersStop.Token); await Task.Delay(250, observersStop.Token); }
            });
            var expectedGeneration = before.Generation;
            ulong expectedRevision = 0;
            var expectedPath = headset.Path;
            var videoReadiness = new BenchmarkVideoReadiness();
            async Task EnsureVideo(CancellationToken ct)
            {
                await videoReadiness.EnsureAsync(async () =>
                {
                    var current = await probe.GetSnapshotAsync();
                    if (current.Generation != expectedGeneration || current.PairedCodecTrialPresent || current.RequiresSteamVrRestart)
                        throw new IOException("The encoder session changed outside auto tune. Starting settings will be restored.");
                    return current;
                }, async restartToken =>
                {
                    await RestartRuntime(restartToken, true);
                    expectedGeneration = (await probe.GetSnapshotAsync()).Generation;
                    if (await probe.ReadActiveSendModeAsync() != activeAsync)
                        throw new IOException("The sender mode changed after recovery.");
                }, Report, ct);
            }
            async Task<BenchmarkSample> Sample(BenchmarkCandidate candidate, bool forceHeadset, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                activity.Check();
                if (pcTask.IsFaulted) throw new IOException("A benchmark observer failed; starting settings will be restored.");
                var remote = forceHeadset ? await receiverMonitor.FreshAsync(ct) : receiverMonitor.Current;
                var source = forceHeadset ? await scene.FreshAsync(ct) : scene.Current;
                var encoder = await probe.GetPassiveSnapshotAsync();
                if (expectedRevision != 0 && encoder.PassiveConfigurationRevision != expectedRevision)
                    throw new IOException("Encoder controls changed during measurement. Starting settings will be restored.");
                expectedRevision = encoder.PassiveConfigurationRevision;
                var pc = compositor.Current;
                if (source.Fps != fps) throw new IOException("The headset refresh rate changed during the benchmark. Run auto tune again at the new Hz.");
                if (!options.Unattended && !source.Tracked) throw new IOException("Head tracking was lost. Wear the Frame and start again after tracking returns.");
                // No new output with live, consistent observers is a measured
                // stall. Score its duration instead of calling it a disconnect.
                if (!MeasurementStreamMatches(encoder,expectedGeneration,pc.RuntimePid) ||
                    remote.Path != expectedPath ||
                    !pc.Available || pc.ScenePid != scene.ProcessId ||
                    (DateTimeOffset.Now - pc.ObservedAt).TotalSeconds > 2)
                    throw new IOException("The stream, tracking source or wireless measurements stopped or changed. Reconnect before trying again.");
                var accepted = encoder.EncodingPolicy;
                var expectedQuality=candidate.Quality ?? BenchmarkQuality.From(before);
                bool configuration = accepted is { Observed: true, Pending: false, NeedsNewSession: false, Unsupported: false, InitializationRejected: false }
                    && source.Unattended == options.Unattended
                    && !accepted.Filler
                    && accepted.Width == width && accepted.Height > 0 && encoder.BitDepth == before.BitDepth &&
                    (candidate.ValveReference ? accepted.Requested.IsDefault :
                        accepted.RateControl == policy.RateControl && accepted.MultiPass == (uint)candidate.MultiPass - 1) &&
                    expectedQuality.Matches(encoder) &&
                    encoder.AdvancedEncoding is not ({PendingFlags:not 0} or {RejectedFlags:not 0}) &&
                    encoder.AdvancedEncoding?.Requested == before.AdvancedEncoding?.Requested;
                var live = await Task.Run(() => BenchmarkSettingsLease.ReadLive(paths), ct);
                configuration &= (candidate.ValveReference || Math.Abs(live["targetBandwidth"].GetDouble() - candidate.TargetMbps) < .01) &&
                    live["asyncSend"].GetBoolean() == candidate.AsyncSend && live["automaticBandwidth"].GetBoolean() == candidate.ValveReference &&
                    !live["automaticStreamFormatWidth"].GetBoolean() && live["streamFormatWidth"].GetInt32() == width;
                return new(encoder.TimestampSeconds, encoder.Generation, encoder.EncodedFrames, encoder.EncodedBytes,
                    encoder.EncodeErrors + encoder.LockErrors + encoder.RejectedApiCalls + (accepted?.Errors ?? 0),
                    pc.DroppedFrames, pc.TimedOutEvents, pc.LateRenderFrames, pc.FrameHistoryGaps, pc.AppGpu?.P99Ms ?? 0,
                    remote.Receiver, source, configuration, remote.Path) { PcTimeoutPresentations = pc.TimedOutPresentations };
            }
            async Task<BenchmarkTrial> Measure(BenchmarkCandidate candidate, int seconds, CancellationToken ct)
            {
                stage = "Testing " + candidate.Label;
                expectedRevision = 0;
                // Do not spend the rest of the search testing an inactive encoder after
                // a previous high-load candidate dropped the wireless video link.
                await EnsureVideo(ct);
                int requestedWidth=candidate.Width>0?candidate.Width:initialWidth;
                int requestedFps=candidate.Fps>0?candidate.Fps:initialFps;
                bool rateChanged=requestedFps!=fps, modeChanged=rateChanged || requestedWidth!=width;
                if(modeChanged)
                {
                    if(rateChanged)runtimeHost=await BenchmarkHostConnection.CaptureAsync(preferences,ct);
                    Interlocked.Exchange(ref observing,0);
                    await receiverMonitor.DisposeAsync();await scene.DisposeAsync();
                    if(options.Unattended)await BenchmarkHeadsetReader.SetActivityAsync(preferences,60,ct);
                    if(rateChanged)await display.ApplyAsync(requestedFps,ct);
                    width=requestedWidth;fps=requestedFps;
                }
                var request = candidate.ValveReference ? new EncodingPolicy() : policy.Requested with { MultiPass = candidate.MultiPass };
                await lease.ApplyAsync(candidate, width, request);
                var applied=await probe.GetSnapshotAsync();
                async Task<double> ReadPcHz()
                {
                    try{return await Task.Run(()=>OpenVrSettings.ReadDisplayHz(paths),ct);}
                    catch(IOException){return double.NaN;}
                }
                double pcHz=rateChanged?await ReadPcHz():fps;
                if(modeChanged && !candidate.FreshSession && applied.EncodingPolicy?.NeedsNewSession!=true)
                {
                    Report("Waiting for the live display/encoder change to take effect…");
                    for(int i=0;i<10 && (applied.EncodingPolicy?.Width!=width || !FrameDisplayRate.Matches(pcHz,fps));i++)
                    {
                        await Task.Delay(300,ct);applied=await probe.GetSnapshotAsync();
                        if(rateChanged)pcHz=await ReadPcHz();
                        if(applied.EncodingPolicy?.NeedsNewSession==true)break;
                    }
                }
                if(candidate.FreshSession || !FrameDisplayRate.Matches(pcHz,fps) || applied.EncodingPolicy?.NeedsNewSession==true || modeChanged && applied.EncodingPolicy?.Width!=width)
                {
                    Report(candidate.FreshSession?"Checking this choice in a fresh streaming session. Restarting SteamVR…":
                        applied.EncodingPolicy?.NeedsNewSession==true?"This encoder pass change needs a fresh session. Restarting SteamVR…":
                        !FrameDisplayRate.Matches(pcHz,fps)?$"The Frame switched to {fps} Hz, but the PC has not confirmed that rate. Restarting SteamVR to synchronize them…":
                        "The encoder has not accepted the new resolution. Restarting SteamVR…");
                    await RestartRuntime(ct,true);
                    applied=await probe.GetSnapshotAsync();expectedGeneration=applied.Generation;
                    if(await probe.ReadActiveSendModeAsync()!=activeAsync)throw new IOException("The sender mode changed after restart.");
                }
                else if(modeChanged)
                {
                    receiverMonitor=await StartReceiver(ct);
                    scene=new BenchmarkSceneProcess();await scene.StartAsync(paths,options.Unattended,ct);
                    if(scene.Current.Fps!=fps)throw new IOException("The requested display rate is not active in the test scene.");
                    Interlocked.Exchange(ref observing,1);
                }
                // Exclude reconfiguration, queue drain and scene startup from scoring.
                Report("Warming up the new settings before measurement…");
                await Task.Delay(modeChanged?5000:3000, ct);
                await EnsureVideo(ct);
                var receiverLogStart = await receiverHealth.PollAsync(ct);
                var logStart = await health.PollAsync(ct);
                // Passive telemetry deliberately lacks QP. Full reads bracket the
                // measured window, so helper IPC does not disturb timed samples.
                async Task<EncoderSnapshot?> ReadQuantizer()
                {
                    try{return await probe.GetSnapshotAsync();}
                    catch(IOException){return null;}
                }
                var quantizerStart=await ReadQuantizer();
                async Task<BenchmarkTrial> Finish(BenchmarkTrial trial)
                {
                    var quantizerEnd=await ReadQuantizer();
                    return trial with
                    {
                        Quantizer=quantizerStart is {} start && quantizerEnd is {} end?BenchmarkQuantizer.Between(start,end):null,
                        Logs = BenchmarkLogEvidence.Between(logStart, await health.PollAsync(ct)),
                        ReceiverLogs = BenchmarkReceiverLogEvidence.Between(receiverLogStart, await receiverHealth.PollAsync(ct))
                    };
                }
                var samples = new List<BenchmarkSample> { await Sample(candidate, true, ct) };
                var began = Stopwatch.StartNew();
                _progress.Measuring(0,seconds);
                while (began.Elapsed.TotalSeconds < seconds)
                {
                    await Task.Delay(1000, ct);
                    samples.Add(await Sample(candidate, false, ct));
                    _progress.Measuring(began.Elapsed.TotalSeconds,seconds);
                    progress($"{fps} Hz · {candidate.Label}\nMeasuring {Math.Min(seconds, (int)began.Elapsed.TotalSeconds)} / {seconds} seconds");
                    if (samples.Count >= 4)
                    {
                        var interim = WirelessBenchmark.Evaluate(candidate, samples, fps, (int)(samples[^1].Seconds - samples[0].Seconds), completeWindow: false);
                        // Finish comparable windows despite hitches. Only invalid
                        // measurements stop early; performance is scored at the end.
                        if (!interim.MeasurementValid) return await Finish(interim);
                    }
                }
                // Close with a fresh headset read, not a cached counter from before the end.
                await Task.Delay(250, ct);
                samples.Add(await Sample(candidate, true, ct));
                var measured = WirelessBenchmark.Evaluate(candidate, samples, fps, seconds);
                return await Finish(measured);
            }
            var completedTrials = new List<BenchmarkTrial>();
            BenchmarkPlan plan=testPlan ?? ((o,c,s,m,p,t,done,q,r)=>BenchmarkExplorer.RunAsync(o,c,s,m,p,t,done,q,initialWidth,initialFps,_progress));
            result = await plan(options, ceiling, activeAsync, Measure, Report, token, trial =>
            {
                completedTrials.Add(trial);
                result = BenchmarkExplorer.Recommend(completedTrials.ToArray(),options,activeAsync,"Search still running",initialWidth,initialFps);
                File.WriteAllText(ReportPath, JsonSerializer.Serialize(new BenchmarkReport(4, started, "Running", options,
                    expectedPath, initialWidth, initialFps, null, null, result, null){Progress=Progress}));
            }, BenchmarkQuality.From(before),null);
            outcome="Recommendations ready";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { error="Stopped early";outcome="Partial recommendations";result=BenchmarkExplorer.Recommend(result?.Trials??[],options,activeAsync,error,initialWidth,initialFps>0?initialFps:90); }
        catch (Exception ex)
        {
            error = stage + ": " + ex.Message;
            result=BenchmarkExplorer.Recommend(result?.Trials??[],options,activeAsync,error,initialWidth,initialFps>0?initialFps:90);
            outcome="Partial recommendations";
        }
        finally
        {
            observersStop.Cancel();
            foreach (var observer in new[] { pcTask }.OfType<Task>())
                try { await observer; } catch (Exception) { }
            // An observer shutdown error must not skip settings/power recovery.
            foreach(var observer in new IAsyncDisposable[]{receiverMonitor,scene})
                try { await observer.DisposeAsync(); }
                catch(Exception cleanupError)
                { outcome="Observer cleanup failed";error=(error is null?"":error+" ")+cleanupError.Message;Recommended=null; }
            connection.Release();
            _progress.Restoring(error is not null);
            Report("Restoring starting settings…");
            try
            {
                if(lease.Pending)
                {
                    if(options.Unattended)await BenchmarkHeadsetReader.SetActivityAsync(preferences,60,CancellationToken.None);
                    await BenchmarkRuntimeRestart.EnsureConnectionAsync(paths,preferences,probe,lease,Report,CancellationToken.None,runtimeHost);
                }
                // Restore Hz while the encoder journal still owns the runtime.
                await display.RestoreAsync(()=>RestartRuntime(CancellationToken.None,false),CancellationToken.None);
                await lease.RestoreAsync(()=>RestartRuntime(CancellationToken.None,false));
            }
            catch (Exception restoreError)
            {
                outcome = "Recovery required"; error = (error is null ? "" : error + " ") + restoreError.Message;
                RecoveryRequired = true;
                Recommended = null;
            }
            if (ownsPower)
                try { await power.RestoreAsync(); }
                catch (Exception powerError) { outcome = "Power recovery required"; error = (error is null ? "" : error + " ") + "Power restore: " + powerError.Message; Recommended = null; RecoveryRequired = true; }
            // Saving the recommended preset never overwrites or applies the user's starting setup.
            if (result is not null && !RecoveryRequired)
            {
                var choices=new List<BenchmarkSavedOption>();
                foreach(var option in result.Recommendations)
                {
                    var profile=WirelessBenchmark.Profile(baseline,option.Candidate,option.Candidate.Width,option.Candidate.Fps);
                    string name=$"Auto {option.Name} {option.Candidate.Width}px {option.Candidate.Fps}Hz "+started.ToString("MM-dd HH-mm-ss");
                    try { await new PresetStore().SaveAsync(name,profile);choices.Add(new(name,profile,option)); }
                    catch(Exception saveError)
                    {
                        outcome="Preset save failed";error=saveError.Message;
                        choices.Add(new(name,profile,option with {Note=option.Note+" Saving failed; this option can still be loaded now."}));
                    }
                }
                Choices=choices;Recommended=choices.FirstOrDefault()?.Profile;PresetName=choices.FirstOrDefault()?.Name;
            }
            _progress.Finish(outcome=="Recommendations ready",RecoveryRequired,error??result?.Message??outcome);
            await File.WriteAllTextAsync(ReportPath, JsonSerializer.Serialize(new BenchmarkReport(4, started, outcome, options,
                headset.Path, initialWidth, initialFps, PresetName, Recommended, result, error){Choices=Choices,Progress=Progress}, new JsonSerializerOptions { WriteIndented = true }));
        }
        if(RecoveryRequired)throw new IOException(error+" Restore starting settings before applying any option.");
        return result!;
    }

    internal static void ValidateModeComparison(BenchmarkOptions options, bool patched)
    {
        if (!patched && (options.TestResolutions || options.TestRefreshRates))
            throw new IOException("Resolution/refresh-rate comparison requires the 500 Mbps / HEVC 6.2 patch. Install it under Patches & Recovery and restart SteamVR, or turn off resolution and refresh-rate comparisons to test the current display mode. The patch also raises the HEVC level; lowering the bitrate alone does not do that.");
    }
}
