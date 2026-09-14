using DshDesktop.Domain;
using FluentAssertions;

namespace DshDesktop.Domain.Tests;

public class UrlExtractorTests
{
    private readonly UrlExtractor _sut = new(
        @"dsh web:\s*(?<url>https?://\S+)",
        @"dsh web|listening|ready|启动成功");

    [Theory]
    [InlineData("dsh web: https://127.0.0.1:3080/token/abc")]
    [InlineData("info: dsh web: http://localhost:9999/")]
    [InlineData("  dsh web:  https://10.0.0.5:443/  ")]
    public void TryExtract_ReturnsUrl_WhenRegexMatches(string line)
    {
        var url = _sut.TryExtract(line);
        url.Should().NotBeNull();
        url.Should().StartWith("http");
    }

    [Fact]
    public void TryExtract_ReturnsNull_WhenRegexDoesNotMatch()
    {
        var url = _sut.TryExtract("some random log line without url");
        url.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    public void TryExtract_ReturnsNull_ForEmptyLine(string line)
    {
        _sut.TryExtract(line).Should().BeNull();
    }

    [Theory]
    [InlineData("dsh web starting up")]
    [InlineData("Server listening on port 3080")]
    [InlineData("service ready")]
    [InlineData("服务启动成功")]
    public void IsSuccessMarker_ReturnsTrue_WhenMarkerMatches(string line)
    {
        _sut.IsSuccessMarker(line).Should().BeTrue();
    }

    [Fact]
    public void IsSuccessMarker_ReturnsFalse_WhenNoMatch()
    {
        _sut.IsSuccessMarker("nothing relevant here").Should().BeFalse();
    }

    [Fact]
    public void IsSuccessMarker_ReturnsFalse_ForEmptyLine()
    {
        _sut.IsSuccessMarker("").Should().BeFalse();
    }

    // —— 空成功标志 = 这一级禁用（默认值，2026-09-14 用户要求）——
    // 回归点：空串交给 Regex 会匹配**任意**行，于是启动输出第一行就被当成
    // "服务已就绪"。这里锁死"空配置 = 永不命中"，防止有人把 null 判断删掉。

    [Theory]
    [InlineData("dsh web starting up")]
    [InlineData("Server listening on port 3080")]
    [InlineData("服务启动成功")]
    [InlineData("随便一行没有任何关键字的输出")]
    [InlineData("")]
    public void IsSuccessMarker_ReturnsFalse_WhenMarkerRegexIsEmpty(string line)
    {
        var extractor = new UrlExtractor(@"dsh web:\s*(?<url>https?://\S+)", string.Empty);
        extractor.IsSuccessMarker(line).Should().BeFalse();
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    public void IsSuccessMarker_ReturnsFalse_WhenMarkerRegexIsWhitespace(string line)
    {
        var extractor = new UrlExtractor(@"dsh web:\s*(?<url>https?://\S+)", line);
        extractor.IsSuccessMarker("listening on 3080").Should().BeFalse();
    }

    [Fact]
    public void IsSuccessMarker_StillWorks_WhenMarkerRegexProvided()
    {
        // 对照组：确认上面的 false 来自"禁用"，而不是断言写错了方向。
        var extractor = new UrlExtractor(@"dsh web:\s*(?<url>https?://\S+)", @"listening");
        extractor.IsSuccessMarker("listening on 3080").Should().BeTrue();
    }

    [Fact]
    public void TryExtract_ReturnsNull_WhenRegexHasNoUrlGroup()
    {
        var extractor = new UrlExtractor(@"dsh web:\s*\S+", @"listening");
        var url = extractor.TryExtract("dsh web: https://x/");
        url.Should().BeNull();
    }

    [Fact]
    public void TryExtract_ReturnsOnlyUrlGroupValue()
    {
        var url = _sut.TryExtract("dsh web: https://127.0.0.1:3080/tok/xyz trailing text");
        url.Should().Be("https://127.0.0.1:3080/tok/xyz");
    }
}
