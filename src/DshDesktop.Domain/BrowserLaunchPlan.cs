namespace DshDesktop.Domain;

/// <summary>一次"交给外部浏览器"的启动方式。</summary>
public enum BrowserLaunchKind
{
    /// <summary>用系统默认浏览器（ShellExecute 走 URL 关联）。</summary>
    SystemDefault,

    /// <summary>用配置指定的可执行文件打开（URL 作为唯一参数）。</summary>
    Executable,
}

/// <summary>
/// 外部浏览器启动计划：由 <see cref="Resolve"/> 从配置值算出，
/// 交给 Infrastructure 层的 <see cref="IExternalBrowser"/> 执行。
/// <para>
/// 拆成"计划 + 执行"是为了让回落分支可单测：App 层没有测试工程，
/// 而"路径非法 → 回落系统默认浏览器"这条必须能被钉住。
/// </para>
/// </summary>
/// <param name="Kind">启动方式。</param>
/// <param name="ExecutablePath"><see cref="BrowserLaunchKind.Executable"/> 时的可执行文件路径；否则 null。</param>
/// <param name="FallbackReason">发生回落时给控制台/日志看的原因；未回落为 null。</param>
public sealed record BrowserLaunchPlan(
    BrowserLaunchKind Kind,
    string? ExecutablePath,
    string? FallbackReason)
{
    /// <summary>
    /// 从配置值算出启动计划。
    /// <para>
    /// 回落规则（用户已确认"运行时不打断，只在控制台留一行"）：路径空白 → 系统默认；
    /// 路径展开环境变量后文件不存在 → <b>系统默认 + 带原因</b>，而不是报错或不打开。
    /// </para>
    /// <para>
    /// 存在性判断用注入的 <paramref name="fileExists"/> 而不是 <c>File.Exists</c>：
    /// Domain 层（net8.0，无 Windows 依赖）保持纯函数，测试可以直接喂任意结果。
    /// </para>
    /// </summary>
    /// <param name="configuredPath">配置里的浏览器位置；空白 = 系统默认。</param>
    /// <param name="fileExists">文件存在性判断（生产传 <c>File.Exists</c>）。</param>
    public static BrowserLaunchPlan Resolve(string? configuredPath, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return new BrowserLaunchPlan(BrowserLaunchKind.SystemDefault, null, null);

        var trimmed = configuredPath.Trim();

        // 用户会写 %LOCALAPPDATA%\... 这类路径；展开失败（含未定义变量）时保留原样，
        // 由下面的存在性判断自然回落。
        var expanded = Environment.ExpandEnvironmentVariables(trimmed);

        if (fileExists(expanded))
            return new BrowserLaunchPlan(BrowserLaunchKind.Executable, expanded, null);

        return new BrowserLaunchPlan(
            BrowserLaunchKind.SystemDefault, null,
            $"指定的浏览器不存在，已回落到系统默认浏览器：{expanded}");
    }
}
