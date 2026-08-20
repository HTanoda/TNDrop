using TNDrop.Core;

namespace TNDrop.Tests;

public class UrlDetectorTests
{
    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("  https://example.com/path?q=1  ", true)]
    [InlineData("http://intra.city.local/keiji", true)]
    [InlineData("example.com", false)]                       // スキームなしは Text 扱い
    [InlineData("ftp://example.com", false)]
    [InlineData("https://example.com\n2行目", false)]
    [InlineData("参照: https://example.com を見て", false)]
    public void IsUrl_detects_single_http_url(string input, bool expected)
        => Assert.Equal(expected, UrlDetector.IsUrl(input));

    [Fact]
    public void GetDomain_extracts_host()
        => Assert.Equal("a.example.co.jp", UrlDetector.GetDomain("https://a.example.co.jp/x?y=1"));
}
