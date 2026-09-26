using System.Text.RegularExpressions;

namespace DshDesktop.Domain;

/// <summary>一个链接请求的处理结论。</summary>
public enum ExternalLinkDecision
{
    /// <summary>不处理：保持 WebView2 原生行为（新窗口请求弹 WebView2 自带窗口，同标签导航照常）。</summary>
    KeepDefault,

    /// <summary>取消本次新窗口请求，把 URL 交给外部浏览器。</summary>
    OpenInBrowser,
}

/// <summary>
/// 外部链接判定：<b>只做"这个 URL 该不该交给外部浏览器"这一件事</b>，不碰 WebView2。
/// <para>
/// 为什么放在 Domain 层：App 层没有测试工程（<c>tests/</c> 只有 Domain / Application / Infrastructure），
/// 判定写在 <c>MainWindow</c> 里就永远测不到。这里保持纯函数语义，可被单测完整覆盖。
/// </para>
/// <para>
/// <b>默认正则 = "域名主机名"</b>，由三段组成，不要随手改成朴素的 <c>^https?://localhost</c>：
/// <list type="number">
/// <item><c>(?!\[)</c> 排除 IPv6 字面量（<c>[::1]</c> / <c>[fe80::1]</c>）；</item>
/// <item><c>(?!localhost(?::\d+)?(?:[/?#]|$))</c> 排除 <c>localhost</c>，用 <c>[/?#]|$</c> 收尾，
/// 因此 <c>localhost.example.com</c> 不会被误伤 —— 朴素写法最容易踩的就是这个坑；</item>
/// <item><c>(?=[^/?#]*[A-Za-z])[^/?#]+</c> 要求主机段至少含一个字母，于是 <c>127.0.0.1</c> /
/// <c>10.0.0.1</c> / <c>192.168.1.5</c> / <c>0.0.0.0</c> 全部自然落选，
/// <b>不需要维护任何 IP 白名单</b>；同时 <c>http://</c> / <c>http:///path</c>（只有 scheme 没有主机名）也被挡掉。</item>
/// </list>
/// </para>
/// <para>
/// 已知代价（用户已确认接受）：带 userinfo 的 URL（<c>http://user@127.0.0.1:3080/</c>）主机段含字母，
/// 会命中正则被交给浏览器。若日后要堵，在正则最前面加 <c>(?!https?://[^/?#]*@)</c>。
/// </para>
/// <para>
/// 另一处已知行为：判定权完全在正则手里，不做运行期同源比对。默认值下 <c>defaultUrl</c>
/// （<c>http://127.0.0.1:3080/</c>）是 IP 字面量，同源链接天然留在应用内；但若把
/// <c>defaultUrl</c> 改成域名（如 <c>http://dsh.local/</c>），页面里的同源链接会命中正则被丢给浏览器，
/// 而系统浏览器没有 <c>WebView2Profile</c> 里的 token Cookie → 401。
/// </para>
/// </summary>
public sealed class ExternalLinkPolicy
{
    /// <summary>出厂默认正则：匹配"非 IP 字面量且非 localhost"的 http(s) 目标。</summary>
    public const string DefaultUrlRegex =
        @"^https?://(?!\[)(?!localhost(?::\d+)?(?:[/?#]|$))(?=[^/?#]*[A-Za-z])[^/?#]+";

    /// <summary>单次匹配的超时；命中超时按"不处理"收敛，绝不把异常抛到导航事件里。</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>为 null = 功能关闭（开关关、正则空白、或正则非法）。</summary>
    private readonly Regex? _pattern;

    /// <summary>正则非法时的原因；正常为 null。供 Shell 层写日志用，不弹窗。</summary>
    public string? PatternError { get; }

    /// <summary>
    /// 构造策略。<b>正则留空 = 功能禁用</b>（与 <see cref="UrlExtractor"/> 对
    /// <c>successMarkerRegex</c> 的"空串 = 这一级禁用"约定一致）：绝不能把空串交给
    /// <c>new Regex("")</c> —— 空模式匹配任意字符串，会把同源链接也一起丢给浏览器。
    /// </summary>
    public ExternalLinkPolicy(bool enabled, string? urlRegex)
    {
        if (!enabled || string.IsNullOrWhiteSpace(urlRegex))
            return;

        try
        {
            _pattern = new Regex(
                urlRegex.Trim(), RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout);
        }
        catch (ArgumentException ex)
        {
            // 手改 config.json 可能留下非法正则。降级为禁用 + 记录原因，不抛异常
            // （抛出会一路冒到 Loaded 的 async void，表现为"运行 exe 什么也不显示"）。
            PatternError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>功能是否真的生效（开关开 + 正则非空且合法）。</summary>
    public bool IsEnabled => _pattern is not null;

    /// <summary>
    /// 判定一个链接请求该不该交给外部浏览器。
    /// <para>
    /// 只接管 <c>http</c> / <c>https</c>：<c>about:</c>（打印预览 / 预开窗）、<c>blob:</c>（下载）、
    /// <c>data:</c>、<c>mailto:</c> 一律保持 WebView2 原生行为 —— 对它们一刀切
    /// <c>Handled = true</c> 会把打印和下载打断。
    /// </para>
    /// </summary>
    public ExternalLinkDecision Decide(string? targetUrl)
    {
        if (_pattern is null || string.IsNullOrWhiteSpace(targetUrl))
            return ExternalLinkDecision.KeepDefault;

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
            return ExternalLinkDecision.KeepDefault;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return ExternalLinkDecision.KeepDefault;

        try
        {
            return _pattern.IsMatch(targetUrl)
                ? ExternalLinkDecision.OpenInBrowser
                : ExternalLinkDecision.KeepDefault;
        }
        catch (RegexMatchTimeoutException)
        {
            // 灾难性回溯：这一次不接管，也不让异常穿过导航事件回调。
            return ExternalLinkDecision.KeepDefault;
        }
    }
}
