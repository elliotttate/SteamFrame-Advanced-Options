using System.Text.RegularExpressions;

namespace SteamLinkVRSettings.Frame;

// Build/branch metadata is diagnostic only. Patch authorization always comes from
// the driver's bytes, never from a manifest's version number or selected branch.
internal static class DriverCompatibility
{
    public const string VerifiedVersions = "Verified bitrate-patch builds: 25998080, public 25330290 and beta 25839986, plus the earlier beta driver. Auto tune sender layouts: 25998080 and 25839986. Other builds must pass the same driver checks. Codec and custom quality support are checked separately.";

    public static string ReferenceDriver(string? hash) => hash switch
    {
        PatchManifest.Supported25998080OriginalSha256 or PatchManifest.Supported25998080PatchedSha256 => "SteamVR 2.18.4, build 25998080",
        PatchManifest.SupportedStableOriginalSha256 or PatchManifest.SupportedStablePatchedSha256 => "Public build 25330290",
        PatchManifest.SupportedCurrentOriginalSha256 or PatchManifest.SupportedCurrentPatchedSha256 => "Beta build 25839986",
        PatchManifest.SupportedOriginalSha256 or PatchManifest.SupportedPatchedSha256 => "Earlier verified beta driver",
        _ => "No reference hash match"
    };

    public static string InstalledVersion(string driverPath)
    {
        try
        {
            var file = Path.GetFullPath(driverPath);
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", ".."));
            if (!file.Equals(Path.Combine(root, "drivers", "vrlink", "bin", "win64", "driver_vrlink.dll"), StringComparison.OrdinalIgnoreCase))
                return "SteamVR build unavailable for this file";
            var common = Directory.GetParent(root);
            if (common?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) != true ||
                common.Parent?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) != true)
                return "SteamVR build unavailable for this file";
            var manifest = Path.Combine(common.Parent.FullName, "appmanifest_250820.acf");
            using var stream = File.OpenRead(manifest);
            if (stream.Length > 1_048_576) return "SteamVR build unavailable (manifest too large)";
            using var reader = new StreamReader(stream);
            return DescribeManifest(reader.ReadToEnd(), Path.GetFileName(root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or RegexMatchTimeoutException)
        { return "SteamVR build unavailable for this file"; }
    }

    internal static string DescribeManifest(string text, string directory)
    {
        if (Value(text, "appid") != "250820" || !string.Equals(Value(text, "installdir"), directory, StringComparison.OrdinalIgnoreCase))
            return "SteamVR build unavailable (manifest does not match this installation)";
        var build = Value(text, "buildid");
        if (build.Length is 0 or > 20 || !build.All(char.IsAsciiDigit)) return "SteamVR build unavailable (invalid build ID)";
        // UserConfig can already name the next branch while a download is pending.
        // MountedConfig identifies the installed content, not the requested update.
        var mounted = Regex.Match(text, "\"MountedConfig\"\\s*\\{([^{}]*)\\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var branch = mounted.Success ? Value(mounted.Groups[1].Value, "BetaKey") : "";
        var label = branch switch
        {
            "public" => "public / stable",
            "beta" => "beta",
            "" => "branch unavailable",
            _ => branch.Length <= 64 && branch.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? branch : "branch unavailable"
        };
        return $"Installed SteamVR: build {build} ({label})";
    }

    private static string Value(string text, string key) => Regex.Match(text,
        "\"" + Regex.Escape(key) + "\"\\s+\"([^\"\\r\\n]*)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Groups[1].Value;
}
