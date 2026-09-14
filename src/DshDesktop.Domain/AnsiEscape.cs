using System.Text.RegularExpressions;

namespace DshDesktop.Domain;

/// <summary>
/// 剥掉子进程输出里的终端控制序列。
/// <para>
/// 为什么必须在做正则匹配之前剥：命令行工具（含 dsh）会给关键行上色，形如
/// <c>ESC[36mhttp://127.0.0.1:3080/tok/xxxESC[0m</c>。默认 URL 正则的 <c>\S+</c> 只按
/// 空白断词，而 ESC 不是空白 —— 结尾的复位序列会被一起吞进捕获组。外壳于是拿着一个
/// <b>带转义字节的伪 URL</b> 去导航：服务明明是活的，却回 404（2026-09-13 实测）。
/// </para>
/// <para>
/// 顺带的好处：配置窗口错误区（设计文档 §8 要求常驻显示 stdout/stderr）不再显示裸转义符。
/// </para>
/// </summary>
public static class AnsiEscape
{
    // 三类控制序列，按"最长匹配优先"排列：
    //   CSI: ESC [ 参数 中间字节 终止字节   —— 颜色、光标等，最常见
    //   OSC: ESC ] ... BEL 或 ESC \        —— 终端超链接（URL 会藏在里面）
    //   两字节: ESC + 0x40-0x5F            —— 其余转义
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
