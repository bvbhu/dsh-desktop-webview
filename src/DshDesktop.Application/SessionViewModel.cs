using System.Text;
using DshDesktop.Domain;

namespace DshDesktop.Application;

public sealed class SessionViewModel : ViewModelBase, IDisposable
{
    private readonly SessionStateMachine _state = new();
    private readonly IEndpointProbe _probe;
    private readonly IServerLauncher _launcher;
    private readonly StringBuilder _console = new();
    private readonly object _consoleLock = new();

    private SessionPhase _phase = SessionPhase.Idle;
    private string? _errorMessage;

    public SessionPhase Phase
    {
        get => _phase;
        private set => Set(ref _phase, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => Set(ref _errorMessage, value);
    }

    public string ConsoleOutput
    {
        get
        {
            lock (_consoleLock)
                return _console.ToString();
        }
    }

    public event Action<string>? NavigateRequested;
    public event Action? OpenConfigRequested;

    /// <summary>
    /// 行级日志事件，由 Shell 层订阅后写入 run.log。
    /// 事件只传递已脱敏的文本，token 不会落盘（设计文档 §5.4）。
    /// </summary>
    public event Action<string>? LogLine;

    public SessionViewModel(IEndpointProbe probe, IServerLauncher launcher)
    {
        _probe = probe;
        _launcher = launcher;
        _state.PhaseChanged += (_, p) => Phase = p;
    }

    public async Task StartSessionAsync(AppConfig cfg, CancellationToken ct)
    {
        try
        {
            Log($"[session] 开始会话 策略={cfg.ServiceStrategy} 默认URL={LogRedaction.Url(cfg.DefaultUrl)}");
            switch (cfg.ServiceStrategy)
            {
                case ServiceStrategy.NeverStart:
                    Log("[session] S1 永不启动：直接导航默认 URL");
                    _state.NavigateDirectly();
                    NavigateRequested?.Invoke(cfg.DefaultUrl);
                    break;
                case ServiceStrategy.AlwaysStart:
                    Log("[session] S3 直接启动：跳过探测");
                    _state.BeginLaunch();
                    await LaunchAndCaptureAsync(cfg, ct);
                    break;
                case ServiceStrategy.ProbeThenStart:
                    await ProbeThenMaybeLaunchAsync(cfg, ct);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"[error] {ex.GetType().Name}: {LogRedaction.Line(ex.Message)}");
            ErrorMessage = ex.Message;
            OpenConfigRequested?.Invoke();
        }
    }

    private async Task ProbeThenMaybeLaunchAsync(AppConfig cfg, CancellationToken ct)
    {
        _state.BeginProbe();
        Log($"[probe] 探测 {LogRedaction.Url(cfg.DefaultUrl)}");
        var probe = await _probe.ProbeAsync(cfg.DefaultUrl, ct);
        Log($"[probe] 结果 outcome={probe.Outcome} status={probe.StatusCode?.ToString() ?? "-"} detail={LogRedaction.Line(probe.Detail)}");
        _state.OnProbeResult(probe);

        var action = ServiceStrategyResolver.Resolve(ServiceStrategy.ProbeThenStart, probe);
        switch (action)
        {
            case ServiceAction.Navigate:
                Log("[session] 探测返回 200：直接打开页面");
                NavigateRequested?.Invoke(cfg.DefaultUrl);
                break;
            case ServiceAction.Launch:
                await LaunchAndCaptureAsync(cfg, ct);
                break;
            case ServiceAction.OpenConfigForToken:
                Log("[session] 探测返回非 200：打开配置窗口等待临时 URL");
                ErrorMessage = $"探测返回 {(probe.StatusCode?.ToString() ?? "连接错误")}: {probe.Detail}";
                OpenConfigRequested?.Invoke();
                break;
        }
    }

    private async Task LaunchAndCaptureAsync(AppConfig cfg, CancellationToken ct)
    {
        Log($"[launch] 执行命令：{LogRedaction.Line(cfg.LaunchCommand)}");
        var progress = new SyncProgress(AppendConsole);
        var result = await _launcher.LaunchAsync(cfg, progress, ct);
        Log($"[launch] 命令结束 命中URL={result.Url is not null} 命中成功标志={result.HitSuccessMarker}");

        if (result.Url is not null)
        {
            Log($"[session] 抓到 URL {LogRedaction.Url(result.Url)}：直接导航");
            // 只记形状不记内容：出问题时能立刻区分"导航到裸 origin""路径不对""URL 被
            // 控制字符污染"，同时不违反 §5.4。
            Log($"[nav] URL 形状：{LogRedaction.UrlFingerprint(result.Url)}");
            _state.OnUrlExtracted();
            NavigateRequested?.Invoke(result.Url);
        }
        else if (result.HitSuccessMarker)
        {
            Log("[session] 未抓到 URL，改用成功标志回退探测默认 URL");
            var fallback = await _probe.ProbeAsync(cfg.DefaultUrl, ct);
            _state.OnFallbackProbeResult(fallback);
            Log($"[probe] 回退探测 outcome={fallback.Outcome} status={fallback.StatusCode?.ToString() ?? "-"}");
            if (fallback.Outcome == ProbeOutcome.Ok)
                NavigateRequested?.Invoke(cfg.DefaultUrl);
            else
            {
                Log("[error] 回退探测未返回 200");
                ErrorMessage = "回退探测失败";
                OpenConfigRequested?.Invoke();
            }
        }
        else
        {
            // 两级判定都没命中。这里要分清两种情形，它们对用户意味着完全不同的动作：
            //   ① 进程已经退出且退出码非零 → 启动命令本身就是错的（命令名打错、参数不认）。
            //      这在几百毫秒内就已确定，没理由让用户陪满 30 秒超时。
            //   ② 进程仍在跑（ExitCode 为 null）→ 服务起得慢，属于真超时。
            // 两者的区分依赖 LaunchResult.ExitCode 的三态语义，否则只能笼统报超时。
            if (result.ExitCode is not null and not 0)
            {
                Log($"[launch] 启动命令非零退出（退出码 {result.ExitCode}），标记失败并打开配置窗口");
                _state.OnProcessExitFailed();
                ErrorMessage = $"启动命令异常退出（退出码 {result.ExitCode}）。请检查「启动命令」是否正确。";
            }
            else
            {
                Log("[session] 两级判定均未命中：启动超时或进程未退出");
                _state.OnLaunchTimeout();
                ErrorMessage = "启动超时：命令未输出可识别的 URL";
            }

            OpenConfigRequested?.Invoke();
        }
    }

    /// <summary>
    /// 页面已成功打开（WebView2 导航完成），清除错误提示。
    /// <para>
    /// 场景：探测 401 → NeedsToken → 用户粘贴临时 URL → 页面打开。此刻标题行里那条
    /// 「错误：探测返回 401」已经过时，留着会误导。只清 ErrorMessage，不动状态机 ——
    /// 状态在 OnTokenProvided 时已经落位；关窗由 Shell 层在 NavigationCompleted 里做。
    /// </para>
    /// </summary>
    public void ClearError()
    {
        if (ErrorMessage is null)
            return;

        Log("[session] 页面已成功打开，清除错误提示");
        ErrorMessage = null;
    }

    /// <summary>
    /// 用户从设置窗口提交临时 URL。
    /// <para>
    /// 这里必须**从不抛异常**。原实现直接调 <c>_state.OnTokenProvided()</c>，而那个方法
    /// 只接受 <see cref="SessionPhase.NeedsToken"/> —— 一旦提交时状态机已不在 NeedsToken
    /// （最常见：用户连点两次「打开」、或上一次临时 URL 已生效），异常会冒到 Dispatcher
    /// 弹「发生未处理异常」，整个界面从此不可用。
    /// </para>
    /// <para>
    /// 语义定成"尽力导航"：URL 能用就导航，状态机能优雅落位就落位，落不了位就只记一行日志。
    /// 停止把内部状态机的合法性检查当作对用户输入的校验 —— 用户点「打开」的意思
    /// 永远是"打开这个地址"，与内部处于哪个阶段无关。
    /// </para>
    /// </summary>
    public void ProvideTempUrl(string url)
    {
        var normalized = UrlNormalizer.EnsureScheme(url);
        if (normalized is null)
        {
            Log("[session] 临时 URL 为空，已忽略");
            return;
        }

        Log($"[session] 收到临时 URL {LogRedaction.Url(normalized)}（token 不落盘）");

        // 状态机只在 NeedsToken 时推进；其它阶段状态已经正确（Ready/Failed），
        // 或者正在跑异步管线（Probing/Launching，那时不该被外部打断），都不推进。
        if (_state.Phase == SessionPhase.NeedsToken)
            _state.OnTokenProvided();

        NavigateRequested?.Invoke(normalized);
    }

    /// <summary>
    /// 把外壳侧的一次动作写进控制台（同步进 run.log），让用户看见结果。
    /// <para>
    /// 为什么需要它："清除 Cookie"这类动作发生在 Shell 层、动的是 WebView2，不属于会话状态机；
    /// 但结果不能只是一个没人看见的副作用 —— 控制台就是这个出口。
    /// </para>
    /// </summary>
    public void Note(string message)
    {
        lock (_consoleLock)
            _console.AppendLine($"[shell] {message}");

        Raise(nameof(ConsoleOutput));
        Log($"[shell] {LogRedaction.Line(message)}");
    }

    private void AppendConsole(string line)
    {
        lock (_consoleLock)
            _console.AppendLine(line);
        Raise(nameof(ConsoleOutput));
        Log($"[launch] {LogRedaction.Line(line)}");
    }

    private void Log(string message) => LogLine?.Invoke(message);

    public void Dispose()
    {
        if (_launcher is IDisposable disposable)
            disposable.Dispose();
    }
}
