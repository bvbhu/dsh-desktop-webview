using System.IO;

namespace DshDesktop.Infrastructure;

/// <summary>
/// 数据目录策略：**只支持便携模式** —— config.json / run.log / WebView2Profile / temp
/// 全部与 exe 同级，整个文件夹可以整体搬移。
/// <para>
/// 为什么不再有 <c>%LocalAppData%</c> 那条路（原决策 D2 的"单 exe 优先"已废弃）：
/// 单 exe 产物 187 MB，而免安装版只有几十 MB。只发便携版之后，"所有写入都落在程序目录内"
/// 就成了硬约束 —— 任何情况下都不许悄悄改写到用户目录，否则"整个文件夹可搬移"这个承诺不成立：
/// 换个目录/换台机器就会冒出第二份配置，而用户以为自己已经搬干净了。
/// </para>
/// <para>
/// 落到本目录之外仍有写入的只有两类，都不归本程序管：
/// ① WebView2 运行时本身安装在系统位置（对本程序只读）；
/// ② 被启动的子命令（如 dsh）自己往 <c>~/.dsh</c> 里写。
/// </para>
/// </summary>
public static class DataPaths
{
    public const string ConfigFileName = "config.json";
    public const string RunLogFileName = "run.log";
    public const string ProfileFolderName = "WebView2Profile";
    public const string TempFolderName = "temp";

    /// <summary>数据根目录 = 程序所在目录。</summary>
    public static string Root => AppContext.BaseDirectory;

    public static string ConfigFile => Path.Combine(Root, ConfigFileName);

    public static string RunLogFile => Path.Combine(Root, RunLogFileName);

    /// <summary>WebView2 的用户数据目录（缓存 / Cookie / LocalStorage 都在里面）。</summary>
    public static string ProfileDir => Path.Combine(Root, ProfileFolderName);

    /// <summary>
    /// 进程级临时目录：<c>&lt;程序目录&gt;\temp</c>。
    /// <para>
    /// **不要用它去设进程级的 TMP/TEMP**（那是旧做法，已废弃，原因见
    /// <see cref="TempEnvironmentVariableNames"/> 的注释）。它的用途是：
    /// ① 作为启动子进程时定向注入的临时目录；② 程序自己建/清理这个目录。
    /// </para>
    /// </summary>
    public static string TempDir => Path.Combine(Root, TempFolderName);

    /// <summary>
    /// 子进程需要被定向的临时目录环境变量名。
    /// <para>
    /// 为什么不是"给本进程设置这两个变量"：那种做法会把值**继承给本进程拉起的每一个子进程**，
    /// 包括 WebView2 的整个浏览器进程树，以及用户在页面里跑起来的任何东西。
    /// 后果是这些子进程的 temp 落在程序目录内 —— 对某些工具是致命的：例如 DSH 自己的
    /// 沙箱会强制要求临时根在工作区之外（否则写入隔离可被绕过），撞上就整条命令拒绝执行。
    /// 用户的直觉也是"我只是开个网页，凭什么我的终端临时目录被改了"。
    /// </para>
    /// <para>
    /// 改成只对**我们主动拉起的服务进程**（见 CommandLauncher）注入这两个变量，
    /// 便携承诺照样成立：这些进程产出的临时文件仍然落在程序目录内，不污染 %TEMP%。
    /// WebView2 的浏览器进程则由 <c>WEBVIEW2_USER_DATA_FOLDER</c> 之外的手段处理不了 ——
    /// 它的临时文件由 user data folder 决定（<see cref="ProfileDir"/>），本来就在程序目录内。
    /// </para>
    /// </summary>
    public static readonly string[] TempEnvironmentVariableNames = { "TMP", "TEMP" };

    /// <summary>
    /// 数据目录是否可写。**用真实写文件来探测**，而不是看目录属性：
    /// 只读挂载、ACL 拒绝、受控文件夹访问（Windows 的"勒索软件防护"）都会让
    /// "属性看起来可写"但实际写不进去 —— 那样用户会看到"保存了但没生效"。
    /// </summary>
    public static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
