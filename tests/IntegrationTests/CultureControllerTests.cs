using System.Net;
using System.Text.RegularExpressions;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class CultureControllerTests : TestBase
{
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("zh-CN")]
    [DataRow("zh-TW")]
    [DataRow("fr-FR")]
    [DataRow("ja-JP")]
    [DataRow("ar-SA")]
    public async Task LockboxProductNameStaysLiteralInNavigationAndHeading(string culture)
    {
        await LoginAsAdmin();
        using var setting = await Http.GetAsync($"/Culture/Set?culture={culture}&returnUrl=/Dms/Lockboxes");
        Assert.AreEqual(HttpStatusCode.Found, setting.StatusCode);
        var html = WebUtility.HtmlDecode(await Http.GetStringAsync("/Dms/Lockboxes"));
        StringAssert.Contains(html, "<h1>Lockbox</h1>");
        StringAssert.Matches(html, new Regex(@"<a[^>]*href=""/Dms/Lockboxes""[^>]*>\s*Lockbox\s*</a>"));
        var mine = WebUtility.HtmlDecode(await Http.GetStringAsync("/DmsAccess/MyLockboxes"));
        StringAssert.Matches(mine, new Regex(@"<h1>[^<]*Lockbox[^<]*</h1>"));
        Assert.DoesNotContain("{0}", mine);
    }

    [TestMethod]
    public async Task SetCulture()
    {
        var url = "/Culture/Set?culture=en&returnUrl=/";
        var response = await Http.GetAsync(url);
        
        // Assert
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
    }

    [TestMethod]
    public async Task SetCultureEmpty()
    {
        var url = "/Culture/Set?culture=&returnUrl=/";
        var response = await Http.GetAsync(url);
        
        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    [DataRow("zh-CN&returnUrl=/Tips/Detail/8")]
    [DataRow("not/a/culture")]
    public async Task SetCultureInvalid(string culture)
    {
        var response = await Http.GetAsync($"/Culture/Set?culture={Uri.EscapeDataString(culture)}&returnUrl=/");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsFalse(response.Headers.Contains("Set-Cookie"));
    }

    [TestMethod]
    public async Task SetCultureNonLocalReturn()
    {
        var url = "/Culture/Set?culture=en&returnUrl=https://google.com";
        var response = await Http.GetAsync(url);
        
        // Assert
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.AreEqual("/", response.Headers.Location?.OriginalString);
    }
}
