namespace DshDesktop.Domain;

/// <summary>
/// 启动命令的执行结果。
/// </summary>
/// <param name="Url">第 1 级命中的 URL；未命中为 <c>null</c>。</param>
/// <param name="HitSuccessMarker">第 2 级（成功标志正则）是否命中。</param>
/// <param name="CapturedOutput">stdout / stderr 的捕获文本（已剥控制序列）。</param>
/// <param name="ExitCode">
/// 进程退出码。<b>仅在进程确实已退出时有值</b>；仍在运行（即超时收尾）时为 <c>null</c>。
/// <para>
/// 为什么需要它：它是「进程退出非零 → Failed」那条迁移（设计文档 §4.1）的唯一前提。
/// 缺了它，调用方无法区分"命令根本不存在、秒退"与"服务启动慢、30 秒没吐 URL"两种情形 ——
/// 前者在几百毫秒内就已经可知，却要陪后者一起干等满超时。
/// </para>
/// <para>
/// 注意区分 <c>null</c> 与 <c>0</c>：<c>null</c> = 进程还活着（正常收尾路径），
/// <c>0</c> = 进程正常退出（服务可能是常驻失败或自己退了），非 0 = 启动命令确实失败。
/// </para>
/// </param>
public sealed record LaunchResult(
    string? Url,
    bool HitSuccessMarker,
    string CapturedOutput,
    int? ExitCode = null);
