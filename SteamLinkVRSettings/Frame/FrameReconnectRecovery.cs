namespace SteamLinkVRSettings.Frame;

public sealed record FrameReconnectResult(EncoderSnapshot Snapshot, bool VideoResumed);

/// <summary>One explicit recovery attempt. Never called by monitoring or polling.</summary>
public static class FrameReconnectRecovery
{
    public static async Task<FrameReconnectResult> RunAsync(
        Func<Task<EncoderSnapshot>> read, Func<int, Task> restartOrStart,
        Func<EncoderSnapshot, EncoderSnapshot, Task<EncoderSnapshot>> prepare,
        Action<string> progress, CancellationToken token = default,
        int driverAttempts = 80, int videoAttempts = 60,
        Func<CancellationToken, Task>? wait = null,
        Func<CancellationToken, Task>? resumeVideoOnce = null)
    {
        wait ??= cancellation => Task.Delay(500, cancellation);
        token.ThrowIfCancellationRequested();
        var before = await read();
        CheckOwnership(before);
        token.ThrowIfCancellationRequested();
        progress("Restarting SteamVR once to reconnect the Frame. A running VR game may close…");
        await restartOrStart(before.ProcessId);
        progress("Waiting for the new SteamVR streaming driver…");
        EncoderSnapshot? ready = null;
        for (var attempt = 0; attempt < driverAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var current = await read();
            CheckOwnership(current);
            if (current.ProcessId > 0 && current.ProcessId != before.ProcessId && !current.WaitingForDriver)
            {
                if (!current.Supported || current.RequiresSteamVrRestart)
                    throw new InvalidOperationException("SteamVR restarted, but its encoder helper is unavailable. " + current.Message);
                ready = current;
                break;
            }
            await wait(token);
        }
        if (ready is null)
            throw new TimeoutException("SteamVR did not finish restarting. No second restart was attempted. Start SteamVR manually, then reconnect from the Frame's PC streaming menu.");

        token.ThrowIfCancellationRequested();
        progress("Restoring applied encoder tuning and waiting for fresh video…");
        var previous = await prepare(before, ready);
        CheckReplacement(previous, ready.ProcessId);
        if (!previous.Installed)
            throw new InvalidOperationException("SteamVR restarted, but video monitoring could not start. " + previous.Message);
        for (var connectionAttempt = 0; ; connectionAttempt++)
        {
            for (var attempt = 0; attempt < videoAttempts; attempt++)
            {
                await wait(token);
                token.ThrowIfCancellationRequested();
                var current = await read();
                CheckReplacement(current, ready.ProcessId);
                if (FreshVideo(previous, current)) return new(current, true);
                previous = current;
            }
            // A completed process restart is not evidence that the headset reconnected.
            // Retry only the client connection, once, before any measurement starts.
            if (connectionAttempt != 0 || resumeVideoOnce is null) return new(previous, false);
            token.ThrowIfCancellationRequested();
            await resumeVideoOnce(token);
            previous = await read();
            CheckReplacement(previous, ready.ProcessId);
        }
    }

    private static void CheckOwnership(EncoderSnapshot snapshot)
    {
        if (snapshot.PairedCodecTrialPresent)
            throw new InvalidOperationException("A paired codec session owns the stream. Switch back to HEVC with Apply and reconnect before using Reconnect Frame.");
    }

    private static void CheckReplacement(EncoderSnapshot snapshot, int processId)
    {
        CheckOwnership(snapshot);
        if (snapshot.ProcessId != processId || snapshot.RequiresSteamVrRestart)
            throw new InvalidOperationException("The SteamVR session changed during recovery. No further restart was attempted.");
    }

    internal static bool FreshVideo(EncoderSnapshot previous, EncoderSnapshot current) =>
        current.Installed && current.Active && current.CodecObserved && !current.CodecReadFailed &&
        current.Codec == "HEVC" && !string.IsNullOrEmpty(current.Generation) &&
        current.Generation == previous.Generation && current.ProcessId == previous.ProcessId &&
        current.EncodedFrames > previous.EncodedFrames && current.EncodedBytes > previous.EncodedBytes &&
        current.TimestampSeconds > previous.TimestampSeconds &&
        current.LastEncodedTimestampSeconds > previous.LastEncodedTimestampSeconds &&
        current.TimestampSeconds >= current.LastEncodedTimestampSeconds &&
        current.TimestampSeconds - current.LastEncodedTimestampSeconds <= 2;
}

/// <summary>Capture live requests first; never use unapplied editor values.</summary>
public sealed record FrameReconnectTuning(bool QualityEnabled, int Gaze, int Peripheral,
    EncodingPolicy Policy, AdvancedEncoding Advanced)
{
    public static FrameReconnectTuning? Capture(EncoderSnapshot before, FramePreferences saved)
    {
        if (before.Installed && !before.RequiresSteamVrRestart)
            return new(before.Enabled, before.CenterOffset, before.PeripheralOffset,
                before.EncodingPolicy?.Requested ?? new(), (before.AdvancedEncoding?.Requested ?? new()).Canonicalize());
        return saved.ReapplyAfterRestart
            ? new(saved.QualityEnabled, saved.GazeOffset, saved.PeripheralOffset,
                new(saved.RateControl, saved.MultiPass), (saved.AdvancedEncoding ?? new()).Canonicalize())
            : null;
    }

    public bool RequestsRestored(EncoderSnapshot snapshot) =>
        snapshot.Enabled == QualityEnabled && snapshot.CenterOffset == Gaze && snapshot.PeripheralOffset == Peripheral &&
        snapshot.EncodingPolicy?.Requested == Policy &&
        (snapshot.AdvancedEncoding?.Requested ?? new()).Canonicalize() == Advanced;
}
