using System.Net.Http;
using DshDesktop.Domain;

namespace DshDesktop.Infrastructure;

/// <summary>
/// 服务存活检查：直接用 <see cref="HttpClient"/> 访问目标 URL，**完全不碰 WebView**。
/// <para>
/// 为什么需要它（2026-09-24 用户报告"反复刷新页面"）：原来的存活检查复用
/// <see cref="WebViewProbe"/>，而后者靠 <c>CoreWebView2.Navigate</c> 实现 ——
/// 每一次"服务还在不在"的检查都会把主 WebView 里用户正在看的页面导航走一次。
/// 这些检查散落在「会话启动后刷新」「设置窗口打开」「启动/重启后确认」「停止后确认」
/// 四处，叠加起来就是用户看到的页面反复刷新。
/// </para>
/// <para>
/// 存活检查本来也不需要 WebView：它只回答"端口上有没有实例在听"。
/// 至于「S2 启动探测」，那个必须继续走 WebView —— 它要在**页面显示之前**
/// 判断"能不能直接打开页面"，需要与真实导航共用同一份 cookie / 会话（§6）。
/// 两者职责不同，不要混用。
/// </para>
/// <para>
/// 分类交给 <see cref="ProbeClassifier.ClassifyHttpStatus"/>（Domain 层，可单测）：
/// 2xx → Ok；401/403 等 → Other（服务活着，只是要 token）；连不上 → Unreachable。
/// </para>
/// </summary>
public sealed class HttpEndpointProbe : IEndpointProbe, IDisposable
{
    /// <summary>单次请求超时。存活检查要快，不能让设置窗口的状态行卡住。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public HttpEndpointProbe(HttpClient? http = null)
    {
        _ownsHttpClient = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            // 存活检查必须立刻反映"端口现在有没有在听"，任何缓存/连接复用都会让它说谎。
            // 尤其：刚被停止的服务如果走了连接池复用，会短暂报"还在跑"。
            // （TimeSpan.Zero 是合法值：只有负值或 InfiniteTimeSpan 才抛。）
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = RequestTimeout,
        })
        {
            Timeout = RequestTimeout,
        };
    }

    public async Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // ResponseHeadersRead：只要响应头就够（我们只关心状态码），
            // 不要把整个页面体读下来 —— 存活检查不该有流量代价。
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;
            return new ProbeResult(ProbeClassifier.ClassifyHttpStatus(status), status, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方取消：如实抛出，交由上层按取消处理（不要伪装成"服务没起来"）。
            throw;
        }
        catch (Exception ex)
        {
            // 连接被拒 / DNS 失败 / 超时 / URL 非法 …… 一律是"现在探不到"。
            // detail 只记异常类型，避免把完整异常文本（可能含 URL 与 token）落进日志。
            return new ProbeResult(ProbeOutcome.Unreachable, null, ex.GetType().Name);
        }
    }

    /// <summary>
    /// 只释放**自己创建**的 <see cref="HttpClient"/>。外部注入的实例归调用方所有 ——
    /// 若将来有人传入共享 HttpClient，这里的 Dispose 会毒掉别人的请求。
    /// </summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}