using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace SteamLinkVRSettings.Frame;

public enum BitratePatchState { Missing, Original, Patched, Unsupported, Unavailable }
public enum BitrateBackupState { Missing, VerifiedOriginal, Mismatch }
public sealed record BitratePatchStatus(string FilePath, string BackupPath, BitratePatchState State,
    BitrateBackupState BackupState, string? Sha256, string? BackupSha256, bool VrServerRunning, string Message)
{
    public bool IsSupported => State is BitratePatchState.Original or BitratePatchState.Patched;
    public bool IsPatched => State == BitratePatchState.Patched;
    public bool CanApply => State == BitratePatchState.Original && BackupState != BitrateBackupState.Mismatch && !VrServerRunning;
    public bool CanRestore => State == BitratePatchState.Patched && BackupState == BitrateBackupState.VerifiedOriginal && !VrServerRunning;
    public string ReceiptPath => BackupPath + ".patch.json";
    public string InstalledVersion { get; init; } = "SteamVR build unavailable for this file";
    public string VerificationError { get; init; } = "";
    public string Compatibility => IsSupported
        ? DriverCompatibility.ReferenceDriver(Sha256) is var reference && reference != "No reference hash match"
            ? $"Verified bitrate patch: {reference}." : "Compatible driver layout verified."
        : "Bitrate patch compatibility has not been verified.";
    public string Details => $"{InstalledVersion}\nReference driver: {DriverCompatibility.ReferenceDriver(Sha256)}\n{DriverCompatibility.VerifiedVersions}\n" +
        $"File: {FilePath}\nSHA-256: {Sha256 ?? "unavailable"}\nState: {State}\nBackup: {BackupPath}\nBackup status: {(BackupState == BitrateBackupState.Missing && State == BitratePatchState.Original ? "Not created yet; saved automatically when installing the override" : BackupState.ToString())}\nBackup SHA-256: {BackupSha256 ?? "unavailable"}\nPatch receipt: {ReceiptPath}\n" +
        $"Verification: {(VerificationError.Length == 0 ? Message : VerificationError)}";
    public string PresetFailureMessage => $"This preset's bitrate override could not be prepared. {Message} See Patches & Recovery for version and driver details.";
}

/// <summary>Exact-hash fast paths plus strict discovery of compatible layouts at moved addresses.</summary>
public sealed class BitratePatcher
{
    public const string SupportedOriginalSha256 = PatchManifest.SupportedOriginalSha256;
    public const string SupportedPatchedSha256 = PatchManifest.SupportedPatchedSha256;
    public const string SupportedCurrentOriginalSha256 = PatchManifest.SupportedCurrentOriginalSha256;
    public const string SupportedCurrentPatchedSha256 = PatchManifest.SupportedCurrentPatchedSha256;
    public const string BackupSuffix = ".steam-frame-bitrate-original.bak";
    private static readonly SemaphoreSlim OperationGate = new(1, 1);
    private readonly PatchManifest manifest;
    private readonly Func<bool> vrServerRunning;
    public BitratePatcher() : this(PatchManifest.LoadSupported(), IsVrServerRunning) { }
    internal BitratePatcher(PatchManifest manifest, Func<bool> vrServerRunning)
    { manifest.Validate(); this.manifest = manifest; this.vrServerRunning = vrServerRunning; }
    public static string GetBackupPath(string path) => NormalizePath(path) + BackupSuffix;
    public static string GetBackupPath(string path, string originalSha256)
    {
        if (originalSha256 == SupportedOriginalSha256) return GetBackupPath(path);
        if (originalSha256.Length != 64 || !originalSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("Expected a lowercase SHA-256 source hash.", nameof(originalSha256));
        return NormalizePath(path) + ".steam-frame-bitrate-" + originalSha256 + ".bak";
    }
    public async Task<BitratePatchStatus> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = NormalizePath(path); await OperationGate.WaitAsync(cancellationToken);
        try { return await InspectCoreAsync(full, cancellationToken); } finally { OperationGate.Release(); }
    }
    public async Task<BitratePatchStatus> ApplyAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = NormalizePath(path); await OperationGate.WaitAsync(cancellationToken);
        try
        {
            var status = await InspectCoreAsync(full, cancellationToken); RequireSupported(status);
            if (status.IsPatched) return status;
            EnsureVrServerStopped();
            var original = await ReadBoundedAsync(full, cancellationToken);
            var plan = await ResolvePlanAsync(full, original, cancellationToken);
            RequireHash(original, status.Sha256!); RequireHash(original, plan.OriginalSha256); ValidateSegments(original, plan, false);
            var patched = (byte[])original.Clone();
            foreach (var change in plan.Changes) { RequireSegment(patched, change.FileOffset, change.OriginalBytes); change.PatchedBytes.CopyTo(patched, change.FileOffset); }
            RequireHash(patched, plan.PatchedSha256);
            await EnsureBackupAsync(status.BackupPath, original, plan, cancellationToken);
            await EnsureReceiptAsync(status.ReceiptPath, plan, cancellationToken);
            await ReplaceGuardedAsync(full, status.BackupPath, patched, plan, false, cancellationToken);
            return await InspectCoreAsync(full, CancellationToken.None);
        }
        finally { OperationGate.Release(); }
    }
    public async Task<BitratePatchStatus> RestoreAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = NormalizePath(path); await OperationGate.WaitAsync(cancellationToken);
        try
        {
            var status = await InspectCoreAsync(full, cancellationToken); RequireSupported(status);
            if (status.State == BitratePatchState.Original) return status;
            if (status.BackupState != BitrateBackupState.VerifiedOriginal) throw new InvalidOperationException("Restore requires this build's verified original backup.");
            EnsureVrServerStopped();
            var current = await ReadBoundedAsync(full, cancellationToken);
            var plan = await ResolvePlanAsync(full, current, cancellationToken);
            RequireHash(current, status.Sha256!); RequireHash(current, plan.PatchedSha256); ValidateSegments(current, plan, true);
            var original = await ReadVerifiedAsync(status.BackupPath, plan, false, cancellationToken);
            await ReplaceGuardedAsync(full, status.BackupPath, original, plan, true, cancellationToken);
            return await InspectCoreAsync(full, CancellationToken.None);
        }
        finally { OperationGate.Release(); }
    }
    private async Task<BitratePatchStatus> InspectCoreAsync(string path, CancellationToken ct)
    {
        var backup = GetBackupPath(path); var state = BitratePatchState.Missing; var backupState = BitrateBackupState.Missing;
        string? hash = null, backupHash = null, error = null; PatchManifest? plan = null;
        try
        {
            if (File.Exists(path))
            {
                var data = await ReadBoundedAsync(path, ct); hash = Hash(data);
                try { plan = await ResolvePlanAsync(path, data, ct); }
                catch (Exception ex) when (ex is InvalidDataException or OverflowException or InvalidOperationException)
                { state = BitratePatchState.Unsupported; error = ex.Message; }
                if (plan != null)
                {
                    backup = GetBackupPath(path, plan.OriginalSha256);
                    state = ClassifyHash(hash, plan);
                    ValidateSegments(data, plan, state == BitratePatchState.Patched);
                    if (File.Exists(backup + ".patch.json")) RequireReceipt(await ReadReceiptAsync(backup + ".patch.json", ct), plan);
                }
            }
            if (File.Exists(backup))
            {
                var data = await ReadBoundedAsync(backup, ct); backupHash = Hash(data);
                backupState = plan != null && backupHash == plan.OriginalSha256 ? BitrateBackupState.VerifiedOriginal : BitrateBackupState.Mismatch;
                if (backupState == BitrateBackupState.VerifiedOriginal) ValidateSegments(data, plan!, false);
            }
        }
        // A malformed backup or receipt makes this driver unavailable for patching; it must
        // not escape and block ordinary stream settings that only inspect the driver.
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OverflowException or InvalidOperationException)
        { state = BitratePatchState.Unavailable; error = ex.Message; }
        var running = vrServerRunning();
        var message = state switch
        {
            BitratePatchState.Unsupported => "This driver does not match a supported bitrate-patch layout. It may be a newer SteamVR version or a modified file. Update this app and check again; if it still fails, copy driver details below when reporting it. Patching is blocked.",
            BitratePatchState.Unavailable => "The driver or its recovery files could not be verified. Check the verification reason in Driver file and verification below, or copy driver details for help. Patching and restore are blocked.",
            _ when backupState == BitrateBackupState.Mismatch => "The backup does not match this driver version or has been modified. Patching and restore are blocked. Keep the backup and copy driver details for recovery help.",
            BitratePatchState.Missing => "Select the SteamVR driver_vrlink.dll file.",
            BitratePatchState.Original => "Validated compatible driver: 500 Mbps and HEVC 6.2 patch available.",
            BitratePatchState.Patched when backupState == BitrateBackupState.Missing => "The 500 Mbps patch is verified, but this version's original backup is missing. Restore is unavailable. Restore the matching backup, or use SteamVR's Verify files to recover Valve's driver.",
            BitratePatchState.Patched => "The 500 Mbps and HEVC 6.2 patch is installed with a verified matching original backup.",
            _ => "Unrecognized driver layout; no changes are allowed."
        };
        if (running && state is BitratePatchState.Original or BitratePatchState.Patched) message += " Close SteamVR before replacing the driver.";
        return new(path, backup, state, backupState, hash, backupHash, running, message)
        { InstalledVersion = DriverCompatibility.InstalledVersion(path), VerificationError = error ?? "" };
    }
    private PatchManifest PlanForOriginal(byte[] bytes)
    {
        var hash = Hash(bytes);
        if (hash == SupportedOriginalSha256) return manifest;
        if (hash == SupportedCurrentOriginalSha256) return manifest.ForCurrentBuild();
        if (hash == PatchManifest.Supported25998080OriginalSha256) return PatchManifest.Load25998080();
        if (hash == PatchManifest.SupportedStableOriginalSha256) return PatchManifest.LoadStable();
        return AdaptivePatchScanner.Discover(bytes);
    }
    internal static BitratePatchState ClassifyHash(string hash, PatchManifest plan) =>
        hash == plan.OriginalSha256 ? BitratePatchState.Original :
        hash == plan.PatchedSha256 ? BitratePatchState.Patched :
        throw new InvalidDataException("Driver hash matches neither the original nor the patched build.");
    private async Task<PatchManifest> ResolvePlanAsync(string path, byte[] data, CancellationToken ct)
    {
        var hash = Hash(data);
        if (hash is SupportedOriginalSha256 or SupportedPatchedSha256) return manifest;
        if (hash is SupportedCurrentOriginalSha256 or SupportedCurrentPatchedSha256) return manifest.ForCurrentBuild();
        if (hash is PatchManifest.Supported25998080OriginalSha256 or PatchManifest.Supported25998080PatchedSha256) return PatchManifest.Load25998080();
        if (hash is PatchManifest.SupportedStableOriginalSha256 or PatchManifest.SupportedStablePatchedSha256) return PatchManifest.LoadStable();
        try { return AdaptivePatchScanner.Discover(data); }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or InvalidOperationException)
        {
            // Receipt recognition requires the retained original to reproduce the
            // semantic discovery. Arbitrary receipt offsets are never trusted.
            var candidates = new List<PatchManifest>();
            foreach (var receiptPath in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".steam-frame-bitrate-*.bak.patch.json").Take(128))
            {
                PatchManifest receipt;
                try { receipt = await ReadReceiptAsync(receiptPath, ct); }
                catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or OverflowException) { continue; }
                if (receipt.PatchedSha256 != hash) continue;
                var backup = GetBackupPath(path, receipt.OriginalSha256);
                if (receiptPath != backup + ".patch.json") continue;
                try
                {
                    var original = await ReadVerifiedAsync(backup, receipt, false, ct);
                    RequireReceipt(receipt, PlanForOriginal(original)); ValidateSegments(data, receipt, true);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException or InvalidOperationException)
                { throw new IOException("The patch receipt matches this file, but its original backup or patch could not be verified: " + error.Message, error); }
                candidates.Add(receipt);
            }
            if (candidates.Count != 1) throw new InvalidDataException($"Driver layout check: {ex.Message} " +
                (candidates.Count == 0 ? "No verified patch receipt and matching original backup were found for this file." : "More than one patch receipt matches this file."), ex);
            return candidates[0];
        }
    }
    private static async Task<PatchManifest> ReadReceiptAsync(string path, CancellationToken ct)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Patch receipts must not be symbolic links.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > 1_048_576) throw new InvalidDataException("Patch receipt is too large.");
        var receipt = await JsonSerializer.DeserializeAsync<PatchManifest>(stream, cancellationToken: ct) ?? throw new InvalidDataException("Patch receipt is empty.");
        receipt.Validate(requirePinned: false); return receipt;
    }
    private static void RequireReceipt(PatchManifest actual, PatchManifest expected)
    { if (!actual.DescribesSamePatch(expected)) throw new InvalidDataException("The patch receipt does not match this exact build and patch; no changes are allowed."); }
    private static void RequireSupported(BitratePatchStatus status)
    { if (!status.IsSupported || status.BackupState == BitrateBackupState.Mismatch) throw new InvalidOperationException(status.Message); }
    private static async Task<byte[]> ReadVerifiedAsync(string path, PatchManifest plan, bool patched, CancellationToken ct)
    {
        var data = await ReadBoundedAsync(path, ct);
        if (data.Length != plan.FileLength) throw new InvalidDataException("Driver or backup length changed.");
        RequireHash(data, patched ? plan.PatchedSha256 : plan.OriginalSha256); ValidateSegments(data, plan, patched); return data;
    }
    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken ct)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Driver and backup files must not be symbolic links or reparse points.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 4096 or > 128 * 1024 * 1024) throw new InvalidDataException("Driver size is outside the supported bounds.");
        var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, ct); return bytes;
    }
    private static void ValidateSegments(byte[] data, PatchManifest plan, bool patched)
    { foreach (var change in plan.Changes) RequireSegment(data, change.FileOffset, patched ? change.PatchedBytes : change.OriginalBytes); }
    private static void RequireSegment(byte[] data, int offset, byte[] expected)
    {
        if (offset < 0 || offset > data.Length - expected.Length || !data.AsSpan(offset, expected.Length).SequenceEqual(expected))
            throw new InvalidDataException($"Patch preimage mismatch at file offset 0x{offset:x}; leaving the driver untouched.");
    }
    private static async Task EnsureBackupAsync(string path, byte[] bytes, PatchManifest plan, CancellationToken ct)
    {
        if (File.Exists(path)) { await ReadVerifiedAsync(path, plan, false, ct); return; }
        var temp = UniqueSibling(path, "backup");
        try
        {
            await WriteDurableAsync(temp, bytes, ct); await ReadVerifiedAsync(temp, plan, false, ct);
            try { File.Move(temp, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { await ReadVerifiedAsync(path, plan, false, ct); }
        }
        finally { DeleteOwnTemporary(temp); }
    }
    private static async Task EnsureReceiptAsync(string path, PatchManifest plan, CancellationToken ct)
    {
        if (File.Exists(path)) { RequireReceipt(await ReadReceiptAsync(path, ct), plan); return; }
        var temp = UniqueSibling(path, "receipt");
        try
        {
            await WriteDurableAsync(temp, JsonSerializer.SerializeToUtf8Bytes(plan, new JsonSerializerOptions { WriteIndented = true }), ct);
            RequireReceipt(await ReadReceiptAsync(temp, ct), plan);
            try { File.Move(temp, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { RequireReceipt(await ReadReceiptAsync(path, ct), plan); }
        }
        finally { DeleteOwnTemporary(temp); }
    }
    private async Task ReplaceGuardedAsync(string path, string backup, byte[] replacement, PatchManifest plan, bool restoring, CancellationToken ct)
    {
        var temp = UniqueSibling(path, "new"); var displaced = UniqueSibling(path, "displaced"); bool committed = false, verified = false;
        try
        {
            await WriteDurableAsync(temp, replacement, ct); await ReadVerifiedAsync(temp, plan, !restoring, ct);
            await ReadVerifiedAsync(path, plan, restoring, ct); await ReadVerifiedAsync(backup, plan, false, ct);
            var receiptRequired = !restoring || plan.OriginalSha256 is not (SupportedOriginalSha256 or SupportedCurrentOriginalSha256 or PatchManifest.SupportedStableOriginalSha256 or PatchManifest.Supported25998080OriginalSha256);
            if (receiptRequired || File.Exists(backup + ".patch.json")) RequireReceipt(await ReadReceiptAsync(backup + ".patch.json", ct), plan);
            ct.ThrowIfCancellationRequested(); EnsureVrServerStopped();
            File.Replace(temp, path, displaced, ignoreMetadataErrors: false); committed = true;
            await ReadVerifiedAsync(displaced, plan, restoring, CancellationToken.None);
            await ReadVerifiedAsync(path, plan, !restoring, CancellationToken.None); verified = true;
        }
        catch (Exception error) when (committed)
        { throw new IOException($"The driver changed during replacement or verification. The displaced file was retained at '{displaced}'. Verify SteamVR files before continuing.", error); }
        finally { DeleteOwnTemporary(temp); if (verified) DeleteOwnTemporary(displaced); }
    }
    private static async Task WriteDurableAsync(string path, byte[] bytes, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(flushToDisk: true);
    }
    private void EnsureVrServerStopped() { if (vrServerRunning()) throw new InvalidOperationException("Close SteamVR (vrserver.exe) before replacing the driver."); }
    private static bool IsVrServerRunning()
    {
        return RuntimeProcessState.IsRunning("vrserver");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void RequireHash(byte[] bytes, string expected)
    { if (Hash(bytes) != expected) throw new InvalidDataException("Driver or backup SHA256 mismatch; no changes are allowed."); }
    private static string NormalizePath(string path) { ArgumentException.ThrowIfNullOrWhiteSpace(path); return Path.GetFullPath(path); }
    private static string UniqueSibling(string path, string purpose) => Path.Combine(Path.GetDirectoryName(path)!, $".frame-{purpose}-{Guid.NewGuid():N}.tmp");
    private static void DeleteOwnTemporary(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
