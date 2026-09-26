using System.Diagnostics;
using System.IO;
using DshDesktop.Domain;

namespace DshDesktop.Infrastructure;

/// <summary>
/// 把 URL 交给外部浏览器。
/// <para>
/// <b>与 <see cref="CommandLauncher"/> 是两条完全不同的路</b>：服务进程用
/// <c>UseShellExecute=false</c> + Job Object 管整棵进程树；浏览器恰恰相反 ——
/// 它必须用 <c>UseShellExecute=true</c>（走 URL 关联才能找到系统默认浏览器），
/// 也<b>不</b>进我们的 Job Object，所以关掉外壳不会连带关掉浏览器（这是期望行为）。
/// </para>
/// <para>
/// <b>绝不用 <c>cmd /c start</c></b>：URL 里的 <c>&amp;</c> 会被 cmd 当命令分隔符，
/// 还会引入引号/注入问题。<c>ArgumentList</c> 把 URL 作为单个 argv 元素原样传递，无 shell。
/// </para>
/// </summary>
public sealed class ShellExternalBrowser : IExternalBrowser
{
    private readonly Func<string> _configuredPath;

    /// <summary>
    /// </summary>
    /// <param name="configuredPath">
    /// 取"浏览器位置"配置值的委托。<b>用委托而不是字符串</b>：设置窗口保存后
    /// <c>_config</c> 会就地换掉，构造时快照的字符串会一直用旧值。
    /// </param>
    public ShellExternalBrowser(Func<string> configuredPath)
    {
        _configuredPath = configuredPath;
    }

    /// <inheritdoc />
    public string? Open(string url)
    {
        var plan = BrowserLaunchPlan.Resolve(_configuredPath(), File.Exists);

        var reason = plan.Kind == BrowserLaunchKind.Executable
            ? null
            : plan.FallbackReason;

        try
        {
            if (plan.Kind == BrowserLaunchKind.Executable)
            {
                // UseShellExecute=false：直接起那个 exe，URL 作为唯一参数。
                // 不用 Arguments 字符串拼引号 —— URL 里带 ? / & / # 时拼错就是打不开或参数被截断。
                var psi = new ProcessStartInfo
                {
                    FileName = plan.ExecutablePath!,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add(url);
                using var process = Process.Start(psi);
                return reason;
            }

            // 系统默认浏览器：ShellExecute 走 http/https 的 URL 关联。
            var fallbackPsi = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            };
            using var started = Process.Start(fallbackPsi);
            return reason;
        }
        catch (Exception ex)
        {
            // 本方法契约是"不抛异常"：调用点在 WebView2 事件回调里。
            var detail = $"{ex.GetType().Name}: {ex.Message}";
            return reason is null ? detail : $"{reason}；启动也失败：{detail}";
        }
    }
}
