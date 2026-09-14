namespace DshDesktop.Domain;

public sealed record ProbeResult(ProbeOutcome Outcome, int? StatusCode, string? Detail);
