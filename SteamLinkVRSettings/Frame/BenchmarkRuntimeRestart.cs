namespace SteamLinkVRSettings.Frame;

/// <summary>One bounded runtime restart when NVENC cannot change rate control in place.</summary>
internal static class BenchmarkRuntimeRestart
{
    public static async Task<EncoderSnapshot> RunAsync(SteamPaths paths, FramePreferences preferences, EncoderProbe probe,
        BenchmarkSettingsLease lease, Action<string> progress, CancellationToken token,string? capturedHost=null)
    {
        // A dropped headset can make Background OpenVR initialization fail (215).
        // Verify the owned helper first, then revive the same host before using settings APIs.
        await lease.CheckEncoderOwnershipAsync();
        var host=capturedHost??await BenchmarkHostConnection.CaptureAsync(preferences,token);
        await EnsureConnectionAsync(paths,preferences,probe,lease,progress,token,host);
        await lease.CheckControlsAsync();
        FrameReconnectTuning? expected=null;
        var result=await FrameReconnectRecovery.RunAsync(probe.GetSnapshotAsync,
            pid=>SteamVrRuntime.StartOrRestartForReconnectAsync(paths,pid),async(before,ready)=>
            {
                expected=new(before.Enabled,before.CenterOffset,before.PeripheralOffset,before.EncodingPolicy!.Requested,
                    before.AdvancedEncoding?.Requested ?? new());
                var current=await probe.StartAsync();
                // Record the replacement helper before writing any requests into it.
                await lease.AdoptRestartAsync(before,current);
                await probe.SetQualityAsync(before.Enabled,before.CenterOffset,before.PeripheralOffset);
                await probe.SetEncodingPolicyAsync(before.EncodingPolicy!.Requested);
                current=await probe.SetAdvancedEncodingAsync(before.AdvancedEncoding?.Requested ?? new());
                await BenchmarkHostConnection.ResumeAsync(preferences,host,token);
                return current;
            },progress,token,resumeVideoOnce:async ct=>
            {
                await lease.CheckEncoderOwnershipAsync();
                progress("SteamVR restarted without fresh video. Reconnecting the same Frame client once before measurement…");
                await BenchmarkHostConnection.ResumeAsync(preferences,host,ct);
            });
        var snapshot=result.Snapshot;
        if(!result.VideoResumed || expected?.RequestsRestored(snapshot)!=true ||
            snapshot.AdvancedEncoding is {PendingFlags:not 0} or {RejectedFlags:not 0} ||
            snapshot.EncodingPolicy is not {Observed:true,Pending:false,NeedsNewSession:false,Unsupported:false,InitializationRejected:false})
            throw new IOException($"The requested encoder mode did not activate after one SteamVR restart (fresh video: {result.VideoResumed}; {snapshot.EncodingPolicy?.StatusMessage ?? snapshot.Message}). Starting-settings recovery remains available.");
        await OpenVrSettings.WaitUntilAvailableAsync(paths);
        await lease.CheckControlsAsync();
        return snapshot;
    }

    internal static async Task EnsureConnectionAsync(SteamPaths paths,FramePreferences preferences,EncoderProbe probe,
        BenchmarkSettingsLease lease,Action<string> progress,CancellationToken token,string? capturedHost)
    {
        if(!(await probe.GetSnapshotAsync()).Active)
        {
            await lease.CheckEncoderOwnershipAsync();
            var host=capturedHost??await BenchmarkHostConnection.CaptureAsync(preferences,token);
            progress("Resuming the disconnected Frame before restoring the SteamVR connection…");
            await BenchmarkHostConnection.ResumeAsync(preferences,host,token);
        }
        await OpenVrSettings.WaitUntilAvailableAsync(paths);
    }
}

internal static class BenchmarkHostConnection
{
    public static async Task<string> CaptureAsync(FramePreferences preferences,CancellationToken token)
    {
        const string expression="""
(() => {
 const streams=RemotePlayStore_SteamUI.remoteClientStreams.filter(s=>String(s.m_gameid)==="250820");
 if(streams.length!==1)throw new Error("One active VR host is required");
 const host=String(streams[0].m_clientid);
 const matches=RemotePlayStore_SteamUI.devices.filter(d=>String(d.clientId)===host && d.bCanStreamVR && d.bHasLocalConnection);
 if(matches.length!==1)throw new Error("The current VR host is unavailable");return host;
})()
""";
        return System.Text.Json.JsonSerializer.Deserialize<string>(await Evaluate(preferences,expression,token))
            ?? throw new IOException("The current VR host was not identified.");
    }
    public static async Task ResumeAsync(FramePreferences preferences,string host,CancellationToken token)
    {
        string identity=System.Text.Json.JsonSerializer.Serialize(host);
        string expression="""
(() => {
 const streams=RemotePlayStore_SteamUI.remoteClientStreams.filter(s=>String(s.m_gameid)==="250820");
 if(streams.some(s=>String(s.m_clientid)!==HOST))throw new Error("Another VR host is active");
 const hosts=RemotePlayStore_SteamUI.devices.filter(d=>String(d.clientId)===HOST && d.bCanStreamVR && d.bHasLocalConnection);
 if(hosts.length!==1)throw new Error("The same VR host is no longer available");
 return true;
})()
""".Replace("HOST",identity);
        await RestartClientAsync(async ct =>
        {
            if(!System.Text.Json.JsonSerializer.Deserialize<bool>(await Evaluate(preferences,expression,ct)))
                throw new IOException("The Frame did not identify the selected VR host.");
        }, ct => FrameStreamControl.StopAsync(preferences,ct),
            ct => FrameStreamControl.StartAsync(preferences,host,ct),token);
    }
    internal static async Task RestartClientAsync(Func<CancellationToken,Task> validateHost,
        Func<CancellationToken,Task> stop,Func<CancellationToken,Task> start,CancellationToken token,
        Func<CancellationToken,Task>? settle=null)
    {
        token.ThrowIfCancellationRequested();
        await validateHost(token); // Never terminate a stream belonging to another PC.
        await stop(token);
        await (settle?.Invoke(token) ?? Task.Delay(FrameStreamControl.Settle,token));
        token.ThrowIfCancellationRequested();
        await validateHost(token);
        await start(token); // A stale receiver needs a stop/start, not another StreamGame request.
    }
    private static Task<string> Evaluate(FramePreferences preferences,string expression,CancellationToken token) =>
        FrameSsh.ExecuteAsync(preferences,FrameLinkQuery.SessionScript+"\nwith FrameUiConnection() as ui:\n print(json.dumps(ui.evaluate("+
            System.Text.Json.JsonSerializer.Serialize(expression)+")))",token);
}

internal sealed class BenchmarkOpenVrConnection(SteamPaths paths) : IDisposable
{
    private IDisposable? _connection=OpenVrSettings.RetainConnection(paths);
    public void Release() { _connection?.Dispose();_connection=null; }
    public void Retain() { _connection ??= OpenVrSettings.RetainConnection(paths); }
    public void Dispose() => Release();
}
