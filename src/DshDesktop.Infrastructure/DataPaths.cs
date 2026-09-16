using System.IO;

namespace DshDesktop.Infrastructure;

/// <summary>
/// 数据目录策略：只支持便携模式。config.json / run.log / WebView2Profile / temp 全部与 exe 同级，
/// 整个文件夹可整体搬移；绝不悄悄改写到用户目录，否则「搬干净了」是假的（换台机器会冒出第二份配置）。
/// <para>落到本目录外的写入只有两类，都不归本程序管：WebView2 运行时装在系统位置；子命令自己写 <c>~/.dsh</c>。</para>
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

    /// <summary>程序自己的临时目录 <c>&lt;程序目录&gt;\temp</c>：注入给子进程，自建自清。</summary>
    public static string TempDir => Path.Combine(Root, TempFolderName);

    /// <summary>
    /// 只注入给主动拉起的服务进程，不设到本进程 —— 设了会被每个子进程继承（含 WebView2 整棵进程树），
    /// 而它们的 temp 落在程序目录内是致命的：DSH 沙箱强制要求临时根在工作区之外，撞上就整条命令拒绝执行。
    /// </summary>
    public static readonly string[] TempEnvironmentVariableNames = { "TMP", "TEMP" };

    /// <summary>数据目录是否可写。用真实写文件探测：只读挂载、ACL 拒绝、受控文件夹访问都会让属性看着可写却写不进去。</summary>
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
