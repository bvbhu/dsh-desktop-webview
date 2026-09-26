using System.Text;
using DshDesktop.Domain;

namespace DshDesktop.Application;

public sealed class SessionViewModel : ViewModelBase, IDisposable
{
    private readonly SessionStateMachine _state = new();
    private readonly IEndpointProbe _probe;
    private readonly IEndpointProbe _livenessProbe;
    private readonly IServerLauncher _launcher;
    private readonly StringBuilder _console = new();
    private readonly object _consoleLock = new();

    private SessionPhase _phase = SessionPhase.Idle;
    private string? _errorMessage;
    private bool _serviceRunning;
    private bool _serviceChanging;
    private bool _serviceOwned;

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

    /// <summary>
    /// 当前探测到的服务状态：true = 端口可访问（已启动），false = 未启动/未知。
    /// 与 <see cref="Phase"/> 解耦：Phase 在启动完成后就停在 Ready，而服务状态由独立的
    /// 探测驱动（设置窗口每次打开、或用户点「刷新状态」时都可重新探测）。
    /// </summary>
    public bool ServiceRunning
    {
        get => _serviceRunning;
        private set => Set(ref _serviceRunning, value);
    }

    /// <summary>
    /// 服务状态变化进行中（正在启动/停止/重启）。为 true 时设置窗口把三个服务按钮置灰，
    /// 避免在同一条管线上重复触发。
    /// </summary>
    public bool ServiceChanging
    {
        get => _serviceChanging;
        private set
        {
            if (Set(ref _serviceChanging, value))
                Raise(nameof(ServiceIdle));
        }
    }

    /// <summary>
    /// 当前运行实例是否由本软件启动（本软件持有它的进程句柄 / Job Object）。
    /// <para>
    /// <c>ServiceOwned=false 且 ServiceRunning=true</c> = 复用外部实例：端口被外部进程占用，
    /// 本软件从未启动过它，也没有关闭它的能力。刷新探测只能确认端口可达，归属由启动/停止
    /// 流程维护（探测 Ok 时我们从未启动 → 外部；我们自己拉起且后探测 Ok → 我们启动）。
    /// </para>
    /// </summary>
    public bool ServiceOwned
    {
        get => _serviceOwned;
        private set => Set(ref _serviceOwned, value);
    }

    /// <summary>是否既不在启动服务中也不在停止服务中（用于按钮的 IsEnabled）。</summary>
    public bool ServiceIdle => !_serviceChanging;

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
    /// 请求停止本地服务。事件只传递"要停"这个意图，真正的关闭发生在 Shell 层
    /// （<c>MainWindow.OnStopServiceRequested</c>），因为 Job Object / 进程树管理在基础设施层。
    /// </summary>
    public event Action? StopServiceRequested;

    /// <summary>
    /// 行级日志事件，由 Shell 层订阅后写入 run.log。
    /// 事件只传递已脱敏的文本，token 不会落盘（设计文档 §5.4）。
    /// </summary>
    public event Action<string>? LogLine;

    /// <param name="probe">
    /// 启动探测用的探针（WebView 实现）：只用于「页面显示之前」的 S2 分流与回退探测 ——
    /// 它必须与真实导航共用 cookie / 会话（§6）。
    /// </param>
    /// <param name="livenessProbe">
    /// 存活检查用的探针（网络实现）。**必须与 <paramref name="probe"/> 分开**：
    /// 存活检查发生在页面已经加载之后，用 WebView 探针会把用户正在看的页面导航走，
    /// 这就是"反复刷新页面"的来源。默认回落到 <paramref name="probe"/>，
    /// 便于既有测试与预览 harness 不改签名。
    /// </param>
    public SessionViewModel(
        IEndpointProbe probe,
        IServerLauncher launcher,
        IEndpointProbe? livenessProbe = null)
    {
        _probe = probe;
        _livenessProbe = livenessProbe ?? probe;
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

            // 启动会话结束后再探测一次服务状态：这是用户第一次进入设置窗口前能拿到的最新状态，
            // 避免初始显示「未知」。失败不影响会话（ServiceRunning 保持当前值）。
            if (cfg.ServiceStrategy != ServiceStrategy.NeverStart)
                _ = SafeRefreshServiceStateAsync(cfg, ct);
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

    private async Task SafeRefreshServiceStateAsync(AppConfig cfg, CancellationToken ct)
    {
        try
        {
            await RefreshServiceStateAsync(cfg, ct);
        }
        catch (OperationCanceledException)
        {
            // 启动时传入的 ct 被取消：会话关闭，无需刷新状态。
        }
        catch (Exception ex)
        {
            // 刷新状态失败绝不影响会话本身。
            Log($"[probe] 启动后刷新状态失败：{ex.GetType().Name}: {ex.Message}");
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

    /// <summary>
    /// <paramref name="driveStateMachine"/>：状态机只管「首次打开」这条管线（Idle → Probing/Launching → …）。
    /// 手动「启动服务」在会话已到 Ready/Failed 时调用本方法，必须传 false —— 否则
    /// <c>OnUrlExtracted</c> 等只允许 Launching 阶段调用的转移会直接抛异常，被外层 catch 成
    /// 误导性的红字错误（2026-09-17 用户实机发现）。
    /// </summary>
    private async Task LaunchAndCaptureAsync(AppConfig cfg, CancellationToken ct, bool driveStateMachine = true)
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
            if (driveStateMachine)
                _state.OnUrlExtracted();
            ServiceOwned = true;
            NavigateRequested?.Invoke(result.Url);
        }
        else if (result.HitSuccessMarker)
        {
            Log("[session] 未抓到 URL，改用成功标志回退探测默认 URL");
            var fallback = await _probe.ProbeAsync(cfg.DefaultUrl, ct);
            if (driveStateMachine)
                _state.OnFallbackProbeResult(fallback);
            Log($"[probe] 回退探测 outcome={fallback.Outcome} status={fallback.StatusCode?.ToString() ?? "-"}");
            if (fallback.Outcome == ProbeOutcome.Ok)
            {
                ServiceOwned = true;
                NavigateRequested?.Invoke(cfg.DefaultUrl);
            }
            else
            {
                Log("[error] 回退探测未返回 200");
                ServiceOwned = false;
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
                if (driveStateMachine)
                    _state.OnProcessExitFailed();
                ServiceOwned = false;
                ErrorMessage = $"启动命令异常退出（退出码 {result.ExitCode}）。请检查「启动命令」是否正确。";
            }
            else
            {
                Log("[session] 两级判定均未命中：启动超时或进程未退出");
                if (driveStateMachine)
                    _state.OnLaunchTimeout();
                ServiceOwned = false;
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

    // ================= 服务状态与控制（设置窗口 2026-09-17） =================

    /// <summary>
    /// 重新探测一次服务状态，更新 <see cref="ServiceRunning"/> 与 <see cref="ServiceOwned"/>。
    /// 设置窗口每次打开时都会调用一次，以拿到最新状态；任何异常都只记日志，不冒泡。
    /// <para>
    /// 归属规则：探测可达只能证明端口有实例在听，不能证明是谁启动的；反过来，一次探不到也
    /// 不能证明我们拉起的实例没了 —— 本方法的探测是 WebView 导航，实测在启动瞬间会假失败
    /// （<c>dsh web</c> 先打印 URL、端口晚几秒才开始监听：2026-09-24 实机 19:44 日志里
    /// URL 行后 19ms 探到 <c>Unreachable</c>，3~5 秒后再探就是 200）。
    /// 所以这里**只修可达性、绝不动归属**：归属只由真正掌握进程信息的两处决定 ——
    /// 启动管线（<c>LaunchAndCaptureAsync</c>）与停止（<see cref="CompleteStopService"/>/<see cref="CompleteStartService"/>)。
    /// 归零过早会把自家实例误报成「复用外部实例」（用户 2026-09-24 报告的症状）。
    /// 残留的 <c>ServiceOwned=true</c> 在 <c>ServiceRunning=false</c> 时界面上不可见（显示「服务未启动」），
    /// 下一次启动/停止流程会把它摆正。
    /// </para>
    /// </summary>
    public async Task RefreshServiceStateAsync(AppConfig cfg, CancellationToken ct)
    {
        try
        {
            // 走网络探针，绝不走 WebView：本方法在设置窗口每次打开时都会跑，
            // 用 WebView 探针会把用户正在看的页面导航走（"反复刷新"的主因之一）。
            var probe = await _livenessProbe.ProbeAsync(cfg.DefaultUrl, ct);
            // 端口有应答（200 或 401 等非 200）就说明服务活着 —— 裸 DefaultUrl 常返回 401，
            // 那是"要 token"而不是"没在跑"。只有连不上（Unreachable）才算没服务。
            ServiceRunning = probe.Outcome != ProbeOutcome.Unreachable;
            Log($"[probe] 手动刷新状态 outcome={probe.Outcome} → ServiceRunning={ServiceRunning} ServiceOwned={ServiceOwned}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"[probe] 刷新状态失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 无副作用的服务状态刷新（不改 <see cref="ServiceChanging"/>）。设置窗口「刷新状态」按钮
    /// 与打开窗口时的自动刷新都走这里。
    /// </summary>
    public async Task RefreshServiceStateNoGuardAsync(AppConfig cfg, CancellationToken ct)
    {
        if (ServiceChanging)
            return;

        await RefreshServiceStateAsync(cfg, ct);
    }

    /// <summary>
    /// 启动本地服务。流程：探测一次（若已启动则不重复拉起）→ 走既有启动管线（复用
    /// <c>LaunchAndCaptureAsync</c>，URL 提取、成功标志、回退探测全部不变）→ 启动完成后再
    /// 探测一次以刷新 <see cref="ServiceRunning"/>。任何阶段的异常都只记日志，不冒泡。
    /// </summary>
    public async Task StartServiceAsync(AppConfig cfg, CancellationToken ct)
    {
        if (ServiceChanging)
        {
            Log("[session] 服务状态变化进行中，忽略重复的启动请求");
            return;
        }

        ServiceChanging = true;
        try
        {
            await StartServiceNoGuardAsync(cfg, ct);
        }
        finally
        {
            ServiceChanging = false;
        }
    }

    /// <summary>
    /// 无副作用的启动尝试（供 Shell 层在「重启」等场景复用）：若服务未运行则拉起，运行中则直接刷新状态。
    /// 与 <see cref="StartServiceAsync"/> 的区别是不改 <see cref="ServiceChanging"/>，由调用方持有守卫。
    /// </summary>
    public async Task TryStartServiceNoGuardAsync(AppConfig cfg, CancellationToken ct) =>
        await StartServiceNoGuardAsync(cfg, ct);

    private async Task StartServiceNoGuardAsync(AppConfig cfg, CancellationToken ct)
    {
        try
        {
            Log($"[session] 手动启动服务：{LogRedaction.Line(cfg.LaunchCommand)}");

            // 已在运行则不必重复启动；探测失败（未启动）才是正常启动入口。
            // 判定用 != Unreachable：端口有应答（含 401）就说明实例已在，重复拉起只会撞端口。
            // 这里**不动归属** —— 归属回答"是不是本软件拉起的"，预探测答不了这个问题；
            // 若本来就归我们（慢启动重入），清掉会把它误判成外部实例。
            var pre = await _livenessProbe.ProbeAsync(cfg.DefaultUrl, ct);
            if (pre.Outcome != ProbeOutcome.Unreachable)
            {
                Log($"[session] 服务已在运行（探测 outcome={pre.Outcome}），跳过启动");
                ServiceRunning = true;
                return;
            }

            // 状态机只在 Idle 时才允许进入 Launching；若会话已经走到 Ready/Failed 等
            // 后续阶段，跳过状态机（它管的是"首次打开"这条管线），但启动命令照常执行。
            var canTransition = _state.Phase == SessionPhase.Idle;
            if (canTransition)
                _state.BeginLaunch();

            await LaunchAndCaptureAsync(cfg, ct, driveStateMachine: canTransition);

            if (canTransition && _state.Phase == SessionPhase.Launching)
            {
                // 没抓到 URL、没命中成功标志、也没超时（例如命令直接退出码 0）：状态机不能
                // 停在 Launching —— 启动请求已经完成，只能回落到 Idle 才能被下一次请求复用。
                Log("[session] 启动请求完成但状态机仍停在 Launching，回落到 Idle");
                _state.Reset();
            }

            var post = await _livenessProbe.ProbeAsync(cfg.DefaultUrl, ct);
            // 后探测只用来确认"端口有没有应答"，不反向清归属：真正启动失败时
            // LaunchAndCaptureAsync 已把 ServiceOwned 置 false；这里若因"抓到 URL 但端口还没绑好"
            // 的瞬态未应答而清归属，慢启动的自家实例就会被误报成外部实例。
            // `|| ServiceOwned`：抓到 URL 就证明我们拉起的进程确实起来了（它自己打印了 URL），
            // 而端口要比 URL 行晚 1.5~2 秒才开始 LISTEN（2026-09-24 实机）—— 单凭这次探测的
            // Unreachable 就写 false，会让用户刚点完「启动服务」就看到红色的「服务未启动」。
            ServiceRunning = post.Outcome != ProbeOutcome.Unreachable || ServiceOwned;
            if (ServiceRunning)
                ServiceOwned = true;
            Log($"[probe] 启动后状态刷新 outcome={post.Outcome} → ServiceRunning={ServiceRunning} ServiceOwned={ServiceOwned}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"[error] 启动服务失败：{ex.GetType().Name}: {LogRedaction.Line(ex.Message)}");
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// 停止本地服务：通过事件把请求交给 Shell 层（真正的关闭发生在 <c>MainWindow.OnStopServiceRequested</c>，
    /// 因为 Job Object / 进程树管理在基础设施层）。返回 true 表示已受理（Shell 层会异步完成并回写
    /// <see cref="ServiceRunning"/>），false 表示当前无服务可停。
    /// </summary>
    public bool RequestStopService()
    {
        if (ServiceChanging)
        {
            Log("[session] 服务状态变化进行中，忽略重复的停止请求");
            return false;
        }

        ServiceChanging = true;
        Log("[session] 请求停止服务");
        StopServiceRequested?.Invoke();
        return true;
    }

    /// <summary>
    /// Shell 层完成停止操作后必须调用，用来复位 <see cref="ServiceChanging"/> 并刷新状态。
    /// <paramref name="stillReachable"/> = 停止后端口是否仍可达：可达说明端口被外部进程占用
    /// （或进程树没杀干净）——状态必须如实保留 running，归属清零（外部实例/复用），
    /// 而不是谎报「已停止」。
    /// </summary>
    public void CompleteStopService(bool stillReachable, string message)
    {
        ServiceRunning = stillReachable;
        ServiceOwned = false;
        ServiceChanging = false;
        Note(message);
        if (stillReachable)
            Log("[session] 停止后端口仍可达：服务可能由外部进程托管（详见控制台）");
    }

    /// <summary>
    /// Shell 层启动/重启服务完成后调用：刷新服务状态（true=已启动，false=启动失败/未启动）。
    /// 归属在启动管线里已经摆对（预探测 Ok = 外部复用 → 不清；我们拉起且后探测 Ok = 我们的实例
    /// → 保持 true），这里**只**在失败时清归属，成功时不动。
    /// </summary>
    public void CompleteStartService(bool running, string message)
    {
        ServiceRunning = running;
        if (!running)
            ServiceOwned = false;
        ServiceChanging = false;
        Note(message);
    }

    /// <summary>Shell 层完成一次启动尝试后调用：仅复位 <see cref="ServiceChanging"/>（状态已由调用方写入）。</summary>
    public void CompleteStartAttempt() => ServiceChanging = false;

    /// <summary>Shell 层完成一次停止尝试后调用：仅复位 <see cref="ServiceChanging"/>（状态已由调用方写入）。</summary>
    public void CompleteStopAttempt() => ServiceChanging = false;

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
