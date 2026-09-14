namespace DshDesktop.Domain;

/// <summary>
/// 用户手输 URL 的规整。设置窗口的临时 URL 输入框里，用户经常会漏掉 scheme ——
/// 界面上给的示例本身就形如 <c>127.0.0.1:3080/?token=...</c>，粘进来的也常是这样。
/// <para>
/// 这种值直接交给 <c>WebView2.Navigate</c> 不会导航到页面（会被当成相对/非法地址），
/// 而 <see cref="LogRedaction.Url"/> 也会把它报成「(非法 URL)」——
/// 排查时只能看到一句"非法"，看不出用户到底想要哪个地址。
/// </para>
/// <para>
/// 因此统一在进入状态机之前补全：只补 scheme，**绝不改写 host / 端口 / 路径 / query**。
/// token 就在 query 里，任何"顺手清理"都可能把它弄丢（§5.4 要求它只在内存里存活，但必须活到导航那一刻）。
/// </para>
/// </summary>
public static class UrlNormalizer
{
    /// <summary>
    /// 返回补全 scheme 后的 URL；输入为空/空白时返回 <c>null</c>。
    /// <para>
    /// 已有 <c>http://</c> / <c>https://</c> 前缀的原样返回（仅 Trim）。
    /// 其余情况补 <c>http://</c> —— 临时 URL 指向的都是本机服务，http 是唯一合理默认。
    /// </para>
    /// </summary>
    public static string? EnsureScheme(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmed = url.Trim();

        if (HasHttpScheme(trimmed))
            return trimmed;

        // 已经是别的 scheme（如 file:）不加 http://，否则会把用户的原意改错；
        // 交由后续的导航/正则去拒绝。
        if (HasAnyScheme(trimmed))
            return trimmed;

        return "http://" + trimmed;
    }

    private static bool HasHttpScheme(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 形如 <c>scheme:</c> 且 scheme 是合法字符集（字母开头，后跟字母/数字/+/-/.）。
    /// <para>
    /// 必须区分「有 scheme」与「host:port」，而这两者光看字符集是分不开的：
    /// <c>localhost:3080</c> 里 <c>localhost</c> 全是字母，字符集检查会误判成 scheme。
    /// 所以先按"主机名"识别一遍。
    /// </para>
    /// <para>
    /// <c>127.0.0.1:3080</c> 那类靠字符集就够了（以数字开头，不是合法 scheme）；
    /// 这里的白名单专治 <c>localhost</c> 这种纯字母主机名。
    /// </para>
    /// </summary>
    private static bool HasAnyScheme(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0)
            return false;

        var scheme = value[..colon];

        // localhost 是最常见的手输主机名，但它碰巧长得跟 scheme 一样。
        if (scheme.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!char.IsAsciiLetter(scheme[0]))
            return false;

        foreach (var c in scheme)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
                return false;
        }

        return true;
    }
}
