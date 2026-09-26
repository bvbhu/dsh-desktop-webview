namespace DshDesktop.Domain;

/// <summary>把 URL 交给外部浏览器的能力。实现放 Infrastructure（Domain 层不许依赖 Windows / 进程 API）。</summary>
public interface IExternalBrowser
{
    /// <summary>
    /// 打开 URL。<b>本方法不抛异常</b>：所有失败都收敛成返回值，
    /// 因为调用点在 WebView2 的事件回调里，异常会一路冒到 Dispatcher。
    /// </summary>
    /// <param name="url">要打开的 URL（调用方已确保是 http/https）。</param>
    /// <returns>成功为 null；失败为给控制台/日志看的原因文本。</returns>
    string? Open(string url);
}
