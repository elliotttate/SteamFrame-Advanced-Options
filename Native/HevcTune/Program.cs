using System.Text.Json;
using SteamLinkVRSettings.Frame;

if (args.Length == 1 && args[0] == "--sender")
{
    var observer = new EncoderProbe();
    var snapshot = await observer.GetSnapshotAsync();
    if (!snapshot.Installed || !snapshot.Active || snapshot.Codec != "HEVC")
        throw new IOException("Start monitoring an active HEVC stream in the app before checking its sender.");
    var asynchronous = await observer.ReadActiveSendModeAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { snapshot.ProcessId, AsyncSend = asynchronous }));
    return;
}

if (args.Length == 3 && args[0] == "--timing")
{
    if (!int.TryParse(args[1], out var seconds) || seconds is < 10 or > 300)
        throw new ArgumentException("Timing duration must be 10..300 seconds");
    var output = Path.GetFullPath(args[2]);
    var timing = new CompositorTiming(SteamPaths.Current, Path.GetDirectoryName(output));
    var samples = new List<CompositorTimingSnapshot>();
    try
    {
        for (var i = 0; i <= seconds; ++i)
        {
            if (i > 0) await Task.Delay(1000);
            var sample = await timing.PollAsync();
            samples.Add(sample);
            if (!sample.Available) throw new InvalidOperationException(sample.Message);
            if (i % 10 == 0)
                Console.WriteLine(JsonSerializer.Serialize(new { sample.ScenePid, sample.Presents, sample.DroppedFrames,
                    sample.LateRenderFrames, sample.AppGpu, sample.TotalGpu, sample.ClientInterval, sample.TargetFrameMs }));
        }
    }
    finally
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(samples));
    }
    return;
}

// Read-only without arguments. Apply only through an explicit, reviewable file;
// reuse the existing guarded monitor instead of injecting another helper.
var start = args.Length == 1 && args[0] == "--start";
var arm = args.Length == 2 && args[0] == "--arm";
if (!start && !arm && (args.Length is not (0 or 2) || args.Length == 2 && args[0] != "--apply"))
    throw new ArgumentException("Use no arguments to inspect, --sender to verify the active sender, --start to start the guarded monitor, --arm REQUEST.json before connecting, or --apply REQUEST.json");
var probe = new EncoderProbe();
var before = start || arm ? await probe.StartAsync() : await probe.GetSnapshotAsync();
if (start)
{
    Console.WriteLine(JsonSerializer.Serialize(new { Event = "start", Snapshot = before }));
    if (!before.Installed) throw new InvalidOperationException(before.Message);
    return;
}
if (args.Length == 0)
{
    Console.WriteLine(JsonSerializer.Serialize(new { Event = "before", Snapshot = before }));
    return; // Inactive/mismatched sessions still have useful read-only diagnostics.
}
if (!before.Installed || before.PairedCodecTrialPresent ||
    (arm ? before.Active : !before.Active || before.Codec != "HEVC"))
    throw new InvalidOperationException("Start the app's HEVC monitor before applying a trial");
Console.WriteLine(JsonSerializer.Serialize(new { Event = "before", Snapshot = before }));
var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(args[1])) ?? throw new InvalidDataException("Empty request");
request.Advanced?.Validate();
if (request.Policy is { IsValid: false }) throw new ArgumentException("Invalid encoding policy");
if (request.QualityEnabled.HasValue &&
    (!FoveationOffsets.IsValid(request.CenterOffset, FoveationOffsets.MaximumGaze, FoveationOffsets.MinimumGaze) ||
     !FoveationOffsets.IsValid(request.PeripheralOffset, FoveationOffsets.MaximumPeripheral)))
    throw new ArgumentException("Invalid gaze quality offsets");
if (request.Advanced is not null) await probe.SetAdvancedEncodingAsync(request.Advanced);
if (request.QualityEnabled.HasValue) await probe.SetQualityAsync(request.QualityEnabled.Value, request.CenterOffset, request.PeripheralOffset);
if (request.Policy is not null) await probe.SetEncodingPolicyAsync(request.Policy);
if (arm)
{
    Console.WriteLine(JsonSerializer.Serialize(new { Event = "armed", Snapshot = await probe.GetSnapshotAsync() }));
    return; // Initialization readback must be checked after the Frame connects.
}
OpenVrSettings.RefreshEncoder(SteamPaths.Current);
await Task.Delay(1000);
var after = await probe.GetSnapshotAsync();
Console.WriteLine(JsonSerializer.Serialize(new { Event = "after", Snapshot = after }));
if (!after.Active || after.Generation != before.Generation || after.EncodeErrors > before.EncodeErrors ||
    after.LockErrors > before.LockErrors || (after.AdvancedEncoding?.RejectedFlags ?? 0) != 0 ||
    (request.Policy is not null && after.EncodingPolicy?.Applied != request.Policy))
    throw new InvalidOperationException("The tuning trial did not remain active without errors; inspect the readback");

sealed record Request(AdvancedEncoding? Advanced, EncodingPolicy? Policy, bool? QualityEnabled,
                      int CenterOffset = 0, int PeripheralOffset = 0);
