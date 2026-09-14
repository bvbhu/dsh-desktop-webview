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

    public async Task<LaunchResult> LaunchAsync(
        AppConfig cfg,
        IProgress<string> output,
        CancellationToken ct)
    {
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
        _job = new JobObjectSupervisor();

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

        _job.Assign(_process);
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
