namespace DshDesktop.Domain;

public interface IServerLauncher
{
    Task<LaunchResult> LaunchAsync(AppConfig cfg, IProgress<string> output, CancellationToken ct);
}
