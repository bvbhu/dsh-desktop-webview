using DshDesktop.Domain;
using Microsoft.Web.WebView2.Core;

namespace DshDesktop.Infrastructure;

public sealed class WebViewProbe : IEndpointProbe
{
    /// <summary>单次探测导航的内部超时；超时按连接失败处理。</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private CoreWebView2? _coreWebView2;

    public void SetWebView(CoreWebView2 coreWebView2)
    {
        _coreWebView2 = coreWebView2;
    }

    public async Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
    {
        if (_coreWebView2 is null)
            throw new InvalidOperationException("WebView 未初始化，无法探测");

        var tcs = new TaskCompletionSource<ProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int? statusCode = null;

        void OnResponseReceived(object? s, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            if (statusCode.HasValue)
                return;

            // 只接受"请求 URI == 探测目标"的响应。错误页上的其他请求（favicon、内嵌资源，
            // 甚至错误页本身的其他子帧）也会触发本事件并带 200，不做过滤就会把
            // "连接拒绝"污染成"服务在线"（2026-09-13 实测回归）。
            // 匹配失败时降级为 statusCode=null，分类器对 null 有安全语义，不会误判为 Ok。
            var requestUri = e.Request?.Uri;
            if (!SameTarget(requestUri, url))
                return;

            if (e.Response?.StatusCode > 0)
                statusCode = e.Response.StatusCode;
        }

        void OnNavCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            // 分类只看两个信号：导航是否成功 + 主文档状态码。
            // 不再枚举 WebErrorStatus——实测连接拒绝被报成 Unknown，白名单方式脆弱。
            var outcome = ProbeClassifier.ClassifyNavigation(e.IsSuccess, statusCode);
            var detail = e.IsSuccess ? null : e.WebErrorStatus.ToString();
            tcs.TrySetResult(new ProbeResult(outcome, statusCode, detail));
        }

        _coreWebView2.WebResourceResponseReceived += OnResponseReceived;
        _coreWebView2.NavigationCompleted += OnNavCompleted;

        try
        {
            _coreWebView2.Navigate(url);
            // 内部超时兜底：NavigationCompleted 极端情况下可能永不触发。
            // 超时按「连接失败」分流进入启动管线（§4.2：连接失败/超时 → Launching），
            // 与外部取消区分开。
            using var timeout = new CancellationTokenSource(ProbeTimeout);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            using var regT = timeout.Token.Register(() => tcs.TrySetCanceled(timeout.Token));
            try
            {
                return await tcs.Task;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new ProbeResult(ProbeOutcome.Unreachable, statusCode, "探测超时");
            }
        }
        finally
        {
            _coreWebView2.WebResourceResponseReceived -= OnResponseReceived;
            _coreWebView2.NavigationCompleted -= OnNavCompleted;
        }
    }

    private static bool SameTarget(string? candidate, string targetUrl)
    {
        if (string.IsNullOrEmpty(candidate))
            return false;
        if (string.Equals(candidate, targetUrl, StringComparison.OrdinalIgnoreCase))
            return true;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var a)
            && Uri.TryCreate(targetUrl, UriKind.Absolute, out var b)
            && string.Equals(a.GetLeftPart(UriPartial.Path), b.GetLeftPart(UriPartial.Path),
                StringComparison.OrdinalIgnoreCase);
    }
}
