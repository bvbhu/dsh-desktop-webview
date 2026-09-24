using System.Diagnostics;
using System.IO;
using System.Text;
using DshDesktop.Domain;

namespace DshDesktop.Infrastructure;

public sealed class CommandLauncher : IServerLauncher, IDisposable
{
    private const int CaptureLimit = 10_000;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private JobObjectSupervisor? _job;
    private Process? _process;
    private bool _disposed;

    /// <summary>
    /// 停止当前正在运行的服务进程。设置窗口的「关闭服务」按钮走这里 —— 关窗时的清场
    /// 依赖 Job Object 的 <c>KILL_ON_JOB_CLOSE</c>，但用户在应用还开着的时候就想把服务停掉，
    /// 不能等到关窗才生效。
    /// <para>
    /// 结束 <c>cmd.exe /c</c> 壳还不够：它常常已经把服务本体 detach 出去（服务自己 fork、
    /// 壳直接退出），所以必须同时关掉 Job Object（它会连带结束整棵被它收养的子进程树）。
    /// 已经退出/没有拉起过任何服务时是安全的空操作。
    /// </para>
    /// </summary>
    public void Kill()
    {
        // 先杀 cmd 壳（若还活着），再关 Job Object 收割残余的整棵子进程树。
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能在 Kill 前一刻自己退出，或没有权限 —— 都交给 Job Object 兜底。
        }

        try
        {
            _job?.Dispose();
        }
        catch
        {
            // 重复 Dispose / 句柄已关：忽略，服务可能本来就没被拉起过。
        }

        _job = null;
        _process?.Dispose();
        _process = null;
    }

    public async Task<LaunchResult> LaunchAsync(
        AppConfig cfg,
        IProgress<string> output,
        CancellationToken ct)
    {
        // 上一次「关闭服务」可能已经把 Job Object 关掉了；再启动必须给一个全新的，
        // 否则 Assign 会把进程挂到一个已经关掉的句柄上（静默失败，关窗时就清不了场）。
        _job ??= new JobObjectSupervisor();

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + cfg.LaunchCommand,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // 必须显式指定 UTF-8：重定向流不设编码时，.NET 按控制台 OEM 代码页
            // （本机 936/GBK）解码，而子命令（dsh / node）吐的是 UTF-8 字节 ——
            // 控制台里就成了「鑷姩鎭㈠鑰冭瘯」。dsh 是 UTF-8 输出，这里不能跟随
            // Console.OutputEncoding（那是宿主自己的控制台，与子进程无关）。
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrWhiteSpace(cfg.WorkingDirectory))
            psi.WorkingDirectory = cfg.WorkingDirectory;

        // 把临时目录定向到程序目录内的 temp\：便携承诺要求这些子进程产生的临时文件
        // 不落到 %TEMP%。**只注入到我们主动拉起的进程**，而不是改本进程的 TMP/TEMP ——
        // 后者会连带影响 WebView2 的浏览器进程树和用户在页面里跑的一切（原因见
        // DataPaths.TempEnvironmentVariableNames 的注释）。
        // 建不出来就跳过：宁可临时文件落到系统 %TEMP%，也不要因此启动不了服务。
        try
        {
            Directory.CreateDirectory(DataPaths.TempDir);
            foreach (var name in DataPaths.TempEnvironmentVariableNames)
            {
                psi.Environment[name] = DataPaths.TempDir;
            }
        }
        catch
        {
            // 忽略：维持系统默认
        }

        var extractor = new UrlExtractor(cfg.UrlExtractRegex, cfg.SuccessMarkerRegex);
        var captured = new StringBuilder(CaptureLimit);
        string? extractedUrl = null;
        bool hitMarker = false;
        bool done = false;
        var gate = new object();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int streamsEnded = 0;

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        void HandleLine(string? line)
        {
            if (line is null)
            {
                if (Interlocked.Increment(ref streamsEnded) >= 2)
                    tcs.TrySetResult(false);
                return;
            }

            // 控制序列必须在这里剥掉，且必须在匹配之前：默认 URL 正则的 \S+ 会把
            // 结尾的 ANSI 复位序列一起吞进捕获组，导航过去就是个 404（见 AnsiEscape 注释）。
            line = AnsiEscape.Strip(line);

            output.Report(line);

            lock (gate)
            {
                if (done)
                    return;

                if (captured.Length < CaptureLimit)
                    captured.AppendLine(line);

                if (extractedUrl is null)
                {
                    var url = extractor.TryExtract(line);
                    if (url is not null)
                    {
                        extractedUrl = url;
                        done = true;
                        tcs.TrySetResult(true);
                        return;
                    }
                }

                if (!hitMarker && extractor.IsSuccessMarker(line))
                {
                    hitMarker = true;
                    done = true;
                    tcs.TrySetResult(true);
                }
            }
        }

        _process.OutputDataReceived += (_, e) => HandleLine(e.Data);
        _process.ErrorDataReceived += (_, e) => HandleLine(e.Data);

        try
        {
            _process.Start();
        }
        catch (Exception ex)
        {
            captured.AppendLine($"[启动失败: {ex.Message}]");
            return new LaunchResult(null, false, captured.ToString());
        }

        _job?.Assign(_process);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        try
        {
            await tcs.Task.WaitAsync(DefaultTimeout, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
        }

        // 退出码必须回传，而不是只拼进捕获文本：调用方要靠它区分"命令秒退"与"服务启动慢"。
        // 进程未退出时给 null（= 仍在跑，属正常超时收尾），别写成 0 —— 那会把
        // "进程还活着"误报成"进程正常退出"。
        int? exitCode = null;
        if (_process.HasExited)
        {
            exitCode = _process.ExitCode;
            if (exitCode != 0)
                captured.AppendLine($"[进程退出码: {exitCode}]");
        }

        return new LaunchResult(extractedUrl, hitMarker, captured.ToString(), exitCode);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _job?.Dispose();
        _process?.Dispose();
    }
}
