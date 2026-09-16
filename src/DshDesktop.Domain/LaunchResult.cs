namespace DshDesktop.Domain;

/// <summary>
/// 启动命令的执行结果。Url = 第 1 级命中的 URL；HitSuccessMarker = 第 2 级（成功标志正则）是否命中；
/// CapturedOutput = stdout / stderr 捕获文本（已剥控制序列）；ExitCode 为 <c>null</c> 表示进程仍存活。
/// </summary>
public sealed record LaunchResult(
    string? Url,
    bool HitSuccessMarker,
    string CapturedOutput,
    int? ExitCode = null);
