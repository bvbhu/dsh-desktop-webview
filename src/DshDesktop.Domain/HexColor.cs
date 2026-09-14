namespace DshDesktop.Domain;

/// <summary>
/// 配置里颜色字符串的解析。只接受十六进制，格式与 WPF 的 Color 语法一致：
/// <c>#RGB</c> / <c>#ARGB</c> / <c>#RRGGBB</c> / <c>#AARRGGBB</c>（前后空白自动忽略）。
/// <para>
/// 为什么放在 Domain 而不是 UI 层：设置窗口的"校验"和界面的"套用"**必须共用同一个解析器**。
/// 分成两份时（UI 那份不 Trim）会让 <c>" #123456 "</c> 这类值在 WPF 侧抛异常后被静默吞掉，
/// 界面回落主题色，用户只看到"设置疑似不生效"。
/// </para>
/// <para>
/// 注意 8 位是 <b>AARRGGBB</b>（alpha 在前），不是 HTML/CSS 的 RRGGBBAA。
/// </para>
/// </summary>
public static class HexColor
{
    /// <summary>解析为 ARGB 四个通道。空串、非 <c>#</c> 开头、非十六进制、长度不对都返回 <c>false</c>。</summary>
    public static bool TryParse(string? text, out byte a, out byte r, out byte g, out byte b)
    {
        a = 255;
        r = 0;
        g = 0;
        b = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim();
        if (s.Length < 4 || s[0] != '#')
            return false;

        for (var i = 1; i < s.Length; i++)
        {
            if (!Uri.IsHexDigit(s[i]))
                return false;
        }

        switch (s.Length - 1)
        {
            case 3: // #RGB
                r = Duplicate(s[1]);
                g = Duplicate(s[2]);
                b = Duplicate(s[3]);
                return true;
            case 4: // #ARGB
                a = Duplicate(s[1]);
                r = Duplicate(s[2]);
                g = Duplicate(s[3]);
                b = Duplicate(s[4]);
                return true;
            case 6: // #RRGGBB
                r = Pair(s[1], s[2]);
                g = Pair(s[3], s[4]);
                b = Pair(s[5], s[6]);
                return true;
            case 8: // #AARRGGBB
                a = Pair(s[1], s[2]);
                r = Pair(s[3], s[4]);
                g = Pair(s[5], s[6]);
                b = Pair(s[7], s[8]);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// <see cref="TryParse"/> 的逆运算。alpha 为 255 时只写 <c>#RRGGBB</c>（配置文件里短一点、也好读）。
    /// </summary>
    public static string ToHex(byte a, byte r, byte g, byte b) =>
        a == 255
            ? $"#{r:X2}{g:X2}{b:X2}"
            : $"#{a:X2}{r:X2}{g:X2}{b:X2}";

    /// <summary>
    /// 在纯黑与纯白之间，挑出与该底色对比度更高的一个：返回 <c>true</c> 表示该用黑前景。
    /// <para>
    /// 用 WCAG 的相对亮度 + 对比度公式，而不是 <c>(R+G+B)/3</c> 这类等权平均：
    /// 人眼对绿最敏感、对蓝最不敏感（权重 0.7152 / 0.0722），等权平均会在纯蓝底上给出错误答案
    /// —— 而"三键图标颜色由底色决定"正是最容易踩到纯色的地方。
    /// </para>
    /// <para>
    /// 两个方向的对比度相等点解出来是 L ≈ 0.179，所以判据就是"亮度低于 0.179 用白、否则用黑"。
    /// </para>
    /// </summary>
    public static bool PreferBlackForeground(byte r, byte g, byte b)
    {
        var luminance = 0.2126 * Linearize(r) + 0.7152 * Linearize(g) + 0.0722 * Linearize(b);

        var contrastWithWhite = 1.05 / (luminance + 0.05);
        var contrastWithBlack = (luminance + 0.05) / 0.05;

        return contrastWithBlack >= contrastWithWhite;
    }

    /// <summary>sRGB 分量的反伽马。不做这一步直接拿 0-255 归一化，暗部会算得偏亮。</summary>
    private static double Linearize(byte value)
    {
        var s = value / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    /// <summary><c>#RGB</c> 这类缩写形式：每个半字节按 0x11 展开。</summary>
    private static byte Duplicate(char c) => (byte)(Digit(c) * 17);

    private static byte Pair(char high, char low) => (byte)((Digit(high) << 4) | Digit(low));

    private static int Digit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0, // TryParse 已保证只走到十六进制字符
    };
}
