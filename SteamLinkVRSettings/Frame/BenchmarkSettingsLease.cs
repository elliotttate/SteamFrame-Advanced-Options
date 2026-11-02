using System.Text.Json;
using SteamLinkVRSettings.Services;

namespace SteamLinkVRSettings.Frame;

internal sealed record BenchmarkLeaseData(string SteamConfig, string Generation, EncodingPolicy OriginalPolicy,
    EncodingPolicy ExpectedPolicy, EncodingPolicy PreviousPolicy, Dictionary<string, JsonElement> OriginalOverrides,
    Dictionary<string, JsonElement> Expected, Dictionary<string, JsonElement> Previous,
    BenchmarkQuality? OriginalQuality = null, BenchmarkQuality? ExpectedQuality = null, BenchmarkQuality? PreviousQuality = null,
    AdvancedEncoding? OriginalAdvanced = null, AdvancedEncoding? PreviousAdvanced = null,
    bool ExpectedAutomaticOwner = false, bool PreviousAutomaticOwner = false);

/// <summary>Durable before-write journal, including absent overrides. Recovery touches only owned keys.</summary>
internal sealed class BenchmarkSettingsLease(SteamPaths paths, EncoderProbe probe, string? journalPath = null, IBenchmarkSettings? testSettings = null)
{
    internal static readonly string[] Keys = ["targetBandwidth", "automaticBandwidth", "automaticStreamFormatWidth", "streamFormatWidth", "asyncSend"];
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamLinkVRSettings", "benchmark-recovery.json");
    private readonly string _path = journalPath ?? DefaultPath;
    private readonly IBenchmarkSettings _settings = testSettings ?? new LiveBenchmarkSettings(paths, probe);
    private BenchmarkLeaseData? _data;
    private bool _beginFailed;
    public bool Pending => File.Exists(_path);

    internal static Dictionary<string, JsonElement> ReadLive(SteamPaths paths)
    {
        var basic = OpenVrSettings.ReadBenchmarkControls(paths);
        return new Dictionary<string, object?>
        {
            ["targetBandwidth"] = basic.Target, ["automaticBandwidth"] = basic.Automatic,
            ["automaticStreamFormatWidth"] = basic.AutomaticWidth, ["streamFormatWidth"] = basic.StreamWidth,
            ["asyncSend"] = basic.AsyncSend
        }.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));
    }
    public async Task BeginAsync(EncoderSnapshot snapshot)
    {
        _beginFailed = true;
        if (Pending) throw new IOException("Recover the interrupted benchmark before starting another one.");
        var saved = await _settings.Saved();
        var live = await _settings.Read();
        _data = new(paths.Config, snapshot.Generation, snapshot.EncodingPolicy!.Requested, snapshot.EncodingPolicy.Requested, snapshot.EncodingPolicy.Requested,
            saved.Where(p => Keys.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value), live, live,
            BenchmarkQuality.From(snapshot), BenchmarkQuality.From(snapshot), BenchmarkQuality.From(snapshot),
            snapshot.AdvancedEncoding?.Requested, snapshot.AdvancedEncoding?.Requested);
        await SaveAsync(create: true);
        _beginFailed = false;
    }
    public async Task ApplyAsync(BenchmarkCandidate candidate, int width, EncodingPolicy policy)
    {
        var data = _data ?? throw new InvalidOperationException("No benchmark recovery journal.");
        var before = await _settings.Snapshot();
        ValidateEncoderOwnership(data, before);
        var live = await _settings.Read();
        if (Keys.Any(k => !MatchesControl(k,live,data.Expected,data.ExpectedAutomaticOwner))) throw new IOException("Stream settings changed outside the benchmark.");
        var writes = new Dictionary<string, JsonElement>(live)
        {
            ["targetBandwidth"] = JsonSerializer.SerializeToElement((double)candidate.TargetMbps),
            ["automaticBandwidth"] = JsonSerializer.SerializeToElement(candidate.ValveReference),
            ["automaticStreamFormatWidth"] = JsonSerializer.SerializeToElement(false),
            ["streamFormatWidth"] = JsonSerializer.SerializeToElement(width),
            ["asyncSend"] = JsonSerializer.SerializeToElement(candidate.AsyncSend)
        };
        var quality=candidate.Quality ?? data.ExpectedQuality;
        if(quality is {IsValid:false})throw new ArgumentException("Invalid candidate quality offsets.");
        _data = data with { Expected = writes, Previous = live, ExpectedPolicy = policy, PreviousPolicy = data.ExpectedPolicy,
            ExpectedQuality=quality, PreviousQuality=data.ExpectedQuality,
            ExpectedAutomaticOwner=candidate.ValveReference,PreviousAutomaticOwner=data.ExpectedAutomaticOwner };
        await SaveAsync(); // Survives failure after any individual setting write.
        await _settings.Write(writes);
        await _settings.Policy(policy);
        if(quality is not null && !quality.Matches(before))await _settings.Quality(quality);
        await _settings.Refresh();
    }
    public async Task RestoreAsync(Func<Task>? reconnect = null)
    {
        if (_beginFailed) return; // A failed Begin never owns another instance's journal.
        if (!Pending) return;
        var data = _data ?? await ReadDataAsync();
        _data=data;
        var snapshot = await _settings.Snapshot();
        if (snapshot.PairedCodecTrialPresent) throw new IOException("Return to HEVC before recovering benchmark settings.");
        var live = await _settings.Read();
        var owned = OwnedKeys(data, live);
        var writes = data.OriginalOverrides.Where(p => owned.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        await _settings.Write(writes);
        await _settings.Remove(owned.Except(writes.Keys));
        var restoredControls=await _settings.Read();
        await RestoreHelperAsync(data,snapshot,restoredControls,owned,writes,reconnect);
    }
    private async Task<BenchmarkLeaseData> ReadDataAsync()
    {
        if (new FileInfo(_path).Length > 65536) throw new IOException("Benchmark recovery journal is invalid.");
        var data = JsonSerializer.Deserialize<BenchmarkLeaseData>(await File.ReadAllTextAsync(_path)) ?? throw new IOException("Benchmark recovery journal is incomplete.");
        if (!Path.GetFullPath(data.SteamConfig).Equals(Path.GetFullPath(paths.Config), StringComparison.OrdinalIgnoreCase) ||
            data.Expected.Keys.Except(Keys).Any() || data.Previous.Keys.Except(Keys).Any() || data.OriginalOverrides.Keys.Except(Keys).Any() ||
            Keys.Any(k => !data.Expected.ContainsKey(k) || !data.Previous.ContainsKey(k)) || !data.OriginalPolicy.IsValid ||
            new[]{data.OriginalQuality,data.ExpectedQuality,data.PreviousQuality}.Any(q=>q is {IsValid:false}))
            throw new IOException("Benchmark recovery journal does not match this installation.");
        foreach (var dictionary in new[] { data.Expected, data.Previous, data.OriginalOverrides })
            foreach (var value in StockProfiles.Deserialize(dictionary)) SettingOwnership.ValidateValue(value.Key, value.Value);
        data.OriginalAdvanced?.Validate();data.PreviousAdvanced?.Validate();
        return data;
    }
    private async Task RestoreHelperAsync(BenchmarkLeaseData data,EncoderSnapshot snapshot,Dictionary<string,JsonElement> restoredControls,
        HashSet<string> owned,Dictionary<string,JsonElement> writes,Func<Task>? reconnect)
    {
        if (snapshot.Installed && snapshot.Generation == data.Generation &&
            (snapshot.EncodingPolicy?.Requested == data.ExpectedPolicy || snapshot.EncodingPolicy?.Requested == data.PreviousPolicy))
        {
            var restored = await _settings.Policy(data.OriginalPolicy);
            if (restored.EncodingPolicy?.Requested != data.OriginalPolicy) throw new IOException("Encoder policy recovery is still pending.");
        }
        if(snapshot.Installed && snapshot.Generation==data.Generation && data.OriginalQuality is {} originalQuality &&
            (data.ExpectedQuality?.Matches(snapshot)==true || data.PreviousQuality?.Matches(snapshot)==true))
        {
            var restored=await _settings.Quality(originalQuality);
            if(!originalQuality.Matches(restored))throw new IOException("Quality-offset recovery is still pending.");
        }
        if(snapshot.Installed && snapshot.Generation==data.Generation && data.OriginalAdvanced is {} originalAdvanced &&
            (snapshot.AdvancedEncoding?.Requested==originalAdvanced || snapshot.AdvancedEncoding?.Requested==data.PreviousAdvanced))
            await _settings.Advanced(originalAdvanced);
        await _settings.Refresh();
        // Refresh momentarily writes the bitrate target. Do not leave a new
        // explicit override when the user originally inherited Valve's default.
        var afterRefresh=await _settings.Read();
        await _settings.Remove(owned.Except(writes.Keys).Where(k=>Same(afterRefresh[k],restoredControls[k])));
        var recovered=await _settings.Snapshot();
        if(recovered.Generation==data.Generation && recovered.EncodingPolicy is {NeedsNewSession:true} pending && pending.Requested==data.OriginalPolicy)
        {
            if(reconnect is null)throw new IOException("Original encoder mode requires a reconnect. Reconnect Frame, then restore starting settings.");
            _data=data with {ExpectedPolicy=data.OriginalPolicy,PreviousPolicy=recovered.EncodingPolicy.Requested,
                ExpectedQuality=BenchmarkQuality.From(recovered),PreviousQuality=BenchmarkQuality.From(recovered),Expected=await _settings.Read(),ExpectedAutomaticOwner=true};
            await SaveAsync();
            await reconnect();recovered=await _settings.Snapshot();
            if(recovered.Generation!=_data!.Generation || recovered.EncodingPolicy is not {Pending:false,NeedsNewSession:false} accepted || accepted.Requested!=data.OriginalPolicy ||
                data.OriginalQuality is {} q && !q.Matches(recovered))throw new IOException("The original encoder mode is not active yet.");
        }
        File.Delete(_path); _data = null;
    }
    public async Task AdoptRestartAsync(EncoderSnapshot before,EncoderSnapshot replacement)
    {
        var data=_data ?? throw new IOException("No benchmark journal owns this restart.");
        if(before.Generation!=data.Generation || before.EncodingPolicy?.Requested!=data.ExpectedPolicy ||
            data.ExpectedQuality?.Matches(before)!=true || replacement.ProcessId<=0 || replacement.ProcessId==before.ProcessId ||
            !replacement.Installed || replacement.PairedCodecTrialPresent || replacement.Generation==before.Generation)
            throw new IOException("The replacement SteamVR helper does not match the benchmark restart.");
        // This callback runs as soon as the helper loads, before the headset's
        // OpenVR interface is necessarily available. Arm reconnect-only controls
        // now, then verify persisted driver controls once the stream is ready.
        _data=data with {Generation=replacement.Generation,PreviousPolicy=replacement.EncodingPolicy?.Requested ?? new(),
            PreviousQuality=BenchmarkQuality.From(replacement),PreviousAdvanced=replacement.AdvancedEncoding?.Requested ?? new()};
        await SaveAsync();
    }
    public async Task CheckControlsAsync()
    {
        if(_beginFailed)throw new IOException("This instance does not own the benchmark journal.");
        var data=_data ??= await ReadDataAsync();
        var live=await _settings.Read();
        if(Keys.Any(k=>!MatchesControl(k,live,data.Expected,data.ExpectedAutomaticOwner)))throw new IOException("Stream settings changed outside the benchmark.");
    }
    public async Task CheckEncoderOwnershipAsync()
    {
        if(_beginFailed)throw new IOException("This instance does not own the benchmark journal.");
        var data=_data ??= await ReadDataAsync();
        var snapshot=await _settings.Snapshot();
        ValidateEncoderOwnership(data, snapshot);
    }
    // Report the failed invariant without claiming that another application caused it.
    // A failed encoder reconfiguration can also replace/reset the helper's state.
    internal static void ValidateEncoderOwnership(BenchmarkLeaseData data, EncoderSnapshot snapshot)
    {
        string? reason = !snapshot.Installed ? "The encoder monitor is no longer installed."
            : snapshot.Generation != data.Generation ? $"The encoder session changed (expected {data.Generation}, observed {snapshot.Generation})."
            : snapshot.PairedCodecTrialPresent ? "A paired codec session now owns the encoder."
            : snapshot.EncodingPolicy?.Requested != data.ExpectedPolicy
                ? $"The encoder request changed (expected {data.ExpectedPolicy.Description}; observed {snapshot.EncodingPolicy?.Requested.Description ?? "unavailable"})."
            : data.ExpectedQuality is { } quality && !quality.Matches(snapshot)
                ? $"The quality request changed (expected {quality.Label}, enabled {quality.Enabled}; observed gaze {snapshot.CenterOffset}, periphery {snapshot.PeripheralOffset}, enabled {snapshot.Enabled})."
            : null;
        if (reason is not null)
            throw new IOException(reason + $" Last NVENC status: {snapshot.LastNvencStatus}. Starting-settings recovery remains available.");
    }
    internal static HashSet<string> OwnedKeys(BenchmarkLeaseData data, Dictionary<string, JsonElement> live) => Keys
        .Where(k => live.ContainsKey(k) && (MatchesControl(k,live,data.Expected,data.ExpectedAutomaticOwner) || MatchesControl(k,live,data.Previous,data.PreviousAutomaticOwner))).ToHashSet();
    // Valve owns targetBandwidth while automatic bandwidth is enabled. Its
    // adaptation must neither invalidate the reference nor evade restoration.
    internal static bool MatchesControl(string key,Dictionary<string,JsonElement> live,Dictionary<string,JsonElement> expected,bool automaticOwner) =>
        automaticOwner && key=="targetBandwidth" && live["automaticBandwidth"].GetBoolean() && expected["automaticBandwidth"].GetBoolean() || Same(live[key],expected[key]);
    internal static bool Same(JsonElement a, JsonElement b) => a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number
        ? Math.Abs(a.GetDouble() - b.GetDouble()) < .01 : a.ToString() == b.ToString();
    private async Task SaveAsync(bool create = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!); var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, _data);
                await stream.FlushAsync(); stream.Flush(flushToDisk: true);
            }
            // The first journal must not replace another app instance's active test.
            File.Move(temporary, _path, overwrite: !create);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal interface IBenchmarkSettings
{
    Task<Dictionary<string, JsonElement>> Saved();
    Task<Dictionary<string, JsonElement>> Read();
    Task Write(Dictionary<string, JsonElement> values);
    Task Remove(IEnumerable<string> keys);
    Task<EncoderSnapshot> Snapshot();
    Task<EncoderSnapshot> Policy(EncodingPolicy value);
    Task<EncoderSnapshot> Quality(BenchmarkQuality value);
    Task<EncoderSnapshot> Advanced(AdvancedEncoding value);
    Task Refresh();
}

internal sealed class LiveBenchmarkSettings(SteamPaths paths, EncoderProbe probe) : IBenchmarkSettings
{
    public async Task<Dictionary<string, JsonElement>> Saved() => (await new SettingsService { UserConfigPath = paths.Config, DefaultsPath = paths.Defaults }.LoadUserOverridesAsync())
        .ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));
    public Task<Dictionary<string, JsonElement>> Read() => Task.Run(() => BenchmarkSettingsLease.ReadLive(paths));
    public async Task Write(Dictionary<string, JsonElement> values)
    {
        var writes=StockProfiles.Deserialize(values);
        if(writes.TryGetValue("automaticBandwidth",out var automatic))
        {
            await Task.Run(()=>OpenVrSettings.Apply(paths,new Dictionary<string,object?> {{"automaticBandwidth",automatic}}));
            await Task.Delay(300); // Drain an in-flight automatic target update before setting a manual target.
            writes.Remove("automaticBandwidth");
        }
        await Task.Run(()=>OpenVrSettings.Apply(paths,writes));
    }
    public Task Remove(IEnumerable<string> keys) => Task.Run(() => OpenVrSettings.RemoveKeys(paths, keys));
    public Task<EncoderSnapshot> Snapshot() => probe.GetSnapshotAsync();
    public Task<EncoderSnapshot> Policy(EncodingPolicy value) => probe.SetEncodingPolicyAsync(value);
    public Task<EncoderSnapshot> Quality(BenchmarkQuality value) => probe.SetQualityAsync(value.Enabled,value.GazeOffset,value.PeripheralOffset);
    public Task<EncoderSnapshot> Advanced(AdvancedEncoding value) => probe.SetAdvancedEncodingAsync(value);
    public Task Refresh() => Task.Run(() => OpenVrSettings.RefreshEncoder(paths));
}
