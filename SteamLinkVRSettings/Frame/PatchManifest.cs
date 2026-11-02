using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamLinkVRSettings.Frame;

/// <summary>Only byte deltas are distributed; the Valve DLL stays on the user's PC.</summary>
public sealed class PatchManifest
{
    public const string SupportedOriginalSha256 = "5f6f063f1ece088491f572db8feb2f2433a5621d0adf7013de77b91604d146b5";
    public const string SupportedPatchedSha256 = "82fd637f1d4b844ca349d2a98867cd0ba8f969a14de47c2a3192b875fb62425d";
    public const string SupportedCurrentOriginalSha256 = "d415ba30fab129b4737ae19a3c71d7628c27e450dd8bf0002a1d4dd98ed58a05";
    public const string SupportedCurrentPatchedSha256 = "34265101d56235e2f87e5b0e2f3fc2340ed1513a9b6d26f03c4fe12bafcbbc03";
    public const string Supported25998080OriginalSha256 = "8e7100b6b1e88d3ad6dd05f30f030dc9a69b092b64a60d35347cebb5c516b55d";
    public const string Supported25998080PatchedSha256 = "1c4290b55f012d7bf2c427117a25455a0ed54ee9dce15e3ab2f2ae407a8aa354";
    public const string SupportedStableOriginalSha256 = "1e846be041039798580a91452371f6288c614fb4c152126b137e2c693fb77eef";
    public const string SupportedStablePatchedSha256 = "d0aeec1b80d31da146365b4b65fa4adadd7230d288ebf0d65e040b3991f28c5e";
    public const int SupportedFileLength = 13927576;
    public const int SupportedCapMbps = 500;
    public const int SupportedHevcLevel = 186; // NVENC's HEVC level 6.2 value.
    public const string ResourceName = "SteamLinkVRSettings.Assets.bitrate-patch.json";

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }
    [JsonPropertyName("file_length")]
    public int FileLength { get; init; }
    [JsonPropertyName("original_sha256")]
    public string OriginalSha256 { get; init; } = "";
    [JsonPropertyName("patched_sha256")]
    public string PatchedSha256 { get; init; } = "";
    [JsonPropertyName("cap_mbps")]
    public int CapMbps { get; init; }
    [JsonPropertyName("hevc_level")]
    public int HevcLevel { get; init; }
    [JsonPropertyName("changes")]
    public IReadOnlyList<PatchSegment> Changes { get; init; } = [];

    public static PatchManifest LoadSupported()
    {
        using var resource = typeof(PatchManifest).Assembly.GetManifestResourceStream(ResourceName);
        if (resource is not null)
            return Read(resource);
        // Allows deployment as content as well as the preferred embedded resource.
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assets", "bitrate-patch.json"));
        return Read(file);
    }

    public static PatchManifest Read(Stream stream, bool requirePinned = true)
    {
        var result = JsonSerializer.Deserialize<PatchManifest>(stream)
            ?? throw new InvalidDataException("The bitrate patch manifest is empty.");
        result.Validate(requirePinned);
        return result;
    }

    public static PatchManifest LoadStable()
    {
        using var resource = typeof(PatchManifest).Assembly.GetManifestResourceStream("SteamLinkVRSettings.Assets.bitrate-patch-stable.json")
            ?? File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assets", "bitrate-patch-stable.json"));
        var result = Read(resource, requirePinned: false);
        if (result.FileLength != 13908632 || result.OriginalSha256 != SupportedStableOriginalSha256 || result.PatchedSha256 != SupportedStablePatchedSha256)
            throw new InvalidDataException("The manifest does not describe the verified stable SteamVR patch.");
        return result;
    }

    public static PatchManifest Load25998080()
    {
        using var resource = typeof(PatchManifest).Assembly.GetManifestResourceStream("SteamLinkVRSettings.Assets.bitrate-patch-25998080.json")
            ?? File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assets", "bitrate-patch-25998080.json"));
        var result = Read(resource, requirePinned: false);
        if (result.FileLength != 13928600 || result.OriginalSha256 != Supported25998080OriginalSha256 || result.PatchedSha256 != Supported25998080PatchedSha256)
            throw new InvalidDataException("The manifest does not describe the verified SteamVR 25998080 patch.");
        return result;
    }

    public void Validate(bool requirePinned = true)
    {
        if (SchemaVersion != 1 || FileLength < 4096 || FileLength > 128 * 1024 * 1024 || CapMbps != SupportedCapMbps ||
            HevcLevel != SupportedHevcLevel || !ValidHash(OriginalSha256) || !ValidHash(PatchedSha256) ||
            OriginalSha256 == PatchedSha256 || Changes is null || Changes.Count != 6 ||
            (requirePinned && (FileLength != SupportedFileLength || OriginalSha256 != SupportedOriginalSha256 || PatchedSha256 != SupportedPatchedSha256)))
            throw new InvalidDataException("The manifest does not describe the supported SteamVR patch.");

        if (Changes.Any(change => change is null))
            throw new InvalidDataException("A patch segment is missing.");
        var end = 0;
        foreach (var change in Changes.OrderBy(segment => segment.FileOffset))
        {
            if (change is null)
                throw new InvalidDataException("A patch segment is missing.");
            var before = change.OriginalBytes;
            var after = change.PatchedBytes;
            if (before.Length == 0 || before.Length != after.Length || change.FileOffset < end ||
                change.FileOffset > FileLength - before.Length)
                throw new InvalidDataException("A patch segment is empty, overlaps another, or exceeds the DLL.");
            end = change.FileOffset + before.Length;
        }
    }

    public PatchManifest ForCurrentBuild() => new()
    {
        SchemaVersion = SchemaVersion, FileLength = FileLength, CapMbps = CapMbps, HevcLevel = HevcLevel,
        OriginalSha256 = SupportedCurrentOriginalSha256, PatchedSha256 = SupportedCurrentPatchedSha256, Changes = Changes
    };

    public bool DescribesSamePatch(PatchManifest other) => SchemaVersion == other.SchemaVersion &&
        FileLength == other.FileLength && CapMbps == other.CapMbps && HevcLevel == other.HevcLevel &&
        OriginalSha256 == other.OriginalSha256 && PatchedSha256 == other.PatchedSha256 &&
        Changes.OrderBy(x => x.FileOffset).Select(x => (x.FileOffset, x.OriginalHex, x.PatchedHex))
            .SequenceEqual(other.Changes.OrderBy(x => x.FileOffset).Select(x => (x.FileOffset, x.OriginalHex, x.PatchedHex)));

    private static bool ValidHash(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class PatchSegment
{
    [JsonPropertyName("file_offset")]
    public int FileOffset { get; init; }
    [JsonPropertyName("old_hex")]
    public string OriginalHex { get; init; } = "";
    [JsonPropertyName("new_hex")]
    public string PatchedHex { get; init; } = "";
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";

    [JsonIgnore]
    public byte[] OriginalBytes => Decode(OriginalHex);
    [JsonIgnore]
    public byte[] PatchedBytes => Decode(PatchedHex);

    private static byte[] Decode(string hex)
    {
        try { return Convert.FromHexString(hex); }
        catch (Exception error) when (error is FormatException or ArgumentNullException)
        { throw new InvalidDataException("A patch segment contains invalid hexadecimal bytes.", error); }
    }
}
