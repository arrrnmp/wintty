using Ghostty.Core.Updates;
using Xunit;

namespace Ghostty.Tests.Updates;

public class UpdateSharedRootTests
{
    [Fact]
    public void Prefers_The_Locator_Root()
    {
        Assert.Equal(
            "C:\\Users\\me\\AppData\\Local\\arrrnmp.wintty.oss",
            UpdatePolicy.SharedRoot(
                "C:\\Users\\me\\AppData\\Local\\arrrnmp.wintty.oss",
                "C:\\Users\\me\\AppData\\Local\\arrrnmp.wintty.oss\\current"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Falls_Back_To_The_App_Directory(string? locatorRoot)
    {
        Assert.Equal(
            "C:\\fallback\\current",
            UpdatePolicy.SharedRoot(locatorRoot, "C:\\fallback\\current"));
    }
}
