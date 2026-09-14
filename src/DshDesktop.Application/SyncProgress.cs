namespace DshDesktop.Application;

internal sealed class SyncProgress : IProgress<string>
{
    private readonly Action<string> _callback;

    public SyncProgress(Action<string> callback) => _callback = callback;

    public void Report(string value) => _callback(value);
}
