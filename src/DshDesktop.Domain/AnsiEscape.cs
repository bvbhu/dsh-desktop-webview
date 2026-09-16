using System.Text.RegularExpressions;

namespace DshDesktop.Domain;

/// <summary>
/// 剥掉子进程输出里的终端控制序列，须在正则匹配 URL 之前调用：工具会给关键行上色，
/// 复位序列会被 URL 正则的 <c>\S+</c> 一起吞进捕获组，外壳便拿带转义字节的伪 URL 去导航并回 404（2026-09-13 实测）。
/// </summary>
public static class AnsiEscape
{
    // 按最长匹配优先：CSI（颜色/光标）、OSC（终端超链接）、两字节 ESC + 0x40-0x5F。
    private static readonly Regex ControlSequences = new(
        @"\x1B(?:\[[0-?]*[\x20-\x2F]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[\x40-\x5F])",
        RegexOptions.Compiled);

    /// <summary>去掉控制序列；<c>null</c>/空串原样返回空串。不做其他任何改写。</summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var stripped = ControlSequences.Replace(text, string.Empty);

        // 收尾：正则没覆盖到的孤立 ESC / BEL 也一并去掉，别让它们继续污染 URL。
        if (stripped.IndexOf('\u001B') >= 0 || stripped.IndexOf('\u0007') >= 0)
            stripped = stripped.Replace("\u001B", string.Empty).Replace("\u0007", string.Empty);

        return stripped;
    }
}
