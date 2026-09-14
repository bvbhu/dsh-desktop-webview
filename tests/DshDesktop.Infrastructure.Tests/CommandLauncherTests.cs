using System.Collections.Concurrent;
using DshDesktop.Domain;
using DshDesktop.Infrastructure;
using FluentAssertions;

namespace DshDesktop.Infrastructure.Tests;

public class CommandLauncherTests
{
    private static AppConfig MakeConfig(string launchCommand) => AppConfig.CreateDefault() with
    {
        LaunchCommand = launchCommand,
        UrlExtractRegex = @"dsh web:\s*(?<url>https?://\S+)",
        SuccessMarkerRegex = @"dsh web|listening|ready|启动成功",
    };

    private static IProgress<string> MakeProgress(ConcurrentBag<string> bag) =>
        new ProgressBag(bag);

    [Fact]
    public async Task Launch_CapturesUrl_FromStdout()
    {
        var cfg = MakeConfig("echo dsh web: https://127.0.0.1:9999/");
        using var launcher = new CommandLauncher();
        var bag = new ConcurrentBag<string>();

        var result = await launcher.LaunchAsync(cfg, MakeProgress(bag), CancellationToken.None);

        result.Url.Should().Be("https://127.0.0.1:9999/");
        result.HitSuccessMarker.Should().BeFalse();
    }

    [Fact]
    public async Task Launch_CapturesSuccessMarker_WhenNoUrl()
    {
        var cfg = MakeConfig("echo Server listening on port 3080");
        using var launcher = new CommandLauncher();
        var bag = new ConcurrentBag<string>();

        var result = await launcher.LaunchAsync(cfg, MakeProgress(bag), CancellationToken.None);

        result.Url.Should().BeNull();
        result.HitSuccessMarker.Should().BeTrue();
    }

    [Fact]
    public async Task Launch_BothMiss_WhenNoUrlNoMarker()
    {
        var cfg = MakeConfig("echo nothing relevant here at all");
        using var launcher = new CommandLauncher();
        var bag = new ConcurrentBag<string>();

        var result = await launcher.LaunchAsync(cfg, MakeProgress(bag), CancellationToken.None);

        result.Url.Should().BeNull();
        result.HitSuccessMarker.Should().BeFalse();
        result.CapturedOutput.Should().Contain("nothing relevant");
    }

    [Fact]
    public async Task Launch_CapturesUrl_FromStderr()
    {
        var cfg = MakeConfig("echo dsh web: https://192.168.1.1:443/ 1>&2");
        using var launcher = new CommandLauncher();
        var bag = new ConcurrentBag<string>();

        var result = await launcher.LaunchAsync(cfg, MakeProgress(bag), CancellationToken.None);

        result.Url.Should().NotBeNull();
        result.Url.Should().StartWith("https://192.168.1.1:443");
    }

    [Fact]
    public async Task Launch_CapturesOutput_ForErrorDisplay()
    {
        var cfg = MakeConfig("echo line1 & echo line2 & echo line3");
        using var launcher = new CommandLauncher();
        var bag = new ConcurrentBag<string>();

        var result = await launcher.LaunchAsync(cfg, MakeProgress(bag), CancellationToken.None);

        result.CapturedOutput.Should().Contain("line1");
        result.CapturedOutput.Should().Contain("line2");
        result.CapturedOutput.Should().Contain("line3");
        bag.Should().NotBeEmpty();
    }

    /// <summary>
    /// 退出码既要进捕获文本（给人看），也要作为结构化字段回传（给调用方判定）。
    /// 只进文本是不够的 —— "进程退出非零 → Failed"这条迁移此前不可达，正是因为
    /// 退出码只被拼成一行字符串，调用方拿不到可判断的值。
    /// </summary>
    [Fact]
    public async Task Launch_NonZeroExitCode_CapturedInOutput()
    {
        var cfg = MakeConfig("exit /b 42");
        using var launcher = new CommandLauncher();

        var result = await launcher.LaunchAsync(cfg, MakeProgress(new()), CancellationToken.None);

        result.CapturedOutput.Should().Contain("进程退出码");
        result.Url.Should().BeNull();
        result.ExitCode.Should().Be(42, "退出码必须结构化回传，不能只留在捕获文本里");
    }

    /// <summary>进程正常退出（码 0）时同样回传 0，不是 null —— 两者语义不同。</summary>
    [Fact]
    public async Task Launch_ZeroExitCode_ReportedAsZeroNotNull()
    {
        var cfg = MakeConfig("echo nothing relevant here at all");
        using var launcher = new CommandLauncher();

        var result = await launcher.LaunchAsync(cfg, MakeProgress(new()), CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.CapturedOutput.Should().NotContain("进程退出码");
    }

    /// <summary>
    /// 进程仍在运行（这里用一条不输出、不退出、撑到超时的命令模拟）时，
    /// ExitCode 必须是 null = "还活着"，绝不能写成 0 —— 那会把
    /// "服务启动慢"误报成"进程正常退出"，进而让调用方误判失败原因。
    /// </summary>
    [Fact]
    public async Task Launch_StillRunningOnTimeout_ExitCodeIsNull()
    {
        // ping 一条本地不存在的地址会等满超时且不产生可识别输出；用 -n 拉长到远超 30s
        var cfg = MakeConfig("ping -n 100 127.0.0.1 > nul");
        using var launcher = new CommandLauncher();

        var result = await launcher.LaunchAsync(cfg, MakeProgress(new()), CancellationToken.None);

        result.Url.Should().BeNull();
        result.ExitCode.Should().BeNull("进程未退出时不能用 0 冒充");
    }

    /// <summary>
    /// 子进程输出按 UTF-8 解码（回归：曾按控制台 OEM 代码页 GBK 解码，
    /// 中文变成「鑷姩鎭㈠鑰冭瘯」）。
    /// <para>
    /// 这里不能用 <c>echo</c> 直接吐中文：cmd.exe 自己的输出编码取决于控制台代码页，
    /// 会在到达我们的解码器之前就已经错掉，测的就不是被测对象了。
    /// 因此改由 PowerShell 显式写 UTF-8 字节到 stdout —— 与 dsh/node 的行为一致。
    /// 断言用 <c>Contain</c> 精确匹配中文串：GBK 误解码时这些字一个都不会出现。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Launch_DecodesUtf8Stdout_SoChineseIsReadable()
    {
        const string chinese = "自动重启引擎就绪";
        var cfg = MakeConfig(
            "powershell -NoProfile -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            $"Write-Output '{chinese}'\"");
        using var launcher = new CommandLauncher();

        var result = await launcher.LaunchAsync(cfg, MakeProgress(new()), CancellationToken.None);

        result.CapturedOutput.Should().Contain(chinese);
    }

    /// <summary>stderr 与 stdout 走同一条解码路径，必须一起钉住 —— 漏了这条就只修了一半。</summary>
    [Fact]
    public async Task Launch_DecodesUtf8Stderr_SoChineseIsReadable()
    {
        const string chinese = "启动失败原因";
        var cfg = MakeConfig(
            "powershell -NoProfile -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            $"[Console]::Error.WriteLine('{chinese}')\"");
        using var launcher = new CommandLauncher();

        var result = await launcher.LaunchAsync(cfg, MakeProgress(new()), CancellationToken.None);

        result.CapturedOutput.Should().Contain(chinese);
    }
}

file sealed class ProgressBag : IProgress<string>
{
    private readonly ConcurrentBag<string> _bag;
    public ProgressBag(ConcurrentBag<string> bag) => _bag = bag;
    public void Report(string value) => _bag.Add(value);
}
