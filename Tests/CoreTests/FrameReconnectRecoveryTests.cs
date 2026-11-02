using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Tests;

public class FrameReconnectRecoveryTests
{
    private static readonly EncoderSnapshot Old = new() { ProcessId = 10, Supported = true };
    private static readonly EncoderSnapshot Ready = new() { ProcessId = 20, Supported = true };
    private static readonly EncoderSnapshot Baseline = Ready with
    {
        Installed = true, Active = true, Codec = "HEVC", CodecObserved = true,
        Generation = "new-session", EncodedFrames = 100, EncodedBytes = 10000,
        TimestampSeconds = 10, LastEncodedTimestampSeconds = 9.99
    };
    private static readonly EncoderSnapshot Video = Baseline with
    {
        EncodedFrames = 145, EncodedBytes = 20000,
        TimestampSeconds = 10.5, LastEncodedTimestampSeconds = 10.49
    };

    [Fact]
    public async Task WaitsForReplacementRestoresOnceAndRequiresFreshOutput()
    {
        var readings = new Queue<EncoderSnapshot>([Old, Old, Ready with { WaitingForDriver = true }, Ready, Baseline, Video]);
        int restarts = 0, prepares = 0;
        var result = await FrameReconnectRecovery.RunAsync(() => Task.FromResult(readings.Dequeue()),
            pid => { Assert.Equal(10, pid); restarts++; return Task.CompletedTask; },
            (before, ready) => { Assert.Equal(Old, before); Assert.Equal(Ready, ready); prepares++; return Task.FromResult(Baseline); },
            _ => { }, TestContext.Current.CancellationToken, wait: _ => Task.CompletedTask);
        Assert.True(result.VideoResumed);
        Assert.Equal(Video, result.Snapshot);
        Assert.Equal(1, restarts); Assert.Equal(1, prepares);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task NoVideoReportsIncompleteWithoutRestartLoop(int originalPid)
    {
        var reads = 0; var restarts = 0;
        var result = await FrameReconnectRecovery.RunAsync(
            () => Task.FromResult(++reads == 1 ? Old with { ProcessId = originalPid } : Baseline),
            pid => { Assert.Equal(originalPid, pid); restarts++; return Task.CompletedTask; },
            (_, _) => Task.FromResult(Baseline), _ => { }, TestContext.Current.CancellationToken,
            videoAttempts: 3, wait: _ => Task.CompletedTask);
        Assert.False(result.VideoResumed); Assert.Equal(1, restarts); Assert.Equal(5, reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OptionalClientRetryIsBoundedAndNeverRestartsTheRuntimeAgain(bool recovers)
    {
        int reads = 0, restarts = 0, resumes = 0;
        var result = await FrameReconnectRecovery.RunAsync(
            () => Task.FromResult(++reads == 1 ? Old : resumes == 1 && recovers && reads > 6 ? Video : Baseline),
            _ => { restarts++; return Task.CompletedTask; },
            (_, _) => Task.FromResult(Baseline), _ => { }, TestContext.Current.CancellationToken,
            videoAttempts: 3, wait: _ => Task.CompletedTask,
            resumeVideoOnce: _ => { resumes++; return Task.CompletedTask; });
        Assert.Equal(recovers, result.VideoResumed);
        Assert.Equal(1, restarts);
        Assert.Equal(1, resumes);
        Assert.InRange(reads, 7, 9);
    }

    [Fact]
    public async Task ClientRetryStillRejectsAReplacedRuntime()
    {
        int reads = 0, resumes = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => FrameReconnectRecovery.RunAsync(
            () => Task.FromResult(++reads == 1 ? Old : resumes == 1 ? Baseline with { ProcessId = 30 } : Baseline),
            _ => Task.CompletedTask, (_, _) => Task.FromResult(Baseline), _ => { }, TestContext.Current.CancellationToken,
            videoAttempts: 1, wait: _ => Task.CompletedTask,
            resumeVideoOnce: _ => { resumes++; return Task.CompletedTask; }));
        Assert.Equal(1, resumes);
    }

    [Fact]
    public async Task OldProcessTimeoutNeverPreparesOrRestartsAgain()
    {
        var restarts = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => FrameReconnectRecovery.RunAsync(
            () => Task.FromResult(Old), _ => { restarts++; return Task.CompletedTask; },
            (_, _) => throw new Exception("Must not prepare old process"), _ => { },
            TestContext.Current.CancellationToken, driverAttempts: 3, wait: _ => Task.CompletedTask));
        Assert.Equal(1, restarts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PairedOwnerAtAnyStageStopsRecovery(int stage)
    {
        var readings = new Queue<EncoderSnapshot>([
            Old with { PairedCodecTrialPresent = stage == 0 },
            Ready with { PairedCodecTrialPresent = stage == 1 },
            Video with { PairedCodecTrialPresent = stage == 2 }]);
        var restarts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => FrameReconnectRecovery.RunAsync(
            () => Task.FromResult(readings.Dequeue()), _ => { restarts++; return Task.CompletedTask; },
            (_, _) => Task.FromResult(Baseline), _ => { }, TestContext.Current.CancellationToken,
            wait: _ => Task.CompletedTask));
        Assert.Equal(stage == 0 ? 0 : 1, restarts);
    }

    [Fact]
    public async Task RestartFailureDoesNotPrepareOrRetry()
    {
        await Assert.ThrowsAsync<IOException>(() => FrameReconnectRecovery.RunAsync(
            () => Task.FromResult(Old), _ => throw new IOException("Cannot stop SteamVR"),
            (_, _) => throw new Exception("Unexpected prepare"), _ => { }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReplacementMustBeSupportedAndCompatible(bool supported)
    {
        var reads = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => FrameReconnectRecovery.RunAsync(
            () => Task.FromResult(++reads == 1 ? Old : Ready with { Supported = supported, RequiresSteamVrRestart = supported }),
            _ => Task.CompletedTask, (_, _) => throw new Exception("Unexpected prepare"), _ => { },
            TestContext.Current.CancellationToken, wait: _ => Task.CompletedTask));
    }

    [Fact]
    public async Task UnexpectedProcessDuringVerificationIsNotSuccess()
    {
        var readings = new Queue<EncoderSnapshot>([Old, Ready, Video with { ProcessId = 30 }]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => FrameReconnectRecovery.RunAsync(
            () => Task.FromResult(readings.Dequeue()), _ => Task.CompletedTask,
            (_, _) => Task.FromResult(Baseline), _ => { }, TestContext.Current.CancellationToken,
            wait: _ => Task.CompletedTask));
    }

    [Fact]
    public async Task CancellationAfterRestartPreventsPrepare()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FrameReconnectRecovery.RunAsync(
            () => Task.FromResult(Old), _ => { cancellation.Cancel(); return Task.CompletedTask; },
            (_, _) => throw new Exception("Unexpected prepare"), _ => { }, cancellation.Token));
    }

    [Fact]
    public void FreshVideoRejectsStaleResetWrongCodecAndMetadataOnly()
    {
        Assert.True(FrameReconnectRecovery.FreshVideo(Baseline, Video));
        foreach (var invalid in new[] {
            Video with { EncodedBytes = Baseline.EncodedBytes }, Video with { EncodedFrames = 1 },
            Video with { Generation = "different" }, Video with { Generation = "" },
            Video with { ProcessId = 30 }, Video with { Codec = "H.264" },
            Video with { CodecObserved = false }, Video with { CodecReadFailed = true },
            Video with { Installed = false }, Video with { Active = false },
            Video with { LastEncodedTimestampSeconds = Baseline.LastEncodedTimestampSeconds },
            Video with { TimestampSeconds = 20 }, Video with { TimestampSeconds = double.NaN },
            Video with { LastEncodedTimestampSeconds = 11 } })
            Assert.False(FrameReconnectRecovery.FreshVideo(Baseline, invalid));
    }

    [Fact]
    public void LiveTuningTakesPrecedenceOverSavedPreferences()
    {
        var advanced = new AdvancedEncoding { MaximumQp = 25 };
        var policy = new EncodingPolicy(EncoderRateControl.Cbr, EncoderMultiPass.QuarterResolution);
        var tuning = FrameReconnectTuning.Capture(Baseline with {
            Enabled = true, CenterOffset = -20, PeripheralOffset = -12,
            EncodingPolicy = new() { Requested = policy }, AdvancedEncoding = new() { Requested = advanced }
        }, new() { ReapplyAfterRestart = false, GazeOffset = -4 });
        Assert.Equal(new FrameReconnectTuning(true, -20, -12, policy, advanced), tuning);
    }

    [Fact]
    public void UnavailableHelperUsesSavedTuningOnlyWhenEnabled()
    {
        Assert.Null(FrameReconnectTuning.Capture(Old, new() { ReapplyAfterRestart = false }));
        var saved = new FramePreferences { ReapplyAfterRestart = true, QualityEnabled = true, GazeOffset = -10, PeripheralOffset = -3,
            RateControl = EncoderRateControl.Cbr, MultiPass = EncoderMultiPass.QuarterResolution };
        var tuning = FrameReconnectTuning.Capture(Baseline with { RequiresSteamVrRestart = true }, saved);
        Assert.Equal(-10, tuning!.Gaze); Assert.Equal(-3, tuning.Peripheral);
        Assert.Equal(EncoderRateControl.Cbr, tuning.Policy.RateControl);
    }

    [Fact]
    public void IgnoredTuningRequestCannotBeReportedAsRestored()
    {
        var policy = new EncodingPolicy(EncoderRateControl.Cbr, EncoderMultiPass.QuarterResolution);
        var tuning = new FrameReconnectTuning(true, -20, -12, policy, new() { MaximumQp = 30 });
        var snapshot = Baseline with { Enabled = true, CenterOffset = -20, PeripheralOffset = -12,
            EncodingPolicy = new() { Requested = policy }, AdvancedEncoding = new() { Requested = tuning.Advanced } };
        Assert.True(tuning.RequestsRestored(snapshot));
        Assert.False(tuning.RequestsRestored(snapshot with { Enabled = false }));
        Assert.False(tuning.RequestsRestored(snapshot with { CenterOffset = -4 }));
        Assert.False(tuning.RequestsRestored(snapshot with { PeripheralOffset = 8 }));
        Assert.False(tuning.RequestsRestored(snapshot with { EncodingPolicy = new() }));
        Assert.False(tuning.RequestsRestored(snapshot with { AdvancedEncoding = new() }));
    }
}
