namespace DshDesktop.Domain;

/// <summary>
/// 服务就绪等待：把「端口什么时候开始 LISTEN」这件事收敛成**一次**等待，
/// 而不是各处各写一套盲重试。
/// <para>
/// 背景（2026-09-24 实机）：<c>dsh web</c> 先打印 URL，端口要 1.5~2 秒后才开始 LISTEN。
/// 此前 <c>MainWindow</c> 里有两条互不知情的重试链——导航失败后 1.5s 重导航一次
/// （<c>ScheduleRealNavRetry</c>），启动/重启后最多 5 次探测（<c>ProbeAfterStartAsync</c>）。
/// 两条链各自都在往**同一个可见 WebView** 上发导航，于是冷启动会看到页面反复刷新。
/// </para>
/// <para>
/// 这个类的职责只有一件：<see cref="WaitAsync"/> 被并发调用时**只跑一次轮询**，
/// 所有调用者共享同一个结果。调用方负责在拿到结果后只导航一次。
/// </para>
/// <para>
/// 本类在 Domain 层，只依赖 <see cref="IEndpointProbe"/> 与 <see cref="TimeProvider"/>，
/// 不碰 WPF/WebView2，可直接单测。
/// </para>
/// </summary>
public sealed class ServiceReadinessWaiter
{
    private readonly IEndpointProbe _probe;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private Task<ProbeResult>? _inFlight;
    private string? _inFlightUrl;
    private CancellationTokenSource? _runCts;

    /// <summary>轮询间隔。默认 350ms：够密以致命中 LISTEN 后立刻返回，又不至于把目标打满。</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(350);

    /// <summary>
    /// 总预算，默认 12 秒。**这是软上界**：只在每轮开头检查是否超时，
    /// 因此最后一轮探测自身的耗时（HTTP 3s / WebView 10s）可能叠加在预算之上。
    /// </summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>进度回调（每次"仍未就绪"的探测后调用一次，不重复），用于写 run.log。</summary>
    public Action<int, ProbeResult>? OnAttempt { get; init; }

    public ServiceReadinessWaiter(IEndpointProbe probe, TimeProvider? timeProvider = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 等待 <paramref name="url"/> 就绪。
    /// <para>
    /// 语义：探测到**任何应答**（含 401 等非 200）即视为就绪——裸 <c>DefaultUrl</c> 常返回 401，
    /// 那是"要 token"而不是"没在跑"（§4.7）。连接失败/超时继续轮询，直到预算耗尽，
    /// 此时返回最后一次的探测结果（<see cref="ProbeOutcome.Unreachable"/>），由调用方按失败处理。
    /// </para>
    /// <para>
    /// 并发去重：同一 URL 上已有等待在跑时，后来的调用者直接复用那一轮轮询，不重复探测。
    /// 但每个调用者仍保留自己的取消权——复用分支用 <c>WaitAsync(ct)</c> 包一层，
    /// 免得后来者的 token 被静默吞掉。URL 变了（例如用户提交了新的临时 URL）则作废旧等待、另起一轮。
    /// </para>
    /// </summary>
    public Task<ProbeResult> WaitAsync(string url, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false } existing
                && string.Equals(_inFlightUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                // 直接返回 existing 会让后来者的 ct 失效：即使它已取消也照样拿到正常结果。
                // 包一层后，取消语义对每个调用者各自成立，而共享的那轮轮询不受影响。
                return ct.CanBeCanceled ? existing.WaitAsync(ct) : existing;
            }

            return StartLocked(url, ct);
        }
    }

    /// <summary>
    /// 无条件发起一次全新等待（供「启动服务」这类明确的用户动作调用：
    /// 用户点了一次按钮，就该有一次真实的重新检查，哪怕上一轮还没结束）。
    /// 上一轮会被作废，其等待者拿到它最后一次的探测结果而不是异常。
    /// </summary>
    public Task<ProbeResult> RestartAsync(string url, CancellationToken ct = default)
    {
        lock (_gate)
            return StartLocked(url, ct);
    }

    /// <summary>是否有等待正在进行（界面据此显示「正在连接服务…」）。</summary>
    public bool IsWaiting
    {
        get
        {
            lock (_gate)
                return _inFlight is { IsCompleted: false };
        }
    }

    /// <summary>
    /// 启动一轮轮询，并作废上一轮。**同一时刻只允许一轮在跑** —— 否则
    /// 「单次 in-flight」的承诺就是假的（旧循环会一直跑到自己预算耗尽）。
    /// </summary>
    private Task<ProbeResult> StartLocked(string url, CancellationToken callerToken)
    {
        // 只 Cancel 不 Dispose：作废后旧循环可能仍有一次 await 在收尾，
        // 此刻 Dispose 会让它撞上 ObjectDisposedException。CTS 数量与用户操作同阶，
        // 不持有定时器，交给 GC 即可。
        _runCts?.Cancel();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _runCts = cts;
        _inFlightUrl = url;
        _inFlight = RunAsync(url, cts.Token, callerToken);
        return _inFlight;
    }

    private async Task<ProbeResult> RunAsync(
        string url, CancellationToken runToken, CancellationToken callerToken)
    {
        var deadline = _time.GetUtcNow() + Budget;
        var attempt = 0;

        while (true)
        {
            var result = await ProbeOnceAsync(url, runToken, callerToken).ConfigureAwait(false);

            if (result.Outcome != ProbeOutcome.Unreachable)
                return result;

            // 每次"仍未就绪"只上报一次，序号严格递增（曾在两个位置各报一次，日志会重复）
            attempt++;
            Report(attempt, result);

            if (_time.GetUtcNow() >= deadline)
                return result;

            try
            {
                await Task.Delay(PollInterval, _time, runToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 调用方取消 → 如实抛出（与 ProbeOnceAsync 的取消语义保持一致）
                if (callerToken.IsCancellationRequested)
                    throw;

                // 否则是被新的一轮作废：返回最后一次结果，不给旧等待者抛异常
                return result;
            }
        }
    }

    /// <summary>
    /// 单次探测。**吞掉探测自身的异常并归为 Unreachable**：等待的语义是"等到能应答为止"，
    /// 中途一次探测抛异常（WebView 正在重建等）不该让整个等待崩掉，继续轮询即可。
    /// 但调用方的取消必须原样抛出。
    /// </summary>
    private async Task<ProbeResult> ProbeOnceAsync(
        string url, CancellationToken runToken, CancellationToken callerToken)
    {
        try
        {
            return await _probe.ProbeAsync(url, runToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (callerToken.IsCancellationRequested)
                throw;

            // 被新的一轮作废
            return new ProbeResult(ProbeOutcome.Unreachable, null, "等待已被新的一轮取代");
        }
        catch (Exception ex)
        {
            return new ProbeResult(ProbeOutcome.Unreachable, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 上报一次未就绪。**回调异常必须吞掉**：这个 Task 被所有共享者 await，
    /// 一个诊断回调抛出（例如 run.log 写盘失败）会连带炸掉全部等待者。
    /// </summary>
    private void Report(int attempt, ProbeResult result)
    {
        if (OnAttempt is null)
            return;

        try
        {
            OnAttempt(attempt, result);
        }
        catch
        {
            // 诊断信息丢了可以接受，等待本身不能因此失败
        }
    }
}