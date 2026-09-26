namespace DshDesktop.Domain;

/// <summary>
/// 把一次探测导航的信号归类为 <see cref="ProbeOutcome"/>（设计文档 §4.2）。
/// 教训（2026-09-13）：端口没服务时 WebView2 的内置错误页自身以 HTTP 200 触发
/// WebResourceResponseReceived，只信状态码会把连接拒绝误判成在线并跳过启动管线。
/// 故导航失败时只有 4xx/5xx 可能是真实服务器响应，其余一律按连接失败处理。
/// </summary>
public static class ProbeClassifier
{
    /// <param name="navigationSucceeded">NavigationCompleted.IsSuccess：目标文档是否成功送达。</param>
    /// <param name="statusCode">导航期间捕获到的首个 HTTP 状态码；可信度取决于导航是否成功。</param>
    public static ProbeOutcome ClassifyNavigation(bool navigationSucceeded, int? statusCode)
    {
        if (!navigationSucceeded)
        {
            // 4xx/5xx：服务器活着但返回错误 → 按「其他一切情况」打开配置窗口（§4.2）。
            // 2xx 或无状态码：错误页/连接失败 → 进入启动管线。
            return statusCode is >= 400 and <= 599
                ? ProbeOutcome.Other
                : ProbeOutcome.Unreachable;
        }

        // 导航成功：默认文档已送达。没抓到状态码（事件时序极端情况）时视为在线。
        return statusCode is null or (>= 200 and <= 299)
            ? ProbeOutcome.Ok
            : ProbeOutcome.Other;
    }

    /// <summary>
    /// 把一次**网络层**存活检查（<c>HttpEndpointProbe</c>）的 HTTP 状态码归类。
    /// <para>
    /// 与 <see cref="ClassifyNavigation"/> 的区别：这里没有"错误页冒充 200"的问题
    /// —— 请求由 <c>HttpClient</c> 直接发出，拿到的就是服务器的真实应答，
    /// 所以不必做 4xx/5xx 的白名单。语义保持一致：
    /// </para>
    /// <list type="bullet">
    /// <item>2xx → <see cref="ProbeOutcome.Ok"/></item>
    /// <item>其余任何状态码（含 401/403）→ <see cref="ProbeOutcome.Other"/>：服务活着，只是要 token（§4.7）</item>
    /// <item><paramref name="statusCode"/> 为 null = 连接失败/超时 → <see cref="ProbeOutcome.Unreachable"/></item>
    /// </list>
    /// </summary>
    public static ProbeOutcome ClassifyHttpStatus(int? statusCode)
    {
        if (statusCode is null)
            return ProbeOutcome.Unreachable;

        return statusCode is >= 200 and <= 299
            ? ProbeOutcome.Ok
            : ProbeOutcome.Other;
    }
}
