using DshDesktop.Domain;
using FluentAssertions;

namespace DshDesktop.Domain.Tests;

/// <summary>
/// 浏览器启动计划：重点是<b>回落规则</b>（用户已确认：路径非法时不打断，回落到系统默认
/// 并在控制台留一行）。存在性判断用注入委托，所以这里不需要真实文件。
/// </summary>
public class BrowserLaunchPlanTests
{
    private static Func<string, bool> Exists(params string[] paths) =>
        path => paths.Contains(path, StringComparer.OrdinalIgnoreCase);

    // ---- 空路径 = 系统默认浏览器 ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Resolve_BlankPath_UsesSystemDefault(string? configured)
    {
        var plan = BrowserLaunchPlan.Resolve(configured, Exists());

        plan.Kind.Should().Be(BrowserLaunchKind.SystemDefault);
        plan.ExecutablePath.Should().BeNull();
        plan.FallbackReason.Should().BeNull(); // 用户本来就要系统默认，不算"回落"
    }

    // ---- 存在的路径 = 用指定 exe ----

    [Fact]
    public void Resolve_ExistingPath_UsesThatExecutable()
    {
        var path = @"C:\Program Files\Firefox\firefox.exe";
        var plan = BrowserLaunchPlan.Resolve(path, Exists(path));

        plan.Kind.Should().Be(BrowserLaunchKind.Executable);
        plan.ExecutablePath.Should().Be(path);
        plan.FallbackReason.Should().BeNull();
    }

    [Fact]
    public void Resolve_TrimsSurroundingWhitespace()
    {
        // 从资源管理器复制路径常带首尾空白
        var path = @"C:\browsers\chrome.exe";
        var plan = BrowserLaunchPlan.Resolve($"  {path}  ", Exists(path));

        plan.Kind.Should().Be(BrowserLaunchKind.Executable);
        plan.ExecutablePath.Should().Be(path);
    }

    // ---- 路径不存在 = 回落系统默认 + 带原因 ----

    [Fact]
    public void Resolve_MissingPath_FallsBackToSystemDefaultWithReason()
    {
        var plan = BrowserLaunchPlan.Resolve(@"C:\gone\browser.exe", Exists());

        plan.Kind.Should().Be(BrowserLaunchKind.SystemDefault);
        plan.ExecutablePath.Should().BeNull();
        plan.FallbackReason.Should().NotBeNullOrWhiteSpace();
        plan.FallbackReason.Should().Contain(@"C:\gone\browser.exe");
    }

    /// <summary>存在性判断必须拿到"展开后"的路径：用户会写 %LOCALAPPDATA% 这类环境变量。</summary>
    [Fact]
    public void Resolve_ExpandsEnvironmentVariablesBeforeExistenceCheck()
    {
        string? seen = null;
        var plan = BrowserLaunchPlan.Resolve(
            @"%LOCALAPPDATA%\Browser\browser.exe",
            path =>
            {
                seen = path;
                return true;
            });

        seen.Should().NotBeNull();
        seen.Should().NotContain("%LOCALAPPDATA%");
        plan.Kind.Should().Be(BrowserLaunchKind.Executable);
        plan.ExecutablePath.Should().Be(seen);
    }

    [Fact]
    public void Resolve_UndefinedEnvironmentVariable_KeepsLiteralAndFallsBack()
    {
        // 未定义变量展开后原样保留 → 文件当然不存在 → 回落
        var plan = BrowserLaunchPlan.Resolve(@"%DSH_NO_SUCH_VAR_12345%\b.exe", Exists());

        plan.Kind.Should().Be(BrowserLaunchKind.SystemDefault);
        plan.FallbackReason.Should().NotBeNullOrWhiteSpace();
    }
}
