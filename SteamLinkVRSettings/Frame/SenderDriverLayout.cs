namespace SteamLinkVRSettings.Frame;

// These are independent sender-layout profiles, not bitrate-patcher compatibility.
// Unknown builds must be inspected before their private object graph can be read.
internal sealed record SenderDriverLayout(int ServerRva, (int Rva, int Length)[] CodeRanges)
{
    internal const string PublicOriginal = PatchManifest.Supported25998080OriginalSha256;
    internal const string PublicPatched = PatchManifest.Supported25998080PatchedSha256;

    internal static SenderDriverLayout Resolve(string sha256) => sha256 switch
    {
        PatchManifest.SupportedCurrentOriginalSha256 or PatchManifest.SupportedCurrentPatchedSha256
            => new(0x179d840, [(0xA48170, 0x1c9), (0xA4A510, 0x12e)]),
        PublicOriginal or PublicPatched
            => new(0x179d840, [(0xA482E0, 0x1be), (0xA4A730, 0x1c0)]),
        _ => throw new IOException("This SteamVR driver has no verified active-sender layout. Update this app before running auto tune; changing the bitrate patch alone cannot add sender support.")
    };
}
