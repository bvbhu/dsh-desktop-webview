using DshDesktop.Domain;
using FluentAssertions;

namespace DshDesktop.Domain.Tests;

/// <summary>
/// <see cref="ServiceReadinessWaiter"/> 的回归测试。
/// <para>
/// 这个类是 2026-09-24「页面反复刷新」修复的核心：它把"端口还没 LISTEN"收敛成
/// **一次**等待，让「导航失败重试」与「启动后确认」两条链共享同一轮轮询。
/// 因此这里重点钉住三件事：单次 in-flight 去重、退避轮询到就绪、预算耗尽如实返回失败。
/// </para>
/// <para>
/// 时间用真实 <see cref="TimeProvider.System"/> + 很小的间隔/预算（毫秒级），
/// 避免引入假时钟依赖；断言只看"探测被调了几次"和"最终结论"，不看墙钟时间。
/// </para>
/// </summary>
public class ServiceReadinessWaiterTests
{
    /// <summary>按脚本依次返回结果的假探针；脚本用尽后重复最后一个结果。</summary>
    private sealed class ScriptedProbe : IEndpointProbe
    {
        private readonly Queue<ProbeResult> _script;
        private ProbeResult _last;

        public ScriptedProbe(params ProbeResult[] script)
        {
            _script = new Queue<ProbeResult>(script);
            _last = script[^1];
        }

        public int Calls { get; private set; }

        public Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
        {
            Calls++;
            var result = _script.Count > 0 ? _script.Dequeue() : _last;
            _last = result;
            return Task.FromResult(result);
        }
    }

    /// <summary>结果可随时切换的探针，用于"先失败、之后变好"的跨轮场景。</summary>
    private sealed class SwitchableProbe : IEndpointProbe
    {
        public SwitchableProbe(ProbeResult initial) => Next = initial;

        public ProbeResult Next { get; set; }
        public int Calls { get; private set; }

        public Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Next);
        }
    }

    /// <summary>前 N 次抛异常，之后按脚本返回——用于验证"探测抛异常不炸掉整轮等待"。</summary>
    private sealed class FlakyProbe : IEndpointProbe
    {
        private readonly int _failures;
        private readonly ProbeResult _then;

        public FlakyProbe(int failures, ProbeResult then)
        {
            _failures = failures;
            _then = then;
        }

        public int Calls { get; private set; }

        public Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
        {
            Calls++;
            if (Calls <= _failures)
                throw new InvalidOperationException("WebView 正在重建");
            return Task.FromResult(_then);
        }
    }

    private static ServiceReadinessWaiter Waiter(IEndpointProbe probe, Action<int, ProbeResult>? onAttempt = null) =>
        new(probe)
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            Budget = TimeSpan.FromMilliseconds(400),
            OnAttempt = onAttempt,
        };

    private static ProbeResult Ok() => new(ProbeOutcome.Ok, 200, null);
    private static ProbeResult Other() => new(ProbeOutcome.Other, 401, null);
    private static ProbeResult Down() => new(ProbeOutcome.Unreachable, null, "refused");

    [Fact]
    public async Task AlreadyReachable_ReturnsOnFirstProbe_WithoutPolling()
    {
        var probe = new ScriptedProbe(Ok());

        var result = await Waiter(probe).WaitAsync("http://127.0.0.1:3080/");

        result.Outcome.Should().Be(ProbeOutcome.Ok);
        probe.Calls.Should().Be(1, "第一次就探到应答，不该再轮询");
    }

    [Fact]
    public async Task PortNotListeningYet_PollsUntilReachable()
    {
        // 2026-09-24 实机时序：URL 先打印，端口晚 1.5~2s 才 LISTEN。
        var probe = new ScriptedProbe(Down(), Down(), Ok());

        var result = await Waiter(probe).WaitAsync("http://127.0.0.1:3080/");

        result.Outcome.Should().Be(ProbeOutcome.Ok);
        probe.Calls.Should().Be(3);
    }

    [Fact]
    public async Task NonOkButReachable_CountsAsReady()
    {
        // 401 = 服务活着、只是要 token（§4.7）。等待必须就此结束，不能一直等到预算耗尽。
        var probe = new ScriptedProbe(Other());

        var result = await Waiter(probe).WaitAsync("http://127.0.0.1:3080/");

        result.Outcome.Should().Be(ProbeOutcome.Other);
        probe.Calls.Should().Be(1);
    }

    [Fact]
    public async Task BudgetExhausted_ReturnsUnreachable_RatherThanHanging()
    {
        var probe = new ScriptedProbe(Down());

        var result = await Waiter(probe).WaitAsync("http://127.0.0.1:3080/");

        result.Outcome.Should().Be(ProbeOutcome.Unreachable);
        probe.Calls.Should().BeGreaterThan(1, "预算内应至少重试过一次");
    }

    [Fact]
    public async Task ConcurrentCallers_SameUrl_ShareOnePollingRun()
    {
        // 这是"反复刷新"的根治点：导航重试与启动后确认同时要就绪时，
        // 只能有一轮轮询在跑，否则探测次数翻倍。
        var gate = new TaskCompletionSource<ProbeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new GatedProbe(gate.Task);
        var waiter = Waiter(probe);

        var first = waiter.WaitAsync("http://127.0.0.1:3080/");
        var second = waiter.WaitAsync("http://127.0.0.1:3080/");
        var third = waiter.WaitAsync("http://127.0.0.1:3080/");

        gate.SetResult(Ok());
        var results = await Task.WhenAll(first, second, third);

        results.Should().AllSatisfy(r => r.Outcome.Should().Be(ProbeOutcome.Ok));
        probe.Calls.Should().Be(1, "同一 URL 的并发等待必须复用同一轮轮询");
    }

    [Fact]
    public async Task DifferentUrl_StartsAFreshWait()
    {
        // 用户提交了新的临时 URL：旧等待的结论不能套用到新目标上。
        var probe = new SwitchableProbe(Down());
        var waiter = Waiter(probe);

        var first = await waiter.WaitAsync("http://127.0.0.1:3080/");
        first.Outcome.Should().Be(ProbeOutcome.Unreachable);

        probe.Next = Ok();
        var second = await waiter.WaitAsync("http://127.0.0.1:9999/");

        second.Outcome.Should().Be(ProbeOutcome.Ok);
    }

    [Fact]
    public async Task RestartAsync_AlwaysStartsANewRun_EvenIfOneIsInFlight()
    {
        // 「启动服务」是明确的用户动作，必须真的重新检查一次。
        var probe = new ScriptedProbe(Ok());
        var waiter = Waiter(probe);

        await waiter.WaitAsync("http://127.0.0.1:3080/");
        var restarted = await waiter.RestartAsync("http://127.0.0.1:3080/");

        restarted.Outcome.Should().Be(ProbeOutcome.Ok);
        probe.Calls.Should().Be(2, "RestartAsync 不复用已完成的结果");
    }

    [Fact]
    public async Task ProbeThrowing_IsTreatedAsUnreachable_AndPollingContinues()
    {
        // WebView 重建期间探测会抛异常；等待不该因此崩掉，应继续轮询到就绪。
        var probe = new FlakyProbe(failures: 2, then: Ok());

        var result = await Waiter(probe).WaitAsync("http://127.0.0.1:3080/");

        result.Outcome.Should().Be(ProbeOutcome.Ok);
        probe.Calls.Should().Be(3);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var probe = new ScriptedProbe(Down());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await Waiter(probe).WaitAsync("http://127.0.0.1:3080/", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task OnAttempt_ReportsUnreachableAttempts_ForDiagnostics()
    {
        var attempts = new List<int>();
        var probe = new ScriptedProbe(Down(), Down(), Ok());

        await Waiter(probe, (n, _) => attempts.Add(n)).WaitAsync("http://127.0.0.1:3080/");

        attempts.Should().NotBeEmpty("未就绪的轮询要能被记进 run.log");
        attempts.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task OnAttempt_ReportsEachUnreachableAttemptExactlyOnce()
    {
        // 回归：曾在一轮里上报两次同一个 attempt（携带完全相同的 ProbeResult），
        // run.log 会出现重复行。序号必须严格递增且不重复。
        var attempts = new List<int>();
        var probe = new ScriptedProbe(Down(), Down(), Ok());

        await Waiter(probe, (n, _) => attempts.Add(n)).WaitAsync("http://127.0.0.1:3080/");

        attempts.Should().Equal(new[] { 1, 2 });
    }

    [Fact]
    public async Task RestartAsync_SupersedesOldRun_SoOnlyOneLoopPollsAtATime()
    {
        // 回归 4c：作废旧循环，避免同一时刻两轮轮询并行。
        // 旧等待者拿到"最后一次结果"而不是异常。
        var gate = new TaskCompletionSource<ProbeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new GatedProbe(gate.Task);
        var waiter = Waiter(probe);

        var superseded = waiter.WaitAsync("http://127.0.0.1:3080/");
        var restarted = waiter.RestartAsync("http://127.0.0.1:3080/");

        // 让第一轮收到一个"未就绪"，随后被作废
        gate.SetResult(Down());

        var supersededResult = await superseded;
        var restartedResult = await restarted;

        // 被作废的那一轮如实返回失败结果，而不是抛 OperationCanceledException
        supersededResult.Outcome.Should().Be(ProbeOutcome.Unreachable);
        restartedResult.Outcome.Should().Be(ProbeOutcome.Unreachable);
    }

    [Fact]
    public async Task WaitAsync_ReusedRun_StillHonoursEachCallersCancellation()
    {
        // 回归 4d：复用共享轮询时，后来者的 ct 不能被静默吞掉。
        var gate = new TaskCompletionSource<ProbeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new GatedProbe(gate.Task);
        var waiter = Waiter(probe);

        // 第一轮由"不取消"的调用者发起并挂住
        var shared = waiter.WaitAsync("http://127.0.0.1:3080/");

        using var cts = new CancellationTokenSource();
        var late = waiter.WaitAsync("http://127.0.0.1:3080/", cts.Token);
        cts.Cancel();

        var act = async () => await late;
        await act.Should().ThrowAsync<OperationCanceledException>();

        // 共享轮询本身不受影响，仍能正常收尾
        gate.SetResult(Ok());
        (await shared).Outcome.Should().Be(ProbeOutcome.Ok);
        probe.Calls.Should().Be(1, "后来者取消不该让共享轮询重跑");
    }

    [Fact]
    public async Task OnAttempt_Throwing_DoesNotFaultTheSharedWait()
    {
        // 回归 4h：诊断回调抛异常不能炸掉被所有共享者 await 的那个 Task。
        var probe = new ScriptedProbe(Down(), Ok());
        var waiter = Waiter(probe, (_, _) => throw new IOException("run.log 写盘失败"));

        var result = await waiter.WaitAsync("http://127.0.0.1:3080/");

        result.Outcome.Should().Be(ProbeOutcome.Ok);
    }

    /// <summary>第一次探测一直挂着，直到测试放行——用来稳定复现"并发等待"场景。</summary>
    private sealed class GatedProbe : IEndpointProbe
    {
        private readonly Task<ProbeResult> _gate;

        public GatedProbe(Task<ProbeResult> gate) => _gate = gate;

        public int Calls { get; private set; }

        public async Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
        {
            Calls++;
            return await _gate;
        }
    }
}
