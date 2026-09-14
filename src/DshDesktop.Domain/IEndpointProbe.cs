namespace DshDesktop.Domain;

public interface IEndpointProbe
{
    Task<ProbeResult> ProbeAsync(string url, CancellationToken ct);
}
