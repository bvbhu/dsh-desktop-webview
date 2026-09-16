namespace DshDesktop.Domain;

/// <summary>
/// 用户手输 URL 的规整。设置窗口里的示例本身就没有 scheme（形如 <c>127.0.0.1:3080/?token=...</c>），
/// 这种值直接交给 <c>WebView2.Navigate</c> 不会导航。
/// 因此进状态机之前统一补全：只补 scheme，绝不改写 host / 端口 / 路径 / query —— token 就在 query 里。
/// </summary>
public static class UrlNormalizer
{
    /// <summary>返回补全 scheme 后的 URL；输入为空/空白时返回 <c>null</c>。已有 http/https 的原样返回，其余补 <c>http://</c>。</summary>
    public static string? EnsureScheme(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmed = url.Trim();

        if (HasHttpScheme(trimmed))
            return trimmed;

        // 已是别的 scheme（如 file:）不加 http://，否则会改错用户原意；交由后续导航/正则拒绝。
        if (HasAnyScheme(trimmed))
            return trimmed;

        return "http://" + trimmed;
    }

    private static bool HasHttpScheme(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>形如 <c>scheme:</c> 且 scheme 合法（字母开头，后跟字母/数字/+/-/.）。难点是区分 scheme 与 host:port，故先单独认一遍 localhost。</summary>
    private static bool HasAnyScheme(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0)
            return false;

        var scheme = value[..colon];

        // localhost 是最常见的手输主机名，却碰巧长得跟 scheme 一样。
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
