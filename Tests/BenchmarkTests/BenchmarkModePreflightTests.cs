using SteamLinkVRSettings.Frame;

public sealed class BenchmarkModePreflightTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void StockDriverCannotStartAModeComparison(bool resolutions, bool rates)
    {
        var options = new BenchmarkOptions(TestResolutions: resolutions, TestRefreshRates: rates);
        var error = Assert.Throws<IOException>(() => BenchmarkRunner.ValidateModeComparison(options, false));
        Assert.Contains("HEVC 6.2", error.Message);
        Assert.Contains("Patches & Recovery", error.Message);
        BenchmarkRunner.ValidateModeComparison(options, true);
    }

    [Fact]
    public void StockDriverCanStillTestItsCurrentDisplayMode()
    {
        BenchmarkRunner.ValidateModeComparison(new(TestResolutions: false, TestRefreshRates: false), false);
    }
}
