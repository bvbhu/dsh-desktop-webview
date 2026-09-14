using System.IO;
using System.Text;

namespace DshDesktop.Infrastructure;

public sealed class RunLog
{
    private readonly string _path;
    private readonly object _lock = new();

    public RunLog(string path) => _path = path;

    public void Append(string message)
    {
        try
        {
            lock (_lock)
                File.AppendAllText(_path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}",
                    Encoding.UTF8);
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
