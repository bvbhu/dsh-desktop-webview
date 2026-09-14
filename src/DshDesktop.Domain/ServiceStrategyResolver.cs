namespace DshDesktop.Domain;

public static class ServiceStrategyResolver
{
    public static ServiceAction Resolve(ServiceStrategy strategy, ProbeResult? probeResult)
    {
        return strategy switch
        {
            ServiceStrategy.NeverStart => ServiceAction.Navigate,
            ServiceStrategy.AlwaysStart => ServiceAction.Launch,
            ServiceStrategy.ProbeThenStart => probeResult switch
            {
                null => throw new ArgumentNullException(nameof(probeResult),
                    "S2 策略需要探测结果"),
                { Outcome: ProbeOutcome.Ok } => ServiceAction.Navigate,
                { Outcome: ProbeOutcome.Unreachable } => ServiceAction.Launch,
                { Outcome: ProbeOutcome.Other } => ServiceAction.OpenConfigForToken,
                _ => throw new InvalidOperationException(
                    $"未知探测结果: {probeResult.Outcome}")
            },
            _ => throw new ArgumentOutOfRangeException(nameof(strategy))
        };
    }
}
