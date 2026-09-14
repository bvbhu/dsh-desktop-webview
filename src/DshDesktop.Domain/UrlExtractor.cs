using System.Text.RegularExpressions;

namespace DshDesktop.Domain;

public sealed class UrlExtractor : IUrlExtractor
{
    private readonly Regex _urlRegex;

    /// <summary>
    /// 成功标志正则。**留空 = 这一级禁用**，用 null 表示。
    /// </summary>
    /// <remarks>
    /// 为什么不能直接把空串交给 <see cref="Regex"/>：空模式匹配**任意**字符串，
    /// <c>IsMatch</c> 恒为 true —— 于是启动命令吐出的第一行就会被当成"服务已就绪"。
    /// 默认值是空串，所以这个分支不是防御性代码，而是该默认值能成立的前提。
    /// </remarks>
    private readonly Regex? _successMarkerRegex;

    public UrlExtractor(string urlExtractRegex, string successMarkerRegex)
    {
        _urlRegex = new Regex(urlExtractRegex, RegexOptions.Compiled, TimeSpan.FromSeconds(5));

        _successMarkerRegex = string.IsNullOrWhiteSpace(successMarkerRegex)
            ? null
            : new Regex(successMarkerRegex, RegexOptions.Compiled, TimeSpan.FromSeconds(5));
    }

    public string? TryExtract(string line)
    {
        if (string.IsNullOrEmpty(line))
            return null;

        var match = _urlRegex.Match(line);
        if (!match.Success)
            return null;

        return match.Groups.TryGetValue("url", out var urlGroup) ? urlGroup.Value : null;
    }

    public bool IsSuccessMarker(string line)
    {
        if (string.IsNullOrEmpty(line) || _successMarkerRegex is null)
            return false;

        return _successMarkerRegex.IsMatch(line);
    }
}
