using System.Text.RegularExpressions;

namespace DshDesktop.Application;

/// <summary>
/// run.log 落盘前的脱敏工具。
/// 设计文档 §5.4 要求临时 URL 只在内存中存活、不落盘，因此日志中一律只保留
/// origin（scheme + host + port），路径 / query / fragment 全部折叠为「/...」。
/// </summary>
public static class LogRedaction
{
    private const string Http = "http";
    private const string Https = "https";

    private static readonly Regex UrlPattern = new(@"https?://\S+", RegexOptions.Compiled);

    public const string RedactedSuffix = "/...";

    public static string Url(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "(空)";

        // Uri.TryCreate 会把任意字符串按 file 方案解析成功，必须显式限定协议。
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "(非法 URL)";

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != Http && scheme != Https)
            return "(非法 URL)";

        var origin = $"{scheme}://{uri.Host}";
        if (!uri.IsDefaultPort)
            origin += $":{uri.Port}";

        var hasTail = uri.AbsolutePath.Length > 1
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment);

        return hasTail ? origin + RedactedSuffix : origin + "/";
    }

    /// <summary>对任意文本中出现的 URL 逐处脱敏，用于命令行输出与异常消息。</summary>
    public static string Line(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return UrlPattern.Replace(text, match => Url(match.Value));
    }

    /// <summary>
    /// URL 的"形状指纹"：只暴露<b>结构</b>——路径段数、每段长度、query 的键名、是否含控制字符 ——
    /// 不含任何 token 内容，因此与 §5.4「临时 URL 不落盘」不冲突。
    /// <para>
    /// 为什么需要它：<see cref="Url"/> 只留 origin，于是日志里
    /// "导航到裸 origin" 与 "导航到带 token 的路径" 长得几乎一样，排查 404 时只能靠猜
    /// （2026-09-13 实际踩过：一次 404 耗掉了整轮排查）。含控制字符那一项专门用来
    /// 暴露 ANSI 转义粘进 URL 的情况 —— 那正是 404 的元凶之一。
    /// </para>
    /// </summary>
    public static string UrlFingerprint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "(空)";

        var controlFlag = url.Count(char.IsControl) > 0;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return $"(非法 URL) 含控制字符={controlFlag}";

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != Http && scheme != Https)
            return $"(非法 URL) 含控制字符={controlFlag}";

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var segmentLengths = string.Join(",", segments.Select(s => s.Length));
        var queryKeys = string.Join(",", uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=')[0]));

        return $"路径段数={segments.Length} 段长=[{segmentLengths}] query键=[{queryKeys}] "
             + $"fragment={uri.Fragment.Length > 0} 含控制字符={controlFlag}";
    }
}
