namespace DshDesktop.Domain;

public sealed class SessionStateMachine
{
    public SessionPhase Phase { get; private set; } = SessionPhase.Idle;

    public event EventHandler<SessionPhase>? PhaseChanged;

    private void TransitionTo(SessionPhase target, string reason)
    {
        if (!IsValidTransition(Phase, target))
            throw new InvalidOperationException(
                $"非法状态迁移: {Phase} -> {target} ({reason})");

        Phase = target;
        PhaseChanged?.Invoke(this, target);
    }

    public void NavigateDirectly()
    {
        if (Phase != SessionPhase.Idle)
            throw new InvalidOperationException($"NavigateDirectly 仅允许在 Idle 调用，当前: {Phase}");
        TransitionTo(SessionPhase.Ready, "S1 直接导航");
    }

    public void BeginProbe()
    {
        if (Phase != SessionPhase.Idle)
            throw new InvalidOperationException($"BeginProbe 仅允许在 Idle 调用，当前: {Phase}");
        TransitionTo(SessionPhase.Probing, "开始探测");
    }

    public void OnProbeResult(ProbeResult result)
    {
        if (Phase != SessionPhase.Probing)
            throw new InvalidOperationException($"OnProbeResult 仅允许在 Probing 调用，当前: {Phase}");

        var target = result.Outcome switch
        {
            ProbeOutcome.Ok => SessionPhase.Ready,
            ProbeOutcome.Unreachable => SessionPhase.Launching,
            ProbeOutcome.Other => SessionPhase.NeedsToken,
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
        TransitionTo(target, $"探测结果 {result.Outcome}");
    }

    public void BeginLaunch()
    {
        if (Phase is SessionPhase.Idle or SessionPhase.Probing)
            TransitionTo(SessionPhase.Launching, "开始启动管线");
        else
            throw new InvalidOperationException($"BeginLaunch 仅允许在 Idle/Probing 调用，当前: {Phase}");
    }

    public void OnUrlExtracted()
    {
        if (Phase != SessionPhase.Launching)
            throw new InvalidOperationException($"OnUrlExtracted 仅允许在 Launching 调用，当前: {Phase}");
        TransitionTo(SessionPhase.Ready, "抓到 URL");
    }

    public void OnFallbackProbeResult(ProbeResult result)
    {
        if (Phase != SessionPhase.Launching)
            throw new InvalidOperationException($"OnFallbackProbeResult 仅允许在 Launching 调用，当前: {Phase}");

        var target = result.Outcome == ProbeOutcome.Ok
            ? SessionPhase.Ready
            : SessionPhase.Failed;
        TransitionTo(target, $"回退探测 {result.Outcome}");
    }

    public void OnLaunchTimeout()
    {
        if (Phase != SessionPhase.Launching)
            throw new InvalidOperationException($"OnLaunchTimeout 仅允许在 Launching 调用，当前: {Phase}");
        TransitionTo(SessionPhase.Failed, "抓取超时");
    }

    public void OnProcessExitFailed()
    {
        if (Phase != SessionPhase.Launching)
            throw new InvalidOperationException($"OnProcessExitFailed 仅允许在 Launching 调用，当前: {Phase}");
        TransitionTo(SessionPhase.Failed, "进程退出非零");
    }

    public void OnTokenProvided()
    {
        if (Phase != SessionPhase.NeedsToken)
            throw new InvalidOperationException($"OnTokenProvided 仅允许在 NeedsToken 调用，当前: {Phase}");
        TransitionTo(SessionPhase.Ready, "用户输入临时 URL");
    }

    public void OnFailed()
    {
        if (Phase == SessionPhase.Failed)
            return;
        if (Phase is SessionPhase.Launching or SessionPhase.Probing)
            TransitionTo(SessionPhase.Failed, "失败");
        else
            throw new InvalidOperationException($"OnFailed 仅允许在 Probing/Launching 调用，当前: {Phase}");
    }

    public void Reset()
    {
        Phase = SessionPhase.Idle;
        PhaseChanged?.Invoke(this, SessionPhase.Idle);
    }

    private static bool IsValidTransition(SessionPhase from, SessionPhase to)
    {
        if (from == to)
            return true;

        return (from, to) switch
        {
            (SessionPhase.Idle, SessionPhase.Probing) => true,
            (SessionPhase.Idle, SessionPhase.Launching) => true,
            (SessionPhase.Idle, SessionPhase.Ready) => true,
            (SessionPhase.Probing, SessionPhase.Ready) => true,
            (SessionPhase.Probing, SessionPhase.Launching) => true,
            (SessionPhase.Probing, SessionPhase.NeedsToken) => true,
            (SessionPhase.Probing, SessionPhase.Failed) => true,
            (SessionPhase.Launching, SessionPhase.Ready) => true,
            (SessionPhase.Launching, SessionPhase.Failed) => true,
            (SessionPhase.NeedsToken, SessionPhase.Ready) => true,
            (SessionPhase.NeedsToken, SessionPhase.Failed) => true,
            (SessionPhase.Ready, SessionPhase.Idle) => true,
            (SessionPhase.Failed, SessionPhase.Idle) => true,
            _ => false,
        };
    }
}
