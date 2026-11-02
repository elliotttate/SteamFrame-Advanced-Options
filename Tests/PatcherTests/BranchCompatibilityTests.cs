using System.Security.Cryptography;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Tests;

public sealed class BranchCompatibilityTests
{
    [Theory]
    [InlineData("SLVR_PATCHER_STABLE_FIXTURE", PatchManifest.SupportedStableOriginalSha256, PatchManifest.SupportedStablePatchedSha256)]
    [InlineData("SLVR_PATCHER_CURRENT_FIXTURE", BitratePatcher.SupportedCurrentOriginalSha256, BitratePatcher.SupportedCurrentPatchedSha256)]
    [InlineData("SLVR_PATCHER_25998080_FIXTURE", PatchManifest.Supported25998080OriginalSha256, PatchManifest.Supported25998080PatchedSha256)]
    public async Task BranchPatchRoundTripPreservesOriginalAndRejectsUnsafeLayouts(string variable, string originalHash, string patchedHash)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(fixture) || !File.Exists(fixture)) Assert.Skip($"Set {variable} to test isolated driver copies.");
        var original = await File.ReadAllBytesAsync(fixture, token);
        Assert.Equal(originalHash, Hash(original));
        var plan = AdaptivePatchScanner.Discover(original);
        Assert.Equal(patchedHash, plan.PatchedSha256);
        Assert.Equal(6, plan.Changes.Count);
        if (originalHash == PatchManifest.SupportedStableOriginalSha256)
            Assert.True(plan.DescribesSamePatch(PatchManifest.LoadStable()));
        if (originalHash == PatchManifest.Supported25998080OriginalSha256)
            Assert.True(plan.DescribesSamePatch(PatchManifest.Load25998080()));
        var root = Path.Combine(Path.GetTempPath(), "SteamLinkVRSettings-branch-patcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "driver_vrlink.dll");
            await File.WriteAllBytesAsync(path, original, token);
            var patcher = NewPatcher();
            var status = await patcher.InspectAsync(path, token);
            Assert.True(status.CanApply);
            Assert.Equal(BitrateBackupState.Missing, status.BackupState);
            Assert.Contains("Not created yet", status.Details);
            // An old build's backup must not prevent the new build's first patch.
            await File.WriteAllTextAsync(BitratePatcher.GetBackupPath(path), "unrelated older backup", token);
            status = await patcher.ApplyAsync(path, token);
            Assert.Equal(patchedHash, status.Sha256);
            Assert.True(status.CanRestore);
            Assert.Equal(original, await File.ReadAllBytesAsync(status.BackupPath, token));
            Assert.True(File.Exists(status.ReceiptPath));
            Assert.True((await NewPatcher().InspectAsync(path, token)).CanRestore);
            var modifiedAt = File.GetLastWriteTimeUtc(path);
            await patcher.ApplyAsync(path, token);
            Assert.Equal(modifiedAt, File.GetLastWriteTimeUtc(path));

            // A recognized patched file remains identifiable without a receipt or
            // backup, but cannot be restored without that exact original.
            var orphan = Path.Combine(root, "orphan.dll");
            await File.WriteAllBytesAsync(orphan, await File.ReadAllBytesAsync(path, token), token);
            var orphanStatus = await NewPatcher().InspectAsync(orphan, token);
            Assert.True(orphanStatus.IsPatched);
            Assert.False(orphanStatus.CanRestore);
            Assert.Contains("backup is missing", orphanStatus.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => patcher.RestoreAsync(orphan, token));

            await Assert.ThrowsAsync<InvalidOperationException>(() => NewPatcher(running: true).RestoreAsync(path, token));
            Assert.Equal(patchedHash, Hash(await File.ReadAllBytesAsync(path, token)));
            var backup = await File.ReadAllBytesAsync(status.BackupPath, token);
            var badBackup = (byte[])backup.Clone(); badBackup[^1] ^= 1;
            await File.WriteAllBytesAsync(status.BackupPath, badBackup, token);
            var damagedBackupStatus = await patcher.InspectAsync(path, token);
            Assert.Equal(BitrateBackupState.Mismatch, damagedBackupStatus.BackupState);
            Assert.Contains("backup does not match", damagedBackupStatus.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => patcher.RestoreAsync(path, token));
            Assert.Equal(patchedHash, Hash(await File.ReadAllBytesAsync(path, token)));
            await File.WriteAllBytesAsync(status.BackupPath, backup, token);
            status = await NewPatcher().RestoreAsync(path, token);
            Assert.Equal(BitratePatchState.Original, status.State);
            Assert.Equal(original, await File.ReadAllBytesAsync(path, token));

            // An unlisted hash is still accepted when all layout checks succeed.
            var unlisted = (byte[])original.Clone(); unlisted[^1] ^= 1;
            var unlistedPath = Path.Combine(root, "unlisted.dll");
            await File.WriteAllBytesAsync(unlistedPath, unlisted, token);
            var unlistedStatus = await patcher.ApplyAsync(unlistedPath, token);
            Assert.True(unlistedStatus.IsPatched);
            Assert.Equal("Compatible driver layout verified.", unlistedStatus.Compatibility);
            Assert.True((await NewPatcher().InspectAsync(unlistedPath, token)).CanRestore);
            await patcher.RestoreAsync(unlistedPath, token);
            Assert.Equal(unlisted, await File.ReadAllBytesAsync(unlistedPath, token));

            // A foreign preset/member layout must not be accepted just because the
            // bitrate selector matches. Neither layout's offsets are wildcarded.
            var mixed = (byte[])original.Clone();
            var hook = plan.Changes.Single(x => x.Reason == "Route preset copy through the level fix").FileOffset;
            mixed[hook - 24] ^= 0x98; // Switch the preset function-table member only.
            Assert.Throws<InvalidDataException>(() => AdaptivePatchScanner.Discover(mixed));
            var wrongCap = (byte[])original.Clone();
            wrongCap[plan.Changes.Single(x => x.Reason == "Wireless-adapter-present bitrate cap").FileOffset] ^= 1;
            Assert.Throws<InvalidDataException>(() => AdaptivePatchScanner.Discover(wrongCap));

            // Partial/manual patches cannot be mistaken for a clean supported file.
            var partial = (byte[])original.Clone();
            var cap = plan.Changes[0]; cap.PatchedBytes.CopyTo(partial, cap.FileOffset);
            var partialPath = Path.Combine(root, "partial.dll");
            await File.WriteAllBytesAsync(partialPath, partial, token);
            var refused = await patcher.InspectAsync(partialPath, token);
            Assert.Equal(BitratePatchState.Unsupported, refused.State);
            Assert.Contains("cap constants", refused.VerificationError);
            Assert.Contains("Update this app", refused.Message);
            Assert.Contains("25330290", refused.Details);
            Assert.Contains("25839986", refused.Details);
            await Assert.ThrowsAsync<InvalidOperationException>(() => patcher.ApplyAsync(partialPath, token));
            Assert.Equal(partial, await File.ReadAllBytesAsync(partialPath, token));
        }
        finally
        {
            var prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "SteamLinkVRSettings-branch-patcher-");
            if (Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SwitchingBuildsKeepsEachOriginalAndUsesTheInstalledDriver()
    {
        var token = TestContext.Current.CancellationToken;
        var stable = Environment.GetEnvironmentVariable("SLVR_PATCHER_STABLE_FIXTURE");
        var beta = Environment.GetEnvironmentVariable("SLVR_PATCHER_CURRENT_FIXTURE");
        if (!File.Exists(stable) || !File.Exists(beta)) Assert.Skip("Both stable and beta original fixtures are required.");
        using var fixture = new DriverTestDirectory();
        var path = Path.Combine(fixture.Root, "driver_vrlink.dll");
        var patcher = NewPatcher();
        var stableBytes = await File.ReadAllBytesAsync(stable!, token);
        var betaBytes = await File.ReadAllBytesAsync(beta!, token);
        await File.WriteAllBytesAsync(path, stableBytes, token);
        var stableStatus = await patcher.ApplyAsync(path, token);
        await File.WriteAllBytesAsync(path, betaBytes, token); // Steam installs another branch.
        var betaStatus = await patcher.ApplyAsync(path, token);
        Assert.NotEqual(stableStatus.BackupPath, betaStatus.BackupPath);
        Assert.Equal(PatchManifest.SupportedCurrentPatchedSha256, betaStatus.Sha256);
        Assert.Equal(stableBytes, await File.ReadAllBytesAsync(stableStatus.BackupPath, token));
        await NewPatcher().RestoreAsync(path, token);
        Assert.Equal(betaBytes, await File.ReadAllBytesAsync(path, token));
        await File.WriteAllBytesAsync(path, stableBytes, token);
        Assert.Equal(PatchManifest.SupportedStablePatchedSha256, (await NewPatcher().ApplyAsync(path, token)).Sha256);
        await patcher.RestoreAsync(path, token);
        Assert.Equal(stableBytes, await File.ReadAllBytesAsync(path, token));
        Assert.Equal(betaBytes, await File.ReadAllBytesAsync(betaStatus.BackupPath, token));
    }

    private static BitratePatcher NewPatcher(bool running = false) => new(PatchManifest.LoadSupported(), () => running);
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
