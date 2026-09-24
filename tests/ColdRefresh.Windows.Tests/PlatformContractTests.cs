namespace ColdRefresh.Windows.Tests;

public sealed class PlatformContractTests
{
    [Fact]
    public void TestsRunOnWindows() => Assert.True(OperatingSystem.IsWindows());
}
