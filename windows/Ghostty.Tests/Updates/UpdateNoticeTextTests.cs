using Ghostty.Core.Updates;
using Xunit;

namespace Ghostty.Tests.Updates;

public class UpdateNoticeTextTests
{
    [Fact]
    public void Copy_IsProductCopy()
    {
        Assert.Equal("Update ready", UpdateNoticeText.Title);
        Assert.Equal("Close Wintty and update", UpdateNoticeText.AcceptLabel);
    }

    [Fact]
    public void Body_NamesVersionAndWarnsAboutTerminals()
    {
        var body = UpdateNoticeText.Body("1.2.4");
        Assert.Contains("1.2.4", body);
        Assert.Contains("closes all windows", body);
        Assert.Contains("terminal", body);
    }

    [Fact]
    public void Copy_HasNoSubscriptionLanguage()
    {
        // Product copy: no subscription or Pro labelling anywhere.
        var body = UpdateNoticeText.Body("1.2.4");
        foreach (var text in new[] { UpdateNoticeText.Title, UpdateNoticeText.AcceptLabel, body })
        {
            Assert.DoesNotContain("Pro", text);
            Assert.DoesNotContain("subscri", text);
        }
    }
}
