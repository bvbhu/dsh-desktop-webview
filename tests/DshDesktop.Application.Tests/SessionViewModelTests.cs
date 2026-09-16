using DshDesktop.Application;
using DshDesktop.Domain;
using FluentAssertions;

namespace DshDesktop.Application.Tests;

public sealed class FakeProbe : IEndpointProbe
{
    private readonly Queue<ProbeResult> _queue = new();

    public ProbeResult Result
    {
        set { _queue.Clear(); _queue.Enqueue(value); }
    }

    public int CallCount;
    public List<string> ProbedUrls { get; } = new();

    public void Enqueue(ProbeResult result) => _queue.Enqueue(result);

    public Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
    {
        CallCount++;
        ProbedUrls.Add(url);
        var result = _queue.Count > 0
            ? _queue.Dequeue()
            : new ProbeResult(ProbeOutcome.Ok, 200, null);
        return Task.FromResult(result);
    }
}

public sealed class FakeLauncher : IServerLauncher, IDisposable
{
    public LaunchResult Result { get; set; } = new(null, false, string.Empty);
    public List<string> OutputLines { get; } = new();
    public bool Disposed { get; private set; }

    public Task<LaunchResult> LaunchAsync(AppConfig cfg, IProgress<string> output, CancellationToken ct)
    {
        foreach (var line in OutputLines)
            output.Report(line);
        return Task.FromResult(Result);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// 可控启动器：LaunchAsync 一直挂着，直到测试显式 Complete。
/// 用于把会话钉在 Launching 阶段，验证「启动管线进行中不允许重置」。
/// </summary>
public sealed class GatedLauncher : IServerLauncher
{
    private readonly TaskCompletionSource<LaunchResult> _gate = new();

    public void Complete(LaunchResult result) => _gate.TrySetResult(result);

    public Task<LaunchResult> LaunchAsync(AppConfig cfg, IProgress<string> output, CancellationToken ct) =>
        _gate.Task;
}

public class SessionViewModelTests
{
    private static AppConfig S2Config => AppConfig.CreateDefault() with
    {
        ServiceStrategy = ServiceStrategy.ProbeThenStart,
        DefaultUrl = "http://127.0.0.1:3080/",
    };

    private static AppConfig S1Config => AppConfig.CreateDefault() with
    {
        ServiceStrategy = ServiceStrategy.NeverStart,
    };

    private static AppConfig S3Config => AppConfig.CreateDefault() with
    {
        ServiceStrategy = ServiceStrategy.AlwaysStart,
    };

    [Fact]
    public async Task S2_ProbeOk_NavigatesDirectly()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Ok, 200, null) };
        var launcher = new FakeLauncher();
        using var vm = new SessionViewModel(probe, launcher);
        var navUrls = new List<string>();
        var configOpened = 0;
        vm.NavigateRequested += url => navUrls.Add(url);
        vm.OpenConfigRequested += () => configOpened++;

        await vm.StartSessionAsync(S2Config, CancellationToken.None);

        vm.Phase.Should().Be(SessionPhase.Ready);
        navUrls.Should().ContainSingle().Which.Should().Be("http://127.0.0.1:3080/");
        configOpened.Should().Be(0);
        probe.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task S2_ProbeUnreachable_LaunchesAndExtractsUrl()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Unreachable, null, "refused") };
        var launcher = new FakeLauncher
        {
            Result = new LaunchResult("https://127.0.0.1:3080/tok/abc", false, "output"),
        };
        using var vm = new SessionViewModel(probe, launcher);
        var navUrls = new List<string>();
        vm.NavigateRequested += url => navUrls.Add(url);

        await vm.StartSessionAsync(S2Config, CancellationToken.None);

        vm.Phase.Should().Be(SessionPhase.Ready);
        navUrls.Should().ContainSingle().Which.Should().Be("https://127.0.0.1:3080/tok/abc");
    }

    [Fact]
    public async Task S2_ProbeUnreachable_LaunchSuccessMarker_FallbackProbeOk()
    {
        var probe = new FakeProbe();
        probe.Enqueue(new ProbeResult(ProbeOutcome.Unreachable, null, null));
        probe.Enqueue(new ProbeResult(ProbeOutcome.Ok, 200, null));
        var launcher = new FakeLauncher
        {
            Result = new LaunchResult(null, true, "listening"),
            OutputLines = { "listening on 3080" },
        };
        using var vm = new SessionViewModel(probe, launcher);
        var navUrls = new List<string>();
        var configOpened = 0;
        vm.NavigateRequested += url => navUrls.Add(url);
        vm.OpenConfigRequested += () => configOpened++;

        await vm.StartSessionAsync(S2Config, CancellationToken.None);

        probe.CallCount.Should().Be(2);
        probe.ProbedUrls[1].Should().Be("http://127.0.0.1:3080/");
        vm.Phase.Should().Be(SessionPhase.Ready);
        navUrls.Should().ContainSingle().Which.Should().Be("http://127.0.0.1:3080/");
        configOpened.Should().Be(0);
        vm.ConsoleOutput.Should().Contain("listening on 3080");
    }

    [Fact]
    public async Task S2_ProbeUnreachable_LaunchSuccessMarker_FallbackProbeOther_Fails()
    {
        var probe = new FakeProbe();
        probe.Enqueue(new ProbeResult(ProbeOutcome.Unreachable, null, null));
        probe.Enqueue(new ProbeResult(ProbeOutcome.Other, 500, "err"));
        var launcher = new FakeLauncher
        {
            Result = new LaunchResult(null, true, "ready"),
        };
        using var vm = new SessionViewModel(probe, launcher);
        var navUrls = new List<string>();
        var configOpened = 0;
        vm.NavigateRequested += url => navUrls.Add(url);
        vm.OpenConfigRequested += () => configOpened++;

        await vm.StartSessionAsync(S2Config, CancellationToken.None);

        probe.CallCount.Should().Be(2);
        vm.Phase.Should().Be(SessionPhase.Failed);
        configOpened.Should().Be(1);
        navUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task S2_ProbeOther_OpensConfig_NeedsToken()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Other, 401, "unauthorized") };
        var launcher = new FakeLauncher();
        using var vm = new SessionViewModel(probe, launcher);
        var configOpened = 0;
        vm.OpenConfigRequested += () => configOpened++;

        await vm.StartSessionAsync(S2Config, CancellationToken.None);

        vm.Phase.Should().Be(SessionPhase.NeedsToken);
        configOpened.Should().Be(1);
        vm.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task S2_NeedsToken_ProvideTempUrl_NavigatesAndReady()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Other, 403, null) };
        var launcher = new FakeLauncher();
        using var vm = new SessionViewModel(probe, launcher);
        var navUrls = new List<string>();
        vm.NavigateRequested += url => navUrls.Add(url);

        await vm.StartSessionAsync(S2Config, CancellationToken.None);
        vm.Phase.Should().Be(SessionPhase.NeedsToken);

        vm.ProvideTempUrl("http://127.0.0.1:3080/temp-token-xyz");

        vm.Phase.Should().Be(SessionPhase.Ready);
        navUrls.Should().Contain("http://127.0.0.1:3080/temp-token-xyz");
    }

    [Fact]
    public async Task S2_ProbeUnreachable_LaunchNeither_Timeout_Fails()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Unreachable, null, null) };
        var launcher = new FakeLauncher
        {
            // 进程未退出（ExitCode = null）= 真超时：命令还在跑，只是没吐 URL
            Result = new LaunchResult(null, false, "no output", null),
        };
        using var vm = new SessionViewModel(probe, launcher);
        var configOpened = 0;
        vm.OpenConfigRequested += () => configOpened++;

        await vm.StartSessionAsync(S2Config, CancellationToken.None);

        vm.Phase.Should().Be(SessionPhase.Failed);
        configOpened.Should().Be(1);
        vm.ErrorMessage.Should().Contain("启动超时");
    }

    /// <summary>
    /// 启动命令非零退出（命令名打错、参数不认）走的是独立迁移（§4.1「进程退出非零 → Failed」），
    /// 而不是超时分支 —— 这条路径此前不可达，因为 LaunchResult 没有回传退出码。
    /// </summary>
    [Fact]
    public async Task S2_ProbeUnreachable_LaunchNonZeroExit_ReportsExitCodeNotTimeout()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Unreachable, null, null) };
        var launcher = new FakeLauncher
        {
            Result = new LaunchResult(null, false, "命令找不到", 1),
        };
        using var vm = new SessionViewModel(probe, launcher);
        var configOpened = 0;
        vm.OpenConfigRequested += () => configOpened++;

        await vm.StartSessionAsync(S2Config, CancellationToken.None);

        vm.Phase.Should().Be(SessionPhase.Failed);
        configOpened.Should().Be(1);
        // 关键区分：文案指向"命令异常退出"并带上退出码，不再是"超时"
        vm.ErrorMessage.Should().Contain("退出码 1");
        vm.ErrorMessage.Should().NotContain("超时");
    }

    /// <summary>
    /// 退出码 0 = 进程正常退出但没输出可识别 URL。这不属于"命令异常退出"，
    /// 必须落回超时分支 —— 否则会把"服务自己退了"误报成"命令写错"。
    /// </summary>
    [Fact]
    public async Task S2_ProbeUnreachable_LaunchZeroExit_FallsBackToTimeout()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Unreachable, null, null) };
        var launcher = new FakeLauncher
        {
            Result = new LaunchResult(null, false, "服务自己退了", 0),
        };
        using var vm = new SessionViewModel(probe, launcher);
        vm.OpenConfigRequested += () => { };

        await vm.StartSessionAsync(S2Config, CancellationToken.None);

        vm.Phase.Should().Be(SessionPhase.Failed);
        vm.ErrorMessage.Should().Contain("启动超时");
    }

    /// <summary>
    /// 退出码非零，但第 1 级已经抓到 URL —— URL 优先，仍然要成功导航。
    /// 有些服务会先打印 URL 再以非零码退出（自身 detach 的场景），不能因此判失败。
    /// </summary>
    [Fact]
    public async Task S2_LaunchNonZeroExit_ButUrlExtracted_StillNavigates()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Unreachable, null, null) };
        var launcher = new FakeLauncher
        {
            Result = new LaunchResult("https://127.0.0.1:3080/tok/ok", false, "out", 3),
        };
        using var vm = new SessionViewModel(probe, launcher);
        var navUrls = new List<string>();
        vm.NavigateRequested += url => navUrls.Add(url);

        await vm.StartSessionAsync(S2Config, CancellationToken.None);

        vm.Phase.Should().Be(SessionPhase.Ready);
        navUrls.Should().ContainSingle().Which.Should().Be("https://127.0.0.1:3080/tok/ok");
    }

    /// <summary>成功标志命中时优先走回退探测：退出码非零不该抢在它前面判失败。</summary>
    [Fact]
    public async Task S2_LaunchSuccessMarker_WithNonZeroExit_PrefersFallbackProbe()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Unreachable, null, null) };
        probe.Enqueue(new ProbeResult(ProbeOutcome.Ok, 200, null));
        var launcher = new FakeLauncher
        {
            Result = new LaunchResult(null, true, "ready", 2),
        };
        using var vm = new SessionViewModel(probe, launcher);
        var navUrls = new List<string>();
        vm.NavigateRequested += url => navUrls.Add(url);

        await vm.StartSessionAsync(S2Config, CancellationToken.None);

        vm.Phase.Should().Be(SessionPhase.Ready);
        navUrls.Should().ContainSingle();
    }

    [Fact]
    public async Task S1_NavigateDirectly_NoProbeNoLaunch()
    {
        var probe = new FakeProbe();
        var launcher = new FakeLauncher();
        using var vm = new SessionViewModel(probe, launcher);
        var navUrls = new List<string>();
        vm.NavigateRequested += url => navUrls.Add(url);

        await vm.StartSessionAsync(S1Config, CancellationToken.None);

        vm.Phase.Should().Be(SessionPhase.Ready);
        navUrls.Should().ContainSingle();
        probe.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task S3_LaunchDirectly_ExtractsUrl()
    {
        var probe = new FakeProbe();
        var launcher = new FakeLauncher
        {
            Result = new LaunchResult("https://localhost:9999/", false, ""),
        };
        using var vm = new SessionViewModel(probe, launcher);
        var navUrls = new List<string>();
        vm.NavigateRequested += url => navUrls.Add(url);

        await vm.StartSessionAsync(S3Config, CancellationToken.None);

        vm.Phase.Should().Be(SessionPhase.Ready);
        navUrls.Should().ContainSingle().Which.Should().Be("https://localhost:9999/");
        probe.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task S3_LaunchSuccessMarker_FallbackProbeOk()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Ok, 200, null) };
        var launcher = new FakeLauncher
        {
            Result = new LaunchResult(null, true, "ready"),
        };
        using var vm = new SessionViewModel(probe, launcher);
        var navUrls = new List<string>();
        vm.NavigateRequested += url => navUrls.Add(url);

        await vm.StartSessionAsync(S3Config, CancellationToken.None);

        vm.Phase.Should().Be(SessionPhase.Ready);
        navUrls.Should().ContainSingle().Which.Should().Be("http://127.0.0.1:3080/");
    }

    [Fact]
    public void ShellViewModel_RestoreAndApply_RoundTrips()
    {
        var shell = new ShellViewModel();
        var cfg = AppConfig.CreateDefault() with
        {
            WindowX = 100,
            WindowY = 200,
            WindowWidth = 800,
            WindowHeight = 600,
            WindowMaximized = true,
        };

        shell.RestoreFrom(cfg);
        shell.WindowX.Should().Be(100);
        shell.WindowMaximized.Should().BeTrue();

        var applied = shell.ApplyTo(AppConfig.CreateDefault());
        applied.WindowX.Should().Be(100);
        applied.WindowMaximized.Should().BeTrue();
    }

    [Fact]
    public void SettingsViewModel_StripToken_RemovesPath()
    {
        SettingsViewModel.StripToken("http://127.0.0.1:3080/token/abc123").Should().Be("http://127.0.0.1:3080/");
        SettingsViewModel.StripToken("https://localhost:443/").Should().Be("https://localhost:443/");
        SettingsViewModel.StripToken("http://127.0.0.1:3080").Should().Be("http://127.0.0.1:3080/");
    }

    [Fact]
    public void SettingsViewModel_Validate_BadRegex_Fails()
    {
        var vm = new SettingsViewModel(AppConfig.CreateDefault());
        vm.UrlExtractRegex = "(invalid[group";
        vm.Validate().Should().BeFalse();
        vm.ValidationError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void SettingsViewModel_BuildConfig_StripsToken()
    {
        var vm = new SettingsViewModel(AppConfig.CreateDefault());
        vm.DefaultUrl = "http://127.0.0.1:3080/tok/secret";
        var cfg = vm.BuildConfig();
        cfg.DefaultUrl.Should().Be("http://127.0.0.1:3080/");
    }

    // ---- 颜色校验：空 = 跟随主题；非空必须是十六进制，否则界面会静默回落 ----

    [Theory]
    [InlineData("#FF0000")]
    [InlineData("#0000FF")]
    [InlineData(" #123456 ")]   // 带空白：曾导致 WPF 解析抛异常后静默回落主题色
    [InlineData("#8F00")]       // #ARGB
    [InlineData("#80112233")]   // #AARRGGBB
    public void SettingsViewModel_Validate_AcceptsHexColors(string color)
    {
        var vm = new SettingsViewModel(AppConfig.CreateDefault())
        {
            ChromeButtonIconColor = color,
            ChromeButtonBackground = color,
            DragStripColor = color,
        };

        vm.Validate().Should().BeTrue();
        vm.ValidationError.Should().BeNull();
    }

    [Theory]
    [InlineData("red")]        // 具名色不在契约内（§5.3 写的是十六进制）
    [InlineData("#GGGGGG")]
    [InlineData("#12345")]
    [InlineData("123456")]
    public void SettingsViewModel_Validate_BadColor_Fails(string color)
    {
        var vm = new SettingsViewModel(AppConfig.CreateDefault());
        vm.ChromeButtonBackground = color;

        vm.Validate().Should().BeFalse();
        vm.ValidationError.Should().Contain("三键背景颜色");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SettingsViewModel_Validate_BlankColor_MeansFollowTheme(string color)
    {
        var vm = new SettingsViewModel(AppConfig.CreateDefault());
        vm.ChromeButtonIconColor = color;

        vm.Validate().Should().BeTrue();
    }

    [Fact]
    public void SettingsViewModel_BuildConfig_TrimsColors()
    {
        var vm = new SettingsViewModel(AppConfig.CreateDefault());
        vm.ChromeButtonBackground = " #123456 ";

        vm.BuildConfig().ChromeButtonBackground.Should().Be("#123456");
    }

    // ---- 「跟随主题」复选框：它是 ChromeButtonBackground 的派生视图（空 = 跟随） ----

    [Fact]
    public void FollowTheme_IsTrue_WhenBackgroundBlank()
    {
        // 出厂默认就是空，所以复选框默认勾选
        var vm = new SettingsViewModel(AppConfig.CreateDefault());

        vm.ChromeButtonBackgroundFollowTheme.Should().BeTrue();
        vm.ChromeButtonBackground.Should().BeEmpty();
    }

    [Fact]
    public void FollowTheme_Unchecking_GivesConcreteColor_SoCheckboxStaysUnchecked()
    {
        var vm = new SettingsViewModel(AppConfig.CreateDefault());

        vm.ChromeButtonBackgroundFollowTheme = false;

        // 关键：不能写成空串 —— 那样复选框会立刻弹回勾选，用户会觉得点不动
        vm.ChromeButtonBackground.Should().NotBeNullOrWhiteSpace();
        vm.ChromeButtonBackgroundFollowTheme.Should().BeFalse();
        vm.Validate().Should().BeTrue();
    }

    [Fact]
    public void FollowTheme_CheckingBack_ClearsBackground()
    {
        var vm = new SettingsViewModel(AppConfig.CreateDefault()) { ChromeButtonBackground = "#123456" };

        vm.ChromeButtonBackgroundFollowTheme = true;

        vm.ChromeButtonBackground.Should().BeEmpty();
        vm.BuildConfig().ChromeButtonBackground.Should().BeEmpty();
    }

    [Fact]
    public void FollowTheme_SettingBackgroundDirectly_MovesTheCheckbox()
    {
        // 派生方向是双向的：字段被别处（如重置外观）改动时复选框要跟着走
        var vm = new SettingsViewModel(AppConfig.CreateDefault());
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.ChromeButtonBackground = "#123456";

        vm.ChromeButtonBackgroundFollowTheme.Should().BeFalse();
        raised.Should().Contain(nameof(SettingsViewModel.ChromeButtonBackgroundFollowTheme));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FollowTheme_TreatsWhitespaceAsFollowing(string value)
    {
        var vm = new SettingsViewModel(AppConfig.CreateDefault()) { ChromeButtonBackground = value };

        vm.ChromeButtonBackgroundFollowTheme.Should().BeTrue();
    }

    // ---- 「重置外观」必须与 CreateDefault() 逐字段一致 ----

    [Fact]
    public void ResetAppearance_MatchesCreateDefault_ForAppearanceFields()
    {
        var d = AppConfig.CreateDefault();
        var vm = new SettingsViewModel(d)
        {
            // 先把所有外观字段改脏，再重置 —— 否则测试可能在"本来就是默认值"时空过
            ChromeButtonsDefault = ChromeButtonVisibility.Hidden,
            ChromeButtonIconColor = "#FF0000",
            ChromeButtonBackground = "#00FF00",
            ChromeHoverDelayMs = 123,
            DragStripEnabled = false,
            DragStripLeftInset = 1,
            DragStripRightInset = 2,
            DragStripHeight = 3,
            DragStripColor = "#ABCDEF",
            DragStripOpacity = 0.9,
        };

        vm.ResetAppearance();

        vm.ChromeButtonsDefault.Should().Be(d.ChromeButtonsDefault);
        vm.ChromeButtonIconColor.Should().Be(d.ChromeButtonIconColor);
        vm.ChromeButtonBackground.Should().Be(d.ChromeButtonBackground);
        vm.ChromeHoverDelayMs.Should().Be(d.ChromeHoverDelayMs);
        vm.DragStripEnabled.Should().Be(d.DragStripEnabled);
        vm.DragStripLeftInset.Should().Be(d.DragStripLeftInset);
        vm.DragStripRightInset.Should().Be(d.DragStripRightInset);
        vm.DragStripHeight.Should().Be(d.DragStripHeight);
        vm.DragStripColor.Should().Be(d.DragStripColor);
        vm.DragStripOpacity.Should().Be(d.DragStripOpacity);
    }

    [Fact]
    public void ResetAppearance_RechecksFollowTheme()
    {
        var vm = new SettingsViewModel(AppConfig.CreateDefault()) { ChromeButtonBackground = "#123456" };

        vm.ResetAppearance();

        vm.ChromeButtonBackgroundFollowTheme.Should().BeTrue();
    }

    // ---- 「不启动」实时隐藏启动配置：ServiceNeverStart 是 ServiceStrategy 的派生视图 ----

    [Fact]
    public void ServiceNeverStart_TracksStrategy_AndRaisesNotification()
    {
        var config = AppConfig.CreateDefault() with { ServiceStrategy = ServiceStrategy.ProbeThenStart };
        var vm = new SettingsViewModel(config);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ServiceNeverStart.Should().BeFalse();

        vm.ServiceStrategy = ServiceStrategy.NeverStart;

        // 两个通知都要有：字段本身 + 派生的显隐视图。缺后者，界面就不会实时藏起启动配置
        vm.ServiceNeverStart.Should().BeTrue();
        raised.Should().Contain(nameof(SettingsViewModel.ServiceStrategy));
        raised.Should().Contain(nameof(SettingsViewModel.ServiceNeverStart));

        vm.ServiceStrategy = ServiceStrategy.AlwaysStart;
        vm.ServiceNeverStart.Should().BeFalse();
    }

    // ---- 拖动层「颜色 + 透明度」合成（参考图要求：共用一个选择器，不给输入框） ----

    [Fact]
    public void SettingsViewModel_DragStripArgb_ComposesColorAndOpacity()
    {
        var vm = new SettingsViewModel(AppConfig.CreateDefault())
        {
            DragStripColor = "#0000FF",
            DragStripOpacity = 0.1,
        };

        // 0.1 × 255 = 25.5 → 四舍五入 26 = 0x1A
        vm.DragStripArgb.Should().Be("#1A0000FF");
    }

    [Fact]
    public void SettingsViewModel_DragStripArgb_SplitsBackIntoBothFields()
    {
        var vm = new SettingsViewModel(AppConfig.CreateDefault());

        vm.DragStripArgb = "#8000FF00";

        vm.DragStripColor.Should().Be("#00FF00"); // 颜色字段只留 RGB
        vm.DragStripOpacity.Should().BeApproximately(128 / 255.0, 0.001);
        vm.BuildConfig().DragStripOpacity.Should().BeApproximately(128 / 255.0, 0.001);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("#12345")]
    public void SettingsViewModel_DragStripArgb_InvalidValueIsKeptForValidation(string bad)
    {
        var vm = new SettingsViewModel(AppConfig.CreateDefault());

        vm.DragStripArgb = bad;

        // 不静默修正：原样留住，交给 Validate() 报"不是合法颜色"
        vm.DragStripColor.Should().Be(bad);
        vm.Validate().Should().BeFalse();
        vm.ValidationError.Should().Contain("拖动热区颜色");
    }

    [Fact]
    public void SettingsViewModel_DragStripArgb_RaisesOnEitherFieldChange()
    {
        var vm = new SettingsViewModel(AppConfig.CreateDefault());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.DragStripColor = "#00FF00";
        vm.DragStripOpacity = 0.5;

        // 颜色选择器绑的是合成值：改动任一字段都必须让它刷新，
        // 否则界面还显示旧颜色（Set 用 CallerMemberName，合成 setter 里必须显式传名字）。
        raised.Should().Contain(nameof(SettingsViewModel.DragStripArgb));
    }

    // ---- 设置实时生效与取消（§十二 待办 2） ----

    [Fact]
    public void SettingsViewModel_RevertToSnapshot_DiscardsUnsavedEdits()
    {
        var cfg = AppConfig.CreateDefault();
        var vm = new SettingsViewModel(cfg);

        vm.DefaultUrl = "http://127.0.0.1:9999/";
        vm.LaunchCommand = "changed --flag";
        vm.DragStripEnabled = !cfg.DragStripEnabled;
        vm.DragStripOpacity = 0.77;

        vm.RevertToSnapshot();

        vm.DefaultUrl.Should().Be(cfg.DefaultUrl);
        vm.LaunchCommand.Should().Be(cfg.LaunchCommand);
        vm.DragStripEnabled.Should().Be(cfg.DragStripEnabled);
        vm.DragStripOpacity.Should().Be(cfg.DragStripOpacity);
    }

    // ---- 页面宿主开关（文件拖放需要 HwndHost） ----

    [Fact]
    public void SettingsViewModel_UseHwndHost_DefaultsFalse_AndRoundTripsThroughBuildConfig()
    {
        var vm = new SettingsViewModel(AppConfig.CreateDefault());
        vm.UseHwndHost.Should().BeFalse();

        vm.UseHwndHost = true;

        vm.BuildConfig().UseHwndHost.Should().BeTrue();
    }

    [Fact]
    public void SettingsViewModel_UseHwndHost_RevertsWithSnapshot()
    {
        var cfg = AppConfig.CreateDefault();
        var vm = new SettingsViewModel(cfg) { UseHwndHost = true };

        vm.RevertToSnapshot();

        vm.UseHwndHost.Should().Be(cfg.UseHwndHost);
    }

    [Fact]
    public void SettingsViewModel_AcceptSnapshot_MovesBaseline()
    {
        var vm = new SettingsViewModel(AppConfig.CreateDefault());
        vm.DefaultUrl = "http://127.0.0.1:9999/";

        // 保存：新值成为基线
        vm.AcceptSnapshot(vm.BuildConfig());

        vm.DefaultUrl = "http://127.0.0.1:1234/";
        vm.RevertToSnapshot();

        // 取消应退回「上次保存」而不是「启动时」
        vm.DefaultUrl.Should().Be("http://127.0.0.1:9999/");
    }

    [Fact]
    public async Task Session_ClearError_ClearsMessageOnly()
    {
        // 场景：探测 401 → NeedsToken → 用户提交临时 URL、页面打开 → 清除错误提示。
        // 只清 ErrorMessage，状态机不动 —— 关窗逻辑在 Shell 层。
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Other, 401, "unauthorized") };
        using var vm = new SessionViewModel(probe, new FakeLauncher());

        await vm.StartSessionAsync(S2Config, CancellationToken.None);
        vm.ErrorMessage.Should().NotBeNullOrEmpty();
        vm.Phase.Should().Be(SessionPhase.NeedsToken);

        vm.ClearError();
        vm.ErrorMessage.Should().BeNull();
        vm.Phase.Should().Be(SessionPhase.NeedsToken);

        // 无错误时重复调用必须是安全的空操作（页面多次导航都会走到这里）
        vm.ClearError();
        vm.ErrorMessage.Should().BeNull();

        // 提交临时 URL 后状态机照常推进（与 ClearError 无关，此处一并锁定）
        var navigated = new List<string>();
        vm.NavigateRequested += navigated.Add;
        vm.ProvideTempUrl("https://127.0.0.1:3080/tok/x");
        vm.Phase.Should().Be(SessionPhase.Ready);
        navigated.Should().ContainSingle();
    }

    // ---- run.log 接线与脱敏（阶段 5） ----

    [Theory]
    [InlineData("http://127.0.0.1:3080/token/abc123", "http://127.0.0.1:3080/...")]
    [InlineData("https://localhost:443/x?q=1", "https://localhost/...")]
    [InlineData("http://127.0.0.1:3080/", "http://127.0.0.1:3080/")]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("not a url", "(非法 URL)")]
    [InlineData("", "(空)")]
    [InlineData(null, "(空)")]
    public void LogRedaction_Url_KeepsOriginOnly(string? input, string expected)
    {
        LogRedaction.Url(input).Should().Be(expected);
    }

    [Fact]
    public void LogRedaction_Line_RedactsEmbeddedUrls()
    {
        var line = "dsh web: http://127.0.0.1:3080/token/SECRET123 ready";
        var result = LogRedaction.Line(line);
        result.Should().NotContain("SECRET123");
        result.Should().Contain("http://127.0.0.1:3080/...");
        result.Should().Contain("ready");
    }

    [Fact]
    public async Task Session_LogLine_EmitsRedactedNavigationUrl()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Unreachable, null, null) };
        var launcher = new FakeLauncher
        {
            Result = new LaunchResult("https://127.0.0.1:3080/token/SECRET42", false, "x"),
        };
        using var vm = new SessionViewModel(probe, launcher);
        var lines = new List<string>();
        vm.LogLine += lines.Add;

        await vm.StartSessionAsync(S2Config, CancellationToken.None);

        string.Join("\n", lines).Should().NotContain("SECRET42");
        lines.Should().Contain(l => l.Contains("[session] 开始会话"));
        lines.Should().Contain(l => l.Contains("抓到 URL"));
    }

    [Fact]
    public async Task Session_ProvideTempUrl_LogsRedactedOnly()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Other, 401, null) };
        var launcher = new FakeLauncher();
        using var vm = new SessionViewModel(probe, launcher);
        var lines = new List<string>();
        vm.LogLine += lines.Add;

        await vm.StartSessionAsync(S2Config, CancellationToken.None);
        vm.Phase.Should().Be(SessionPhase.NeedsToken);

        vm.ProvideTempUrl("http://127.0.0.1:3080/token/TOPSECRET");

        vm.Phase.Should().Be(SessionPhase.Ready);
        var joined = string.Join("\n", lines);
        joined.Should().Contain("收到临时 URL http://127.0.0.1:3080/...");
        joined.Should().NotContain("TOPSECRET");
    }

    // ---- 临时 URL 的容错（回归：界面崩溃「OnTokenProvided 仅允许在 NeedsToken 调用，当前: Ready」） ----

    /// <summary>
    /// 用户粘的临时 URL 常常没有 scheme（设置窗口的说明就写着示例形如
    /// <c>127.0.0.1:3080/?token=...</c>）。这种值必须被补成 <c>http://</c> 再导航，
    /// 且不能把状态机带崩。
    /// </summary>
    [Fact]
    public async Task Session_ProvideTempUrl_WithoutScheme_IsNormalizedToHttp()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Other, 401, null) };
        using var vm = new SessionViewModel(probe, new FakeLauncher());
        var navUrls = new List<string>();
        vm.NavigateRequested += url => navUrls.Add(url);

        await vm.StartSessionAsync(S2Config, CancellationToken.None);
        vm.Phase.Should().Be(SessionPhase.NeedsToken);

        vm.ProvideTempUrl("127.0.0.1:3080/?token=aHuVnlzmAn7t");

        vm.Phase.Should().Be(SessionPhase.Ready);
        navUrls.Should().ContainSingle()
            .Which.Should().Be("http://127.0.0.1:3080/?token=aHuVnlzmAn7t");
    }

    /// <summary>
    /// 核心回归：会话已经 Ready（用户点了「保存并重启会话」后重新走到 Ready，
    /// 或者前一次临时 URL 已经生效），而设置窗口还开着，用户又点了一次「打开」。
    /// 此时**绝不能抛异常** —— 抛了就会冒到 Dispatcher 上弹「发生未处理异常」框。
    /// 应当直接导航、保持 Ready。
    /// </summary>
    [Fact]
    public async Task Session_ProvideTempUrl_WhenAlreadyReady_DoesNotThrow()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Other, 401, null) };
        using var vm = new SessionViewModel(probe, new FakeLauncher());
        var navUrls = new List<string>();
        vm.NavigateRequested += url => navUrls.Add(url);

        await vm.StartSessionAsync(S2Config, CancellationToken.None);
        vm.Phase.Should().Be(SessionPhase.NeedsToken);

        vm.ProvideTempUrl("http://127.0.0.1:3080/token/first");
        vm.Phase.Should().Be(SessionPhase.Ready);

        // 再点一次：状态机此刻是 Ready
        var act = () => vm.ProvideTempUrl("http://127.0.0.1:3080/token/second");

        act.Should().NotThrow();
        vm.Phase.Should().Be(SessionPhase.Ready);
        navUrls.Should().HaveCount(2);
    }

    /// <summary>空串 / 空白不该进入状态机 —— 否则同样是"非 NeedsToken 阶段被调用"的崩法。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Session_ProvideTempUrl_BlankInput_IsIgnored(string url)
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Other, 401, null) };
        using var vm = new SessionViewModel(probe, new FakeLauncher());
        var navUrls = new List<string>();
        vm.NavigateRequested += url => navUrls.Add(url);

        await vm.StartSessionAsync(S2Config, CancellationToken.None);
        vm.Phase.Should().Be(SessionPhase.NeedsToken);

        var act = () => vm.ProvideTempUrl(url);

        act.Should().NotThrow();
        vm.Phase.Should().Be(SessionPhase.NeedsToken);
        navUrls.Should().BeEmpty();
    }

    /// <summary>Failed 阶段提供临时 URL 同样不能崩（设置窗口在错误状态下也是开着的）。</summary>
    [Fact]
    public async Task Session_ProvideTempUrl_WhenFailed_DoesNotThrow()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Unreachable, null, null) };
        var launcher = new FakeLauncher { Result = new LaunchResult(null, false, "no output") };
        using var vm = new SessionViewModel(probe, launcher);
        var navUrls = new List<string>();
        vm.NavigateRequested += url => navUrls.Add(url);

        await vm.StartSessionAsync(S2Config, CancellationToken.None);
        vm.Phase.Should().Be(SessionPhase.Failed);

        var act = () => vm.ProvideTempUrl("http://127.0.0.1:3080/token/late");

        act.Should().NotThrow();
        navUrls.Should().ContainSingle();
    }

    [Fact]
    public async Task Session_ConsoleOutput_IsLoggedRedacted()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Unreachable, null, null) };
        var launcher = new FakeLauncher
        {
            Result = new LaunchResult(null, true, "ready"),
            OutputLines = { "listening on http://127.0.0.1:3080/token/ABC" },
        };
        using var vm = new SessionViewModel(probe, launcher);
        var lines = new List<string>();
        vm.LogLine += lines.Add;

        await vm.StartSessionAsync(S2Config, CancellationToken.None);

        vm.ConsoleOutput.Should().Contain("ABC");
        string.Join("\n", lines).Should().NotContain("ABC");
    }

    // ---- URL 形状指纹：只记结构不记内容（§5.4 安全，且可诊断） ----

    [Fact]
    public void UrlFingerprint_RevealsShapeWithoutLeakingToken()
    {
        var fp = LogRedaction.UrlFingerprint("http://127.0.0.1:3080/tok/SUPERSECRET?token=ALSOSECRET");

        fp.Should().NotContain("SUPERSECRET");
        fp.Should().NotContain("ALSOSECRET");
        fp.Should().Contain("路径段数=2");
        fp.Should().Contain("段长=[3,11]");
        fp.Should().Contain("query键=[token]");
        fp.Should().Contain("含控制字符=False");
    }

    [Fact]
    public void UrlFingerprint_DistinguishesBareOriginFromTokenUrl()
    {
        LogRedaction.UrlFingerprint("http://127.0.0.1:3080/")
            .Should().Contain("路径段数=0");
        LogRedaction.UrlFingerprint("http://127.0.0.1:3080/tok/abc")
            .Should().Contain("路径段数=2");
    }

    [Fact]
    public void UrlFingerprint_FlagsControlCharacters_FromAnsiPollutedUrl()
    {
        // 上色行的复位序列被 \S+ 吞进 URL —— 这就是 404 的元凶，必须能从日志一眼看出
        var fp = LogRedaction.UrlFingerprint("http://127.0.0.1:3080/tok/abc\u001B[0m");

        fp.Should().Contain("含控制字符=True");
    }

    [Theory]
    [InlineData(null, "(空)")]
    [InlineData("", "(空)")]
    [InlineData("not a url", "(非法 URL) 含控制字符=False")]
    public void UrlFingerprint_HandlesInvalidInput(string? url, string expected)
    {
        LogRedaction.UrlFingerprint(url).Should().Be(expected);
    }

    [Fact]
    public async Task Session_LogsUrlShapeForExtractedUrl()
    {
        var probe = new FakeProbe { Result = new ProbeResult(ProbeOutcome.Unreachable, null, null) };
        var launcher = new FakeLauncher
        {
            Result = new LaunchResult("https://127.0.0.1:3080/token/SUPERSECRET42", false, "x"),
        };
        using var vm = new SessionViewModel(probe, launcher);
        var lines = new List<string>();
        vm.LogLine += lines.Add;

        await vm.StartSessionAsync(S2Config, CancellationToken.None);

        var joined = string.Join("\n", lines);
        joined.Should().Contain("[nav] URL 形状：");
        joined.Should().Contain("路径段数=2");
        joined.Should().NotContain("SUPERSECRET42");
    }
}
