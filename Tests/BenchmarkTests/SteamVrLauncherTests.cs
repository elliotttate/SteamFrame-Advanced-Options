using SteamLinkVRSettings.Frame;

public sealed class SteamVrLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "steamvr-launcher-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CustomRuntimeUsesItsOwnLauncherWithoutASteamAppLaunchRequest()
    {
        var steam = Path.Combine(_root, "Steam");
        var runtime = Path.Combine(_root, "Separate library", "SteamVR");
        Touch(Path.Combine(steam, "steam.exe"));
        var launcher = Path.Combine(runtime, "bin", "win64", "vrstartup.exe");
        Touch(launcher);
        var info = SteamVrRuntime.CreateStartInfo(new(steam, runtime));
        Assert.Equal(launcher, info.FileName);
        Assert.Equal(runtime, info.WorkingDirectory);
        Assert.Empty(info.ArgumentList);
        Assert.False(info.UseShellExecute);
    }

    [Fact]
    public void MissingSelectedRuntimeCannotFallBackToAnotherLibrary()
    {
        var steam = Path.Combine(_root, "Steam");
        Touch(Path.Combine(steam, "steam.exe"));
        Touch(Path.Combine(steam, "steamapps", "common", "SteamVR", "bin", "win64", "vrstartup.exe"));
        var selected = Path.Combine(_root, "Selected runtime");
        var error = Assert.Throws<FileNotFoundException>(() => SteamVrRuntime.CreateStartInfo(new(steam, selected)));
        Assert.Equal(Path.Combine(selected, "bin", "win64", "vrstartup.exe"), error.FileName);
    }

    private static void Touch(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "test fixture, never executed");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
