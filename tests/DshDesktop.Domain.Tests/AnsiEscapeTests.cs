using DshDesktop.Domain;
using FluentAssertions;

namespace DshDesktop.Domain.Tests;

public class AnsiEscapeTests
{
    private const string DefaultUrlRegex = @"dsh web:\s*(?<url>https?://\S+)";
    private const string DefaultMarkerRegex = @"dsh web|listening|ready";

    [Fact]
    public void Strip_RemovesSgrColorCodes()
    {
        AnsiEscape.Strip("\u001B[36mhello\u001B[0m").Should().Be("hello");
        AnsiEscape.Strip("\u001B[1;32mready\u001B[39m").Should().Be("ready");
    }

    [Fact]
    public void Strip_RemovesOscEscapesButKeepsVisibleText()
    {
        // OSC 8 终端超链接：URL 在控制序列里，可见文本也是 URL
        const string line = "\u001B]8;;http://127.0.0.1:3080/tok/abc\u0007http://127.0.0.1:3080/tok/abc\u001B]8;;\u0007";
        AnsiEscape.Strip(line).Should().Be("http://127.0.0.1:3080/tok/abc");
    }

    [Fact]
    public void Strip_RemovesStrayEscAndBel()
    {
        AnsiEscape.Strip("a\u001B\u0007b").Should().Be("ab");
    }

    [Fact]
    public void Strip_LeavesPlainTextUntouched()
    {
        const string line = "dsh web: http://127.0.0.1:3080/tok/abc";
        AnsiEscape.Strip(line).Should().Be(line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Strip_HandlesNullAndEmpty(string? input)
    {
        AnsiEscape.Strip(input).Should().BeEmpty();
    }

    // ---- 回归：上色行会把复位序列粘进 URL,导致 404 ----

    [Fact]
    public void ColoredUrl_WithoutStrip_CaptureIsPolluted()
    {
        // 记录病因本身：这就是"服务是活的却回 404"的来源
        const string line = "\u001B[36mdsh web: http://127.0.0.1:3080/tok/abc\u001B[0m";
        var extractor = new UrlExtractor(DefaultUrlRegex, DefaultMarkerRegex);

        var polluted = extractor.TryExtract(line);

        polluted.Should().Be("http://127.0.0.1:3080/tok/abc\u001B[0m");
        polluted!.Should().Contain("\u001B");
    }

    [Fact]
    public void ColoredUrl_WithStrip_CaptureIsClean()
    {
        const string line = "\u001B[36mdsh web: http://127.0.0.1:3080/tok/abc\u001B[0m";
        var extractor = new UrlExtractor(DefaultUrlRegex, DefaultMarkerRegex);

        var url = extractor.TryExtract(AnsiEscape.Strip(line));

        url.Should().Be("http://127.0.0.1:3080/tok/abc");
        Uri.TryCreate(url, UriKind.Absolute, out _).Should().BeTrue();
    }

    [Fact]
    public void ColoredSuccessMarker_StillMatchesAfterStrip()
    {
        var extractor = new UrlExtractor(DefaultUrlRegex, DefaultMarkerRegex);
        extractor.IsSuccessMarker(AnsiEscape.Strip("\u001B[32mlistening on 3080\u001B[0m"))
            .Should().BeTrue();
    }
}
