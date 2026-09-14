using DshDesktop.Domain;
using FluentAssertions;

namespace DshDesktop.Domain.Tests;

public class ServiceStrategyResolverTests
{
    [Fact]
    public void NeverStart_AlwaysNavigate()
    {
        ServiceStrategyResolver.Resolve(ServiceStrategy.NeverStart, null)
            .Should().Be(ServiceAction.Navigate);
    }

    [Fact]
    public void AlwaysStart_AlwaysLaunch()
    {
        ServiceStrategyResolver.Resolve(ServiceStrategy.AlwaysStart, null)
            .Should().Be(ServiceAction.Launch);
    }

    [Fact]
    public void ProbeThenStart_Ok_Navigate()
    {
        var pr = new ProbeResult(ProbeOutcome.Ok, 200, null);
        ServiceStrategyResolver.Resolve(ServiceStrategy.ProbeThenStart, pr)
            .Should().Be(ServiceAction.Navigate);
    }

    [Fact]
    public void ProbeThenStart_Unreachable_Launch()
    {
        var pr = new ProbeResult(ProbeOutcome.Unreachable, null, "refused");
        ServiceStrategyResolver.Resolve(ServiceStrategy.ProbeThenStart, pr)
            .Should().Be(ServiceAction.Launch);
    }

    [Fact]
    public void ProbeThenStart_Other_OpenConfigForToken()
    {
        var pr = new ProbeResult(ProbeOutcome.Other, 401, "unauthorized");
        ServiceStrategyResolver.Resolve(ServiceStrategy.ProbeThenStart, pr)
            .Should().Be(ServiceAction.OpenConfigForToken);
    }

    [Fact]
    public void ProbeThenStart_NullProbeResult_Throws()
    {
        var act = () => ServiceStrategyResolver.Resolve(ServiceStrategy.ProbeThenStart, null);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(301)]
    [InlineData(403)]
    [InlineData(500)]
    public void ProbeThenStart_Non200NonUnreachable_Other(int statusCode)
    {
        var pr = new ProbeResult(ProbeOutcome.Other, statusCode, null);
        ServiceStrategyResolver.Resolve(ServiceStrategy.ProbeThenStart, pr)
            .Should().Be(ServiceAction.OpenConfigForToken);
    }
}
