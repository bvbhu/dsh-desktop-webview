using DshDesktop.Domain;
using FluentAssertions;

namespace DshDesktop.Domain.Tests;

public class SessionStateMachineTests
{
    [Fact]
    public void InitialPhase_IsIdle()
    {
        var sm = new SessionStateMachine();
        sm.Phase.Should().Be(SessionPhase.Idle);
    }

    [Fact]
    public void BeginProbe_Idle_To_Probing()
    {
        var sm = new SessionStateMachine();
        sm.BeginProbe();
        sm.Phase.Should().Be(SessionPhase.Probing);
    }

    [Fact]
    public void OnProbeResult_Ok_To_Ready()
    {
        var sm = new SessionStateMachine();
        sm.BeginProbe();
        sm.OnProbeResult(new ProbeResult(ProbeOutcome.Ok, 200, null));
        sm.Phase.Should().Be(SessionPhase.Ready);
    }

    [Fact]
    public void OnProbeResult_Unreachable_To_Launching()
    {
        var sm = new SessionStateMachine();
        sm.BeginProbe();
        sm.OnProbeResult(new ProbeResult(ProbeOutcome.Unreachable, null, "refused"));
        sm.Phase.Should().Be(SessionPhase.Launching);
    }

    [Fact]
    public void OnProbeResult_Other_To_NeedsToken()
    {
        var sm = new SessionStateMachine();
        sm.BeginProbe();
        sm.OnProbeResult(new ProbeResult(ProbeOutcome.Other, 401, "unauthorized"));
        sm.Phase.Should().Be(SessionPhase.NeedsToken);
    }

    [Fact]
    public void NavigateDirectly_Idle_To_Ready()
    {
        var sm = new SessionStateMachine();
        sm.NavigateDirectly();
        sm.Phase.Should().Be(SessionPhase.Ready);
    }

    [Fact]
    public void BeginLaunch_Idle_To_Launching()
    {
        var sm = new SessionStateMachine();
        sm.BeginLaunch();
        sm.Phase.Should().Be(SessionPhase.Launching);
    }

    [Fact]
    public void OnUrlExtracted_Launching_To_Ready()
    {
        var sm = new SessionStateMachine();
        sm.BeginLaunch();
        sm.OnUrlExtracted();
        sm.Phase.Should().Be(SessionPhase.Ready);
    }

    [Fact]
    public void OnFallbackProbeResult_Ok_To_Ready()
    {
        var sm = new SessionStateMachine();
        sm.BeginLaunch();
        sm.OnFallbackProbeResult(new ProbeResult(ProbeOutcome.Ok, 200, null));
        sm.Phase.Should().Be(SessionPhase.Ready);
    }

    [Fact]
    public void OnFallbackProbeResult_Other_To_Failed()
    {
        var sm = new SessionStateMachine();
        sm.BeginLaunch();
        sm.OnFallbackProbeResult(new ProbeResult(ProbeOutcome.Other, 500, "err"));
        sm.Phase.Should().Be(SessionPhase.Failed);
    }

    [Fact]
    public void OnLaunchTimeout_Launching_To_Failed()
    {
        var sm = new SessionStateMachine();
        sm.BeginLaunch();
        sm.OnLaunchTimeout();
        sm.Phase.Should().Be(SessionPhase.Failed);
    }

    [Fact]
    public void OnProcessExitFailed_Launching_To_Failed()
    {
        var sm = new SessionStateMachine();
        sm.BeginLaunch();
        sm.OnProcessExitFailed();
        sm.Phase.Should().Be(SessionPhase.Failed);
    }

    [Fact]
    public void OnTokenProvided_NeedsToken_To_Ready()
    {
        var sm = new SessionStateMachine();
        sm.BeginProbe();
        sm.OnProbeResult(new ProbeResult(ProbeOutcome.Other, 403, null));
        sm.Phase.Should().Be(SessionPhase.NeedsToken);
        sm.OnTokenProvided();
        sm.Phase.Should().Be(SessionPhase.Ready);
    }

    [Fact]
    public void Reset_ReturnsToIdle_FromAnyPhase()
    {
        var sm = new SessionStateMachine();
        sm.BeginLaunch();
        sm.OnLaunchTimeout();
        sm.Phase.Should().Be(SessionPhase.Failed);
        sm.Reset();
        sm.Phase.Should().Be(SessionPhase.Idle);
    }

    [Fact]
    public void PhaseChanged_Raises_OnTransition()
    {
        var sm = new SessionStateMachine();
        var phases = new List<SessionPhase>();
        sm.PhaseChanged += (_, p) => phases.Add(p);

        sm.BeginProbe();
        sm.OnProbeResult(new ProbeResult(ProbeOutcome.Unreachable, null, null));

        phases.Should().Equal(SessionPhase.Probing, SessionPhase.Launching);
    }

    [Fact]
    public void InvalidTransition_BeginProbe_InLaunching_Throws()
    {
        var sm = new SessionStateMachine();
        sm.BeginLaunch();
        var act = () => sm.BeginProbe();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void InvalidTransition_OnUrlExtracted_InIdle_Throws()
    {
        var sm = new SessionStateMachine();
        var act = () => sm.OnUrlExtracted();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void InvalidTransition_OnTokenProvided_InIdle_Throws()
    {
        var sm = new SessionStateMachine();
        var act = () => sm.OnTokenProvided();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FullS2Flow_Idle_Probing_Launching_Ready()
    {
        var sm = new SessionStateMachine();
        sm.BeginProbe();
        sm.Phase.Should().Be(SessionPhase.Probing);
        sm.OnProbeResult(new ProbeResult(ProbeOutcome.Unreachable, null, null));
        sm.Phase.Should().Be(SessionPhase.Launching);
        sm.OnUrlExtracted();
        sm.Phase.Should().Be(SessionPhase.Ready);
    }

    [Fact]
    public void FullS2FallbackFlow_To_Ready()
    {
        var sm = new SessionStateMachine();
        sm.BeginProbe();
        sm.OnProbeResult(new ProbeResult(ProbeOutcome.Unreachable, null, null));
        sm.OnFallbackProbeResult(new ProbeResult(ProbeOutcome.Ok, 200, null));
        sm.Phase.Should().Be(SessionPhase.Ready);
    }
}
