using DshDesktop.Domain;
using FluentAssertions;

namespace DshDesktop.Domain.Tests;

public class HexColorTests
{
    [Theory]
    [InlineData("#FF0000", 255, 255, 0, 0)]
    [InlineData("#0000FF", 255, 0, 0, 255)]
    [InlineData("#1E1E1E", 255, 30, 30, 30)]
    [InlineData("#f00", 255, 255, 0, 0)]          // #RGB 缩写
    [InlineData("#8F00", 136, 255, 0, 0)]         // #ARGB：4 位时 alpha 在最前
    [InlineData("#80112233", 128, 17, 34, 51)]    // #AARRGGBB
    public void TryParse_ValidHex_ReturnsArgb(string input, byte a, byte r, byte g, byte b)
    {
        HexColor.TryParse(input, out var pa, out var pr, out var pg, out var pb).Should().BeTrue();
        (pa, pr, pg, pb).Should().Be((a, r, g, b));
    }

    [Theory]
    [InlineData("#ABCDEF")]
    [InlineData(" #ABCDEF ")]   // 带空白：曾经因此静默回落主题色（本次修复的起因）
    [InlineData("\t#ABCDEF\n")]
    public void TryParse_TrimsSurroundingWhitespace(string input)
    {
        HexColor.TryParse(input, out _, out var r, out var g, out var b).Should().BeTrue();
        (r, g, b).Should().Be((0xAB, 0xCD, 0xEF));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("FF0000")]   // 少了 #
    [InlineData("#12345")]   // 5 位
    [InlineData("#1234567")] // 7 位
    [InlineData("#GGGGGG")]  // 非十六进制
    [InlineData("red")]      // 具名色不在契约内（§5.3 写的就是十六进制）
    public void TryParse_Invalid_ReturnsFalse(string? input)
    {
        HexColor.TryParse(input, out _, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_Invalid_DoesNotLeakPreviousChannels()
    {
        HexColor.TryParse("#000000", out _, out _, out _, out _).Should().BeTrue();

        HexColor.TryParse("nonsense", out var a, out var r, out var g, out var b).Should().BeFalse();
        (a, r, g, b).Should().Be((255, 0, 0, 0));
    }

    // ---- 写回十六进制：取色器把颜色写进配置时走这条 ----

    [Theory]
    [InlineData(255, 0x12, 0x34, 0x56, "#123456")]     // 不透明时不写 alpha
    [InlineData(0x80, 0x12, 0x34, 0x56, "#80123456")]
    [InlineData(0x00, 0x00, 0x00, 0x00, "#00000000")]
    [InlineData(255, 255, 255, 255, "#FFFFFF")]
    public void ToHex_RoundTripsThroughTryParse(byte a, byte r, byte g, byte b, string expected)
    {
        HexColor.ToHex(a, r, g, b).Should().Be(expected);

        // 必须与 TryParse 互为逆运算：取色器写出去的值要能被校验器原样读回来，
        // 否则又会出现"保存时判非法、或颜色悄悄变了"这类问题。
        HexColor.TryParse(expected, out var pa, out var pr, out var pg, out var pb).Should().BeTrue();
        (pa, pr, pg, pb).Should().Be((a, r, g, b));
    }

    // ---- 图标前景色的自动取值（用户要求：图标颜色由底色判断，黑/白选对比度更高的） ----

    [Theory]
    [InlineData(0, 0, 0, false)]             // 纯黑底 → 白图标
    [InlineData(255, 255, 255, true)]        // 纯白底 → 黑图标
    [InlineData(0, 0, 255, false)]           // 纯蓝：亮度仅 0.0722，白图标对比度更高
    [InlineData(0, 255, 0, true)]            // 纯绿：亮度 0.7152，黑图标对比度更高
    [InlineData(0x80, 0x80, 0x80, true)]     // 中灰：亮度 0.216，已在 0.179 分界点之上 → 黑
    [InlineData(0x59, 0x59, 0x59, false)]    // 深灰：亮度 0.0999，在分界点之下 → 白
    public void PreferBlackForeground_PicksHigherContrast(byte r, byte g, byte b, bool expectBlack)
    {
        HexColor.PreferBlackForeground(r, g, b).Should().Be(expectBlack);
    }

    [Fact]
    public void PreferBlackForeground_WeighsGreenNotAverage()
    {
        // 纯绿的三通道均值 = 85（"深色"），等权平均会判成该用白图标；
        // 但人眼对绿最敏感，按 WCAG 权重它的亮度是 0.7152 —— 正确答案是黑图标。
        // 这条用例存在的意义就是把"等权平均"那种实现钉死在测试里。
        HexColor.PreferBlackForeground(0, 255, 0).Should().BeTrue();
        ((0 + 255 + 0) / 3).Should().BeLessThan(128);
    }
}
