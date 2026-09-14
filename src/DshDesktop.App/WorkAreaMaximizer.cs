using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DshDesktop.App;

/// <summary>
/// 让「最大化」的可见内容正好等于显示器工作区。
/// <para>
/// <b>问题</b>：Windows 最大化可缩放窗口时会把它放大一圈
/// （<c>SM_CXSIZEFRAME + SM_CXPADDEDBORDER</c>，本机 8px/边），好让边框溢到屏幕外。
/// 普通窗口靠非客户区那圈边框消化这点溢出，而本窗口用 <c>WindowChrome</c> 把客户区做成了整窗
/// （它接管了 <c>WM_NCCALCSIZE</c>），于是溢出直接变成<b>内容四周被裁 8px</b>。
/// 实测量测（2026-09-13）：最大化后窗口 <c>1936x1096 @ (-8,-8)</c>，工作区 <c>1920x1080</c>。
/// </para>
/// <para>
/// <b>试过但无效的两条路</b>（日志均有留证）：① 回填 <c>WM_GETMINMAXINFO</c> 的
/// <c>ptMaxSize</c>/<c>ptMaxPosition</c> —— 值确实写进去了，最终矩形仍是溢出值；
/// ② <c>SetWindowPos</c> 硬改实际矩形 —— 调用成功，一秒后又退回。最大化几何由系统自己维护，不回让。
/// </para>
/// <para>
/// <b>最终做法</b>：不跟窗口矩形较劲，改为<b>把内容内缩同样的量</b>。那多出来的一圈本来就在屏幕外，
/// 内缩之后可见部分正好是完整内容：既不裁切、也不留白。
/// </para>
/// </summary>
internal static class WorkAreaMaximizer
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;
    private const int SmCxSizeFrame = 32;
    private const int SmCySizeFrame = 33;
    private const int SmCxPaddedBorder = 92;

    /// <summary>内缩量的合理上限（像素）。超过就认为是取错了显示器/异常值，宁可不缩。</summary>
    private const int MaxSaneInset = 64;

    private static Action<string>? _log;
    private static bool _loggedDefault;
    private static int _lastInsetX = -1;
    private static int _lastInsetY = -1;

    /// <summary>在窗口句柄创建时挂上 <c>WM_GETMINMAXINFO</c> 处理。构造期调用即可。</summary>
    public static void Attach(Window window, Action<string>? log = null)
    {
        _log = log;
        window.SourceInitialized += (_, _) => AttachCore(window);
    }

    /// <summary>
    /// 计算最大化时内容需要内缩的边距，让可见内容正好落在工作区上。
    /// 非最大化、取不到工作区、或窗口并未被放大时一律返回零边距 —— 因此可以安全地在
    /// <c>SizeChanged</c> / <c>StateChanged</c> 上反复调用。
    /// </summary>
    public static Thickness ComputeMaximizedContentInset(Window window)
    {
        if (window.WindowState != WindowState.Maximized)
            return default;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return default;

        if (!TryGetWorkArea(hwnd, out var work) || !GetWindowRect(hwnd, out var actual))
            return default;

        var insetX = ((actual.Right - actual.Left) - work.Width) / 2;
        var insetY = ((actual.Bottom - actual.Top) - work.Height) / 2;

        // 尺寸还没落到最大化（StateChanged 先于系统改矩形时）会算出 ≤0，直接不缩。
        if (insetX <= 0 || insetY <= 0 || insetX > MaxSaneInset || insetY > MaxSaneInset)
            return default;

        if (insetX != _lastInsetX || insetY != _lastInsetY)
        {
            _lastInsetX = insetX;
            _lastInsetY = insetY;
            _log?.Invoke($"[maximize] 窗口 {actual.Right - actual.Left}x{actual.Bottom - actual.Top}"
                + $"@{actual.Left},{actual.Top} 比工作区 {work.Width}x{work.Height}@{work.Left},{work.Top}"
                + $" 每边多出 {insetX},{insetY} —— 内容按此内缩，可见部分即为工作区");
        }

        return new Thickness(insetX, insetY, insetX, insetY);
    }

    private static void AttachCore(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source)
            source.AddHook(Hook);
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo)
            return IntPtr.Zero;

        if (!TryGetWorkArea(hwnd, out var work))
            return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        // 只记第一次：用来核对"系统默认给的是什么"以及"我们回填了什么"。
        if (!_loggedDefault)
        {
            _loggedDefault = true;
            var frameX = GetSystemMetrics(SmCxSizeFrame) + GetSystemMetrics(SmCxPaddedBorder);
            var frameY = GetSystemMetrics(SmCySizeFrame) + GetSystemMetrics(SmCxPaddedBorder);
            _log?.Invoke($"[maximize] WM_GETMINMAXINFO 默认值 ptMaxSize={mmi.ptMaxSize.X}x{mmi.ptMaxSize.Y} "
                + $"ptMaxPosition={mmi.ptMaxPosition.X},{mmi.ptMaxPosition.Y}"
                + $"（比工作区多 {(mmi.ptMaxSize.X - work.Width) / 2},{(mmi.ptMaxSize.Y - work.Height) / 2}，"
                + $"系统标称边框={frameX},{frameY}）；工作区={work.Width}x{work.Height}@{work.Left},{work.Top}；"
                + $"回填为 ptMaxSize={work.Width}x{work.Height} ptMaxPosition={work.Left},{work.Top}");
        }

        mmi.ptMaxSize.X = work.Width;
        mmi.ptMaxSize.Y = work.Height;
        mmi.ptMaxPosition.X = work.Left;
        mmi.ptMaxPosition.Y = work.Top;
        Marshal.StructureToPtr(mmi, lParam, true);

        // 必须声明已处理，否则 DefWindowProc 会把默认值（含那圈溢出）覆盖回来。
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>取当前显示器的工作区（相对显示器左上角，物理像素 —— per-monitor DPI 下也是对的）。</summary>
    private static bool TryGetWorkArea(IntPtr hwnd, out PixelRect work)
    {
        work = default;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return false;

        work = new PixelRect(
            info.rcWork.Left - info.rcMonitor.Left,
            info.rcWork.Top - info.rcMonitor.Top,
            info.rcWork.Right - info.rcWork.Left,
            info.rcWork.Bottom - info.rcWork.Top);
        return true;
    }

    private readonly record struct PixelRect(int Left, int Top, int Width, int Height);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point ptReserved;
        public Point ptMaxSize;
        public Point ptMaxPosition;
        public Point ptMinTrackSize;
        public Point ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public int dwFlags;
    }
}
