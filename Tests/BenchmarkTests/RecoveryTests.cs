using System.Text.Json;
using SteamLinkVRSettings.Frame;

public sealed class BenchmarkRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "benchmark-test-" + Guid.NewGuid().ToString("N"));
    private SteamPaths Paths => new(_directory, _directory);
    private string Journal => Path.Combine(_directory, "recovery.json");
    private BenchmarkSettingsLease Lease(Fake settings) => new(Paths, new EncoderProbe(), Journal, settings);

    [Theory]
    [InlineData("generation", "session changed")]
    [InlineData("quality", "quality request changed")]
    [InlineData("policy", "encoder request changed")]
    public async Task ReconnectReportsTheFailedOwnershipInvariant(string change, string reason)
    {
        var backend=new Fake();var lease=Lease(backend);await lease.BeginAsync(await backend.Snapshot());
        await lease.CheckEncoderOwnershipAsync();
        if(change=="generation")backend.Generation="replacement";
        else if(change=="quality")backend.QualityValue=new(true,-4,0);
        else backend.Request=new(EncoderRateControl.Vbr,EncoderMultiPass.SinglePass);
        var error = await Assert.ThrowsAsync<IOException>(lease.CheckEncoderOwnershipAsync);
        Assert.Contains(reason, error.Message);
        Assert.Contains("Last NVENC status", error.Message);
        Assert.True(lease.Pending);
    }

    [Fact] public async Task ChangedEncoderCannotWriteTheNextTrialAndReportsNvencFailure()
    {
        var backend = new Fake(); var lease = Lease(backend);
        await lease.BeginAsync(await backend.Snapshot());
        backend.Generation = "replacement"; backend.LastNvencStatus = 8;
        var journal = await File.ReadAllTextAsync(Journal);
        var error = await Assert.ThrowsAsync<IOException>(() => lease.ApplyAsync(
            new(200, EncoderMultiPass.SinglePass, false), 2048,
            new(EncoderRateControl.Cbr, EncoderMultiPass.SinglePass)));
        Assert.Contains("Last NVENC status: 8", error.Message);
        Assert.Equal(journal, await File.ReadAllTextAsync(Journal));
        Assert.Single(backend.Overrides);
        Assert.Equal(400, backend.Overrides["targetBandwidth"].GetDouble());
    }

    [Fact] public async Task RestoresAbsentOverridesAndOriginalPolicyAfterCompletedRun()
    {
        var backend = new Fake(); var lease = Lease(backend);
        await lease.BeginAsync(await backend.Snapshot());
        await lease.ApplyAsync(new(300, EncoderMultiPass.SinglePass, true), 1664, new(EncoderRateControl.Cbr, EncoderMultiPass.SinglePass));
        Assert.True(lease.Pending); Assert.Equal(300, (await backend.Read())["targetBandwidth"].GetDouble());
        await lease.RestoreAsync();
        Assert.False(lease.Pending); Assert.Single(backend.Overrides); Assert.Equal(400, backend.Overrides["targetBandwidth"].GetDouble());
        Assert.Equal(new(EncoderRateControl.Cbr, EncoderMultiPass.QuarterResolution), backend.Request);
    }
    [Fact] public async Task ValveReferenceAndManualTrialsRestoreTheOriginalAutomaticModeAndMap()
    {
        var backend=new Fake();var lease=Lease(backend);var original=backend.QualityValue;
        await lease.BeginAsync(await backend.Snapshot());
        await lease.ApplyAsync(new(200,EncoderMultiPass.ValveDefault,false,new(false,0,0)){ValveReference=true},1664,new());
        Assert.True((await backend.Read())["automaticBandwidth"].GetBoolean());Assert.True(backend.Request.IsDefault);
        await lease.ApplyAsync(new(300,EncoderMultiPass.SinglePass,false,new(true,-8,-4)),1664,new(EncoderRateControl.Cbr,EncoderMultiPass.SinglePass));
        Assert.False((await backend.Read())["automaticBandwidth"].GetBoolean());
        await Lease(backend).RestoreAsync();Assert.True((await backend.Read())["automaticBandwidth"].GetBoolean());
        Assert.Equal(original,backend.QualityValue);Assert.Single(backend.Overrides);
    }
    [Fact] public async Task RefreshDoesNotLeaveAnOverrideThatWasOriginallyAbsent()
    {
        var backend=new Fake{RefreshCreatesTarget=true};backend.Overrides.Clear();var lease=Lease(backend);
        await lease.BeginAsync(await backend.Snapshot());
        await lease.ApplyAsync(new(300,EncoderMultiPass.SinglePass,false),1664,new(EncoderRateControl.Cbr,EncoderMultiPass.SinglePass));
        await lease.RestoreAsync();Assert.Empty(backend.Overrides);
    }
    [Fact] public async Task NewInstanceRecoversPartialWriteBeforePolicyWasChanged()
    {
        var backend = new Fake(); var lease = Lease(backend); await lease.BeginAsync(await backend.Snapshot());
        backend.FailAfterFirstWrite = true;
        await Assert.ThrowsAsync<IOException>(() => lease.ApplyAsync(new(100, EncoderMultiPass.SinglePass, false), 1664, new(EncoderRateControl.Cbr, EncoderMultiPass.SinglePass)));
        backend.FailAfterFirstWrite = false;
        await Lease(backend).RestoreAsync();
        Assert.Single(backend.Overrides); Assert.Equal(400, backend.Overrides["targetBandwidth"].GetDouble()); Assert.False(File.Exists(Journal));
    }
    [Fact] public async Task ValveAutomaticTargetChangesRemainOwnedAndDoNotBlockTheNextTrial()
    {
        var backend=new Fake();var lease=Lease(backend);await lease.BeginAsync(await backend.Snapshot());
        await lease.ApplyAsync(new(200,EncoderMultiPass.ValveDefault,false,new(false,0,0)){ValveReference=true},1664,new());
        backend.Overrides["targetBandwidth"]=JsonSerializer.SerializeToElement(213d);
        await lease.CheckControlsAsync();
        await lease.ApplyAsync(new(150,EncoderMultiPass.SinglePass,false,new(false,0,0)),1664,new(EncoderRateControl.Cbr,EncoderMultiPass.SinglePass));
        await lease.RestoreAsync();Assert.Equal(400,backend.Overrides["targetBandwidth"].GetDouble());
    }
    [Fact] public async Task ValveAutomaticTargetChangesAreRestoredAfterCancellation()
    {
        var backend=new Fake();var lease=Lease(backend);await lease.BeginAsync(await backend.Snapshot());
        await lease.ApplyAsync(new(200,EncoderMultiPass.ValveDefault,false,new(false,0,0)){ValveReference=true},1664,new());
        backend.Overrides["targetBandwidth"]=JsonSerializer.SerializeToElement(213d);
        await Lease(backend).RestoreAsync();Assert.Equal(400,backend.Overrides["targetBandwidth"].GetDouble());
    }
    [Fact] public async Task NewHelperIsJournaledBeforeControlsSoInterruptedRestartCanBeRecovered()
    {
        var backend=new Fake();var original=backend.Request;var quality=backend.QualityValue;
        backend.AdvancedValue=new(){PeakMbps=420};
        var lease=Lease(backend);await lease.BeginAsync(await backend.Snapshot());
        await lease.ApplyAsync(new(200,EncoderMultiPass.ValveDefault,false,new(false,0,0)){ValveReference=true},1664,new());
        var before=await backend.Snapshot();backend.ProcessId=20;backend.Generation="replacement";
        backend.Request=new();backend.QualityValue=new(false,0,0);backend.AdvancedValue=new();
        await lease.AdoptRestartAsync(before,await backend.Snapshot());
        // Simulate app loss before its first request reaches the replacement helper.
        await Lease(backend).RestoreAsync();
        Assert.Equal(original,backend.Request);Assert.Equal(quality,backend.QualityValue);
        Assert.Equal(420,backend.AdvancedValue.PeakMbps);Assert.False(File.Exists(Journal));
    }
    [Fact] public async Task RecoveredInstanceCanOwnTheDisplayRestorationRestart()
    {
        var backend=new Fake();var lease=Lease(backend);await lease.BeginAsync(await backend.Snapshot());
        await lease.ApplyAsync(new(150,EncoderMultiPass.SinglePass,false),1280,new(EncoderRateControl.Cbr,EncoderMultiPass.SinglePass));
        var recovered=Lease(backend);await recovered.CheckControlsAsync();
        var before=await backend.Snapshot();backend.ProcessId=20;backend.Generation="restoring-display";
        await recovered.AdoptRestartAsync(before,await backend.Snapshot());
        await recovered.RestoreAsync();Assert.False(recovered.Pending);Assert.Equal(400,backend.Overrides["targetBandwidth"].GetDouble());
    }
    [Fact] public async Task OriginalModeIsVerifiedAfterRestorationRestart()
    {
        var backend=new Fake();var lease=Lease(backend);await lease.BeginAsync(await backend.Snapshot());
        await lease.ApplyAsync(new(200,EncoderMultiPass.ValveDefault,false,new(false,0,0)){ValveReference=true},1664,new());
        backend.NeedsNewSession=true;int restarts=0;
        await lease.RestoreAsync(async()=>
        {
            Assert.True(File.Exists(Journal));var before=await backend.Snapshot();
            backend.ProcessId=20;backend.Generation="restored";backend.NeedsNewSession=false;
            await lease.AdoptRestartAsync(before,await backend.Snapshot());restarts++;
        });
        Assert.Equal(1,restarts);Assert.False(File.Exists(Journal));
        Assert.Equal(new(EncoderRateControl.Cbr,EncoderMultiPass.QuarterResolution),backend.Request);
    }
    [Fact] public async Task FailedRestorationRestartRetainsRecoveryJournal()
    {
        var backend=new Fake();var lease=Lease(backend);await lease.BeginAsync(await backend.Snapshot());
        backend.NeedsNewSession=true;
        await Assert.ThrowsAsync<IOException>(()=>lease.RestoreAsync(()=>throw new IOException("Restart failed")));
        Assert.True(File.Exists(Journal));backend.NeedsNewSession=false;
        await Lease(backend).RestoreAsync();Assert.False(File.Exists(Journal));
    }
    [Fact] public async Task RestartCannotAdoptAnUnrelatedRequestOrTheOldProcess()
    {
        var backend=new Fake();var lease=Lease(backend);await lease.BeginAsync(await backend.Snapshot());
        var before=await backend.Snapshot();
        await Assert.ThrowsAsync<IOException>(()=>lease.AdoptRestartAsync(before,before));
        backend.Request=new();backend.ProcessId=20;backend.Generation="external";
        var external=await backend.Snapshot();
        await Assert.ThrowsAsync<IOException>(()=>lease.AdoptRestartAsync(external,before));
    }
    [Fact] public async Task LaterExternalEditsArePreserved()
    {
        var backend = new Fake(); var lease = Lease(backend); await lease.BeginAsync(await backend.Snapshot());
        await lease.ApplyAsync(new(300, EncoderMultiPass.SinglePass, true), 1664, new(EncoderRateControl.Cbr, EncoderMultiPass.SinglePass));
        backend.Overrides["targetBandwidth"] = JsonSerializer.SerializeToElement(333d);
        backend.Request = new(EncoderRateControl.Vbr, EncoderMultiPass.FullResolution);
        await lease.RestoreAsync();
        Assert.Equal(333, backend.Overrides["targetBandwidth"].GetDouble()); Assert.Equal(EncoderRateControl.Vbr, backend.Request.RateControl);
    }
    [Fact] public async Task FailedRestoreKeepsJournalForRetry()
    {
        var backend = new Fake(); var lease = Lease(backend); await lease.BeginAsync(await backend.Snapshot());
        backend.FailAfterFirstWrite = true;
        await Assert.ThrowsAsync<IOException>(() => lease.RestoreAsync()); Assert.True(File.Exists(Journal));
        backend.FailAfterFirstWrite = false; await Lease(backend).RestoreAsync(); Assert.False(File.Exists(Journal));
    }
    [Fact] public async Task ASecondRunCannotOverwriteTheRecoveryJournal()
    {
        var backend = new Fake(); var lease = Lease(backend); await lease.BeginAsync(await backend.Snapshot());
        var bytes = await File.ReadAllBytesAsync(Journal);
        var second = Lease(backend); var snapshot = await backend.Snapshot();
        await Assert.ThrowsAsync<IOException>(() => second.BeginAsync(snapshot));
        await second.RestoreAsync(); // Failed acquisition must not consume the first app's journal.
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Journal));
    }
    [Fact] public async Task ReplacedSessionNeverReceivesOldHelperRequests()
    {
        var backend = new Fake(); var lease = Lease(backend); await lease.BeginAsync(await backend.Snapshot());
        await lease.ApplyAsync(new(300, EncoderMultiPass.SinglePass, true), 1664, new(EncoderRateControl.Cbr, EncoderMultiPass.SinglePass));
        backend.Generation = "replacement";
        await lease.RestoreAsync(); Assert.Equal(EncoderMultiPass.SinglePass, backend.Request.MultiPass);
    }
    [Fact] public async Task ExternalChangePreventsNextCandidate()
    {
        var backend = new Fake(); var lease = Lease(backend); await lease.BeginAsync(await backend.Snapshot());
        backend.Overrides["targetBandwidth"] = JsonSerializer.SerializeToElement(333d);
        await Assert.ThrowsAsync<IOException>(() => lease.ApplyAsync(new(200, EncoderMultiPass.SinglePass, false), 1664, new()));
        Assert.Equal(333, backend.Overrides["targetBandwidth"].GetDouble());
    }
    [Fact] public async Task QualityFallbackIsJournaledAndRecoveredByANewInstance()
    {
        var backend=new Fake();var original=backend.QualityValue;var lease=Lease(backend);
        await lease.BeginAsync(await backend.Snapshot());
        await lease.ApplyAsync(new(100,EncoderMultiPass.SinglePass,false,new(true,-8,-4)),1664,new(EncoderRateControl.Cbr,EncoderMultiPass.SinglePass));
        Assert.Equal(new(true,-8,-4),backend.QualityValue);
        await Lease(backend).RestoreAsync();Assert.Equal(original,backend.QualityValue);Assert.False(File.Exists(Journal));
    }
    [Fact] public async Task NewerQualityEditIsPreservedAndBlocksFurtherTrials()
    {
        var backend=new Fake();var lease=Lease(backend);await lease.BeginAsync(await backend.Snapshot());
        backend.QualityValue=new(true,-3,5);
        await Assert.ThrowsAsync<IOException>(()=>lease.ApplyAsync(new(100,EncoderMultiPass.SinglePass,false,new(true,-8,-4)),1664,new()));
        await lease.RestoreAsync();Assert.Equal(new(true,-3,5),backend.QualityValue);
    }
    [Fact] public async Task LegacyJournalDoesNotClaimOwnershipOfQuality()
    {
        var backend=new Fake();var lease=Lease(backend);await lease.BeginAsync(await backend.Snapshot());
        var legacy=System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Journal))!.AsObject();
        foreach(var key in new[]{"OriginalQuality","ExpectedQuality","PreviousQuality"})legacy.Remove(key);
        await File.WriteAllTextAsync(Journal,legacy.ToJsonString());backend.QualityValue=new(true,-3,5);
        await Lease(backend).RestoreAsync();Assert.Equal(new(true,-3,5),backend.QualityValue);Assert.False(File.Exists(Journal));
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private sealed class Fake : IBenchmarkSettings
    {
        public Dictionary<string, JsonElement> Overrides = new() { ["targetBandwidth"] = JsonSerializer.SerializeToElement(400d) };
        public EncodingPolicy Request = new(EncoderRateControl.Cbr, EncoderMultiPass.QuarterResolution);
        public bool FailAfterFirstWrite;
        public string Generation = "original";
        public int ProcessId=10;
        public bool NeedsNewSession;
        public bool RefreshCreatesTarget;
        public uint LastNvencStatus;
        public AdvancedEncoding AdvancedValue=new();
        public BenchmarkQuality QualityValue = new(true,-20,-12);
        public Task<Dictionary<string, JsonElement>> Saved() => Task.FromResult(new Dictionary<string, JsonElement>(Overrides));
        public Task<Dictionary<string, JsonElement>> Read()
        {
            var result = new Dictionary<string, JsonElement>
            {
                ["targetBandwidth"] = JsonSerializer.SerializeToElement(100d),
                ["automaticBandwidth"] = JsonSerializer.SerializeToElement(true),
                ["automaticStreamFormatWidth"] = JsonSerializer.SerializeToElement(true),
                ["streamFormatWidth"] = JsonSerializer.SerializeToElement(1664), ["asyncSend"] = JsonSerializer.SerializeToElement(false)
            };
            foreach (var pair in Overrides) result[pair.Key] = pair.Value;
            return Task.FromResult(result);
        }
        public Task Write(Dictionary<string, JsonElement> values)
        {
            foreach (var pair in values) { Overrides[pair.Key] = pair.Value; if (FailAfterFirstWrite) throw new IOException("Write failed."); }
            return Task.CompletedTask;
        }
        public Task Remove(IEnumerable<string> keys) { foreach (var key in keys) Overrides.Remove(key); return Task.CompletedTask; }
        public Task<EncoderSnapshot> Snapshot() => Task.FromResult(new EncoderSnapshot { Installed = true, Generation = Generation, ProcessId=ProcessId, EncodingPolicy = new() { Requested = Request, NeedsNewSession=NeedsNewSession },AdvancedEncoding=new(){Requested=AdvancedValue},
            LastNvencStatus=LastNvencStatus, Enabled=QualityValue.Enabled, CenterOffset=QualityValue.GazeOffset,PeripheralOffset=QualityValue.PeripheralOffset });
        public Task<EncoderSnapshot> Policy(EncodingPolicy value) { Request = value; return Snapshot(); }
        public Task<EncoderSnapshot> Quality(BenchmarkQuality value) { QualityValue=value;return Snapshot(); }
        public Task<EncoderSnapshot> Advanced(AdvancedEncoding value) {AdvancedValue=value;return Snapshot();}
        public async Task Refresh()
        { if(RefreshCreatesTarget)Overrides["targetBandwidth"]=(await Read())["targetBandwidth"]; }
    }
}
