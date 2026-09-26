using DshDesktop.Domain;
using FluentAssertions;

namespace DshDesktop.Domain.Tests;

/// <summary>
/// 外部链接判定。核心是<b>默认正则的边界</b>：它必须放行域名主机名、拦住 IP 字面量与
/// localhost，且不能误伤 <c>localhost.example.com</c> 这种"以 localhost 开头但其实是域名"的主机。
/// </summary>
public class ExternalLinkPolicyTests
{
    private static ExternalLinkPolicy Default() =>
        new(enabled: true, ExternalLinkPolicy.DefaultUrlRegex);

    // ---- 默认正则：放行域名主机名 ----

    [Theory]
    [InlineData("https://github.com/deepseek-ai/deepseek-harness")]
    [InlineData("http://example.com")]
    [InlineData("http://example.com:8080/path?q=1#f")]
    [InlineData("https://sub.domain.co.uk/x")]
    [InlineData("http://mypc.local:3080/")]
    [InlineData("https://example.com")]
    [InlineData("http://example.com?x=1")]
    [InlineData("http://xn--fiqs8s.example/")]
    public void Decide_DefaultRegex_OpensDomainHostsInBrowser(string url)
    {
        Default().Decide(url).Should().Be(ExternalLinkDecision.OpenInBrowser);
    }

    /// <summary>
    /// 朴素写法 <c>^https?://localhost</c> 会把 <c>localhost.example.com</c> 误判成本机。
    /// 默认正则用 <c>[/?#]|$</c> 收尾，所以这个域名必须仍然交给浏览器。
    /// </summary>
    [Theory]
    [InlineData("http://localhost.example.com/")]
    [InlineData("http://localhost.example.com:9999/")]
    [InlineData("http://localhostx/")]
    public void Decide_DefaultRegex_DoesNotMistakeLocalhostPrefixForLoopback(string url)
    {
        Default().Decide(url).Should().Be(ExternalLinkDecision.OpenInBrowser);
    }

    // ---- 默认正则：拦住本机目标 ----

    [Theory]
    [InlineData("http://127.0.0.1:3080/")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://0.0.0.0:3080/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://192.168.1.5:3080/")]
    [InlineData("https://127.0.0.1:3080/?token=abc")]
    public void Decide_DefaultRegex_KeepsIpv4LiteralsInApp(string url)
    {
        Default().Decide(url).Should().Be(ExternalLinkDecision.KeepDefault);
    }

    [Theory]
    [InlineData("http://[::1]:3080/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    public void Decide_DefaultRegex_KeepsIpv6LiteralsInApp(string url)
    {
        Default().Decide(url).Should().Be(ExternalLinkDecision.KeepDefault);
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:3080/")]
    [InlineData("http://localhost/")]
    [InlineData("http://LOCALHOST:3080/")]
    [InlineData("https://LocalHost:3080/?token=abc")]
    public void Decide_DefaultRegex_KeepsLocalhostInApp_CaseInsensitively(string url)
    {
        Default().Decide(url).Should().Be(ExternalLinkDecision.KeepDefault);
    }

    /// <summary>只有 scheme、没有主机名的 URL 必须落选（正则第三段要求至少一个字母）。</summary>
    [Theory]
    [InlineData("http://")]
    [InlineData("http:///path")]
    public void Decide_DefaultRegex_KeepsHostlessUrlInApp(string url)
    {
        Default().Decide(url).Should().Be(ExternalLinkDecision.KeepDefault);
    }

    // ---- 非 http(s)：一律保持 WebView2 原生行为 ----

    /// <summary>
    /// <c>about:blank</c>（打印预览 / 预开窗）、<c>blob:</c>（下载）、<c>data:</c> 被一刀切
    /// <c>Handled = true</c> 会把打印和下载打断，所以必须原样放过。
    /// </summary>
    [Theory]
    [InlineData("about:blank")]
    [InlineData("blob:https://example.com/abc")]
    [InlineData("data:text/html,<h1>x</h1>")]
    [InlineData("mailto:a@b.com")]
    [InlineData("file:///C:/tmp/x.html")]
    [InlineData("ftp://example.com/x")]
    [InlineData("javascript:void(0)")]
    public void Decide_NonHttpScheme_KeepsDefaultBehaviour(string url)
    {
        Default().Decide(url).Should().Be(ExternalLinkDecision.KeepDefault);
    }

    // ---- 开关 / 正则禁用 / 非法输入 ----

    [Fact]
    public void Decide_Disabled_KeepsEverythingInApp()
    {
        var policy = new ExternalLinkPolicy(enabled: false, ExternalLinkPolicy.DefaultUrlRegex);

        policy.IsEnabled.Should().BeFalse();
        policy.Decide("https://github.com/x").Should().Be(ExternalLinkDecision.KeepDefault);
    }

    /// <summary>
    /// 空/空白正则 = 功能禁用。绝不能把空串交给 <c>new Regex("")</c> —— 空模式匹配任意字符串，
    /// 会把同源链接也一起丢给浏览器（与 <c>successMarkerRegex</c> 的约定一致）。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Decide_BlankRegex_DisablesPolicy(string? pattern)
    {
        var policy = new ExternalLinkPolicy(enabled: true, pattern);

        policy.IsEnabled.Should().BeFalse();
        policy.Decide("https://github.com/x").Should().Be(ExternalLinkDecision.KeepDefault);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Decide_BlankUrl_KeepsDefault(string? url)
    {
        Default().Decide(url).Should().Be(ExternalLinkDecision.KeepDefault);
    }

    /// <summary>手改 config.json 留下非法正则时降级为禁用并记录原因，绝不抛异常。</summary>
    [Fact]
    public void Ctor_InvalidRegex_DisablesPolicyAndRecordsReason()
    {
        var act = () => new ExternalLinkPolicy(enabled: true, "([unclosed");

        var policy = act.Should().NotThrow().Subject;

        policy.IsEnabled.Should().BeFalse();
        policy.PatternError.Should().NotBeNullOrWhiteSpace();
        policy.Decide("https://github.com/x").Should().Be(ExternalLinkDecision.KeepDefault);
    }

    [Fact]
    public void Ctor_ValidRegex_HasNoPatternError()
    {
        Default().PatternError.Should().BeNull();
        Default().IsEnabled.Should().BeTrue();
    }

    // ---- 自定义正则：判定权完全在用户手里 ----

    [Fact]
    public void Decide_CustomRegex_TakesPrecedenceOverDefaults()
    {
        // 用户把正则收窄到只放行 github：example.com 就不再接管。
        var policy = new ExternalLinkPolicy(enabled: true, @"^https://github\.com/");

        policy.Decide("https://github.com/x").Should().Be(ExternalLinkDecision.OpenInBrowser);
        policy.Decide("https://example.com/x").Should().Be(ExternalLinkDecision.KeepDefault);
    }

    /// <summary>正则被写成 <c>.*</c> 时会连本机链接一起接管 —— 这是"判定权在用户手里"的已知代价，钉住行为而不是假装不存在。</summary>
    [Fact]
    public void Decide_CatchAllRegex_AlsoTakesLoopback()
    {
        var policy = new ExternalLinkPolicy(enabled: true, ".*");

        policy.Decide("http://127.0.0.1:3080/").Should().Be(ExternalLinkDecision.OpenInBrowser);
    }

    /// <summary>带 userinfo 的 URL 会命中默认正则（主机段含字母）—— 用户已确认接受这个漏网。</summary>
    [Theory]
    [InlineData("http://user@127.0.0.1:3080/")]
    [InlineData("https://user:pass@example.com/x")]
    public void Decide_DefaultRegex_UserInfoUrls_AreOpenedInBrowser(string url)
    {
        Default().Decide(url).Should().Be(ExternalLinkDecision.OpenInBrowser);
    }

    /// <summary>默认正则本身必须能编译（常量写错一个字符就会在这里红，而不是等到运行时）。</summary>
    [Fact]
    public void DefaultUrlRegex_IsCompilableAndMatchesDefaultConfig()
    {
        var policy = Default();

        policy.PatternError.Should().BeNull();
        policy.IsEnabled.Should().BeTrue();
        AppConfig.CreateDefault().ExternalLinkUrlRegex.Should().Be(ExternalLinkPolicy.DefaultUrlRegex);
        AppConfig.CreateDefault().OpenExternalLinksEnabled.Should().BeTrue();
    }
}
