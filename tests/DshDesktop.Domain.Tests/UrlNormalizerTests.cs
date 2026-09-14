using DshDesktop.Domain;
using FluentAssertions;

namespace DshDesktop.Domain.Tests;

public class UrlNormalizerTests
{
    [Theory]
    [InlineData("127.0.0.1:3080/?token=abc", "http://127.0.0.1:3080/?token=abc")]
    [InlineData("127.0.0.1:3080/", "http://127.0.0.1:3080/")]
    [InlineData("localhost:3080", "http://localhost:3080")]
    [InlineData("example.com/path?q=1", "http://example.com/path?q=1")]
    public void EnsureScheme_AddsHttpWhenMissing(string input, string expected)
    {
        UrlNormalizer.EnsureScheme(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("http://127.0.0.1:3080/?token=abc")]
    [InlineData("https://localhost:443/x")]
    [InlineData("HTTP://127.0.0.1:3080/")]
    [InlineData("HTTPS://example.com/")]
    public void EnsureScheme_LeavesHttpAndHttpsUntouched(string input)
    {
        UrlNormalizer.EnsureScheme(input).Should().Be(input);
    }

    [Fact]
    public void EnsureScheme_TrimsSurroundingWhitespace()
    {
        // 从浏览器地址栏复制常带首尾空白/换行
        UrlNormalizer.EnsureScheme("  127.0.0.1:3080/?token=abc  ")
            .Should().Be("http://127.0.0.1:3080/?token=abc");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void EnsureScheme_ReturnsNullForBlank(string? input)
    {
        UrlNormalizer.EnsureScheme(input).Should().BeNull();
    }

    [Theory]
    [InlineData("file:///C:/tmp/x.html")]
    [InlineData("ftp://example.com/x")]
    public void EnsureScheme_DoesNotPrependHttpToOtherSchemes(string input)
    {
        // 别的 scheme 原样放过：加 http:// 会把用户的原意彻底改错。
        // 该不该导航由后续流程决定，不在这里"顺手"改。
        UrlNormalizer.EnsureScheme(input).Should().Be(input);
    }

    /// <summary>
    /// 关键区分：<c>127.0.0.1:3080/</c> 里也有冒号，但 <c>127.0.0.1</c> 以数字开头、
    /// 不是合法 scheme，所以必须补 http://。这条要是判反，最常用的输入就全废了。
    /// </summary>
    [Fact]
    public void EnsureScheme_TreatsHostPortAsHostPort_NotAsScheme()
    {
        UrlNormalizer.EnsureScheme("127.0.0.1:3080/")
            .Should().Be("http://127.0.0.1:3080/");
    }

    /// <summary>token 绝不能因为"规整"而丢失 —— 它就是导航能不能成功的关键（§5.4 只要求不落盘）。</summary>
    [Fact]
    public void EnsureScheme_PreservesQueryTokenVerbatim()
    {
        const string token = "aHuVnlzmAn7t_OBIN3qJb6M8zD1CV4MV_HvwpHydzFM";
        var result = UrlNormalizer.EnsureScheme($"127.0.0.1:3080/?token={token}");

        result.Should().Be($"http://127.0.0.1:3080/?token={token}");
        result.Should().Contain(token);
    }
}
