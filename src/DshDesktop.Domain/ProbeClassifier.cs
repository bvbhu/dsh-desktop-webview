namespace DshDesktop.Domain;

/// <summary>
/// 把"一次探测导航"的信号归类为 <see cref="ProbeOutcome"/>（设计文档 §4.2）。
///
/// 实测教训（2026-09-13 回归）：目标端口没有服务时，WebView2 渲染内置错误页，
/// 该错误页自身会以 HTTP 200 触发 WebResourceResponseReceived。若"先抓到状态码就信
/// 状态码"，连接拒绝会被误判为服务在线（Ok），跳过启动管线，用户停在"拒绝连接"页。
/// 因此导航失败时，只有 4xx/5xx 才可能是真实服务器响应（错误页不会以 4xx/5xx 送达），
/// 2xx / 无状态码 一律按连接失败处理 → 进入启动管线。
/// </summary>
public static class ProbeClassifier
{
    /// <param name="navigationSucceeded">NavigationCompleted.IsSuccess：目标文档是否成功送达。</param>
    /// <param name="statusCode">导航期间捕获到的首个 HTTP 状态码；可信度取决于导航是否成功。</param>
    public static ProbeOutcome ClassifyNavigation(bool navigationSucceeded, int? statusCode)
    {
        if (!navigationSucceeded)
        {
            // 收到 4xx/5xx：服务器活着但返回错误 → 按"其他一切情况"打开配置窗口（§4.2）。
            // 2xx 或无状态码：错误页/连接失败，服务未响应 → 进入启动管线。
            return statusCode is >= 400 and <= 599
                ? ProbeOutcome.Other
                : ProbeOutcome.Unreachable;
        }

        // 导航成功：默认文档已送达。没抓到状态码（事件时序极端情况）时视为在线。
        return statusCode is null or (>= 200 and <= 299)
            ? ProbeOutcome.Ok
            : ProbeOutcome.Other;
    }
}
