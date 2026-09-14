using System.IO;
using System.Text;
using DshDesktop.Infrastructure;
using FluentAssertions;

namespace DshDesktop.Infrastructure.Tests;

public class RunLogTests : IDisposable
{
    private readonly string _dir;
    private readonly string _logPath;

    public RunLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"runlog_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _logPath = Path.Combine(_dir, "run.log");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Fact]
    public void Append_WritesTimestampedLine()
    {
        var log = new RunLog(_logPath);
        log.Append("[probe] 探测 http://127.0.0.1:3080/");

        var text = File.ReadAllText(_logPath, Encoding.UTF8);
        text.Should().Contain("[probe] 探测 http://127.0.0.1:3080/");
        text.Should().MatchRegex(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}");
    }

    [Fact]
    public void Append_IsAdditive_AndUtf8()
    {
        var log = new RunLog(_logPath);
        log.Append("第一行 启动");
        log.Append("第二行 退出");

        var lines = File.ReadAllLines(_logPath, Encoding.UTF8);
        lines.Should().HaveCount(2);
        lines[0].Should().EndWith("第一行 启动");
        lines[1].Should().EndWith("第二行 退出");
    }

    [Fact]
    public void Append_UnwritablePath_DoesNotThrow()
    {
        // 日志失败不得影响主流程（RunLog 契约）
        var log = new RunLog(Path.Combine(_dir, "missing", "nested", "run.log"));
        Action act = () => log.Append("boom");
        act.Should().NotThrow();
    }
}

public class DataPathsTests
{
    [Fact]
    public void RootsAtExeDirectory()
    {
        DataPaths.Root.Should().Be(AppContext.BaseDirectory);
        DataPaths.ConfigFile.Should().Be(Path.Combine(AppContext.BaseDirectory, "config.json"));
        DataPaths.RunLogFile.Should().Be(Path.Combine(AppContext.BaseDirectory, "run.log"));
        DataPaths.ProfileDir.Should().Be(Path.Combine(AppContext.BaseDirectory, "WebView2Profile"));
        DataPaths.TempDir.Should().Be(Path.Combine(AppContext.BaseDirectory, "temp"));
    }

    /// <summary>
    /// 这条用例是"绝不在程序目录之外写东西"这个要求的可执行形式：
    /// 任何一个数据路径只要逃出 Root，用户搬移文件夹时就会留下残余（配置分叉）。
    /// </summary>
    [Fact]
    public void EveryPathStaysInsideRoot()
    {
        var root = Path.GetFullPath(DataPaths.Root);

        foreach (var path in new[]
                 {
                     DataPaths.ConfigFile,
                     DataPaths.RunLogFile,
                     DataPaths.ProfileDir,
                     DataPaths.TempDir,
                 })
        {
            var full = Path.GetFullPath(path);
            full.Should().StartWith(root, "数据路径不允许落在程序目录之外");
        }

        // 顺带钉住：不再有任何指向 %LocalAppData% 的数据目录
        DataPaths.Root.Should().NotBe(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DSH-Desktop-Webview"));
    }

    [Fact]
    public void IsWritable_True_ForTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-writable-" + Guid.NewGuid().ToString("N"));
        try
        {
            DataPaths.IsWritable(dir).Should().BeTrue();
            Directory.Exists(dir).Should().BeTrue("探测成功时目录应已创建");
            Directory.GetFiles(dir).Should().BeEmpty("探测文件必须自己清掉，不能留垃圾");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void IsWritable_False_ForImpossiblePath()
    {
        // 不存在的盘符：CreateDirectory 会抛，必须被吞掉并如实返回 false
        DataPaths.IsWritable(@"Z:\definitely\not\here").Should().BeFalse();
    }
}
