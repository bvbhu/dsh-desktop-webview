using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DshDesktop.Domain;

namespace DshDesktop.App;

public partial class ChromeBar : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty IconBrushProperty =
        DependencyProperty.Register(nameof(IconBrush), typeof(Brush), typeof(ChromeBar),
            new PropertyMetadata(null));

    /// <summary>光标轮询周期。100ms 足够跟手，代价可忽略。</summary>
    private static readonly TimeSpan CursorPollInterval = TimeSpan.FromMilliseconds(100);

    // 两态图标共用同一包围盒（见 ChromeBar.xaml 的注释）：不这样切换时会跳位。
    private const string MaximizeGlyph = "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z";
    private const string RestoreGlyph = "M2.5,0.5 L9.5,0.5 L9.5,7.5 L7.5,7.5 M0.5,2.5 L7.5,2.5 L7.5,9.5 L0.5,9.5 Z";

    private readonly DispatcherTimer _cursorTimer;
    private DispatcherTimer? _hoverTimer;
    private bool _defaultVisible;
    private int _hoverDelayMs = 800;
    private bool _cursorInside;
    private bool _hovered;

    public Brush? IconBrush
    {
        get => (Brush?)GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    public event EventHandler? MinimizeRequested;
    public event EventHandler? MaximizeRequested;
    public event EventHandler? CloseRequested;

    public ChromeBar()
    {
        InitializeComponent();
        // 用 SetResourceReference 而非 FindResource 赋值：前者建立动态资源引用，
        // 主题字典在运行期被替换时图标色会跟着变；后者只在构造这一刻取到一份快照。
        SetResourceReference(IconBrushProperty, "ChromeButtonIconBrush");

        // 悬停检测为什么是轮询而不是 MouseEnter/MouseLeave：
        // 命中测试是把双刃剑 —— 能收到 MouseEnter 的元素，同时也会吃掉它覆盖区域的点击。
        // 而三键隐藏时用户必须还能点页面（否则页面右上角就永远是死区）。
        // 于是覆盖层一律 IsHitTestVisible=False，只"观察"光标，不拦截任何输入。
        _cursorTimer = new DispatcherTimer { Interval = CursorPollInterval };
        _cursorTimer.Tick += OnCursorTick;
        _cursorTimer.Start();
    }

    public void Configure(AppConfig cfg)
    {
        _defaultVisible = cfg.ChromeButtonsDefault == ChromeButtonVisibility.Shown;
        _hoverDelayMs = cfg.ChromeHoverDelayMs;

        // 颜色留空 = 跟随主题。"固定色"与"跟随主题"是同一 DP 上的本地值 /
        // 资源引用两种状态，必须成对设置，否则一旦配过颜色就再也回不到跟随主题。
        // 解析统一走 Domain.HexColor：设置窗口的校验必须与此处共用同一个解析器，
        // 否则会出现"设置窗口放行、界面静默回落主题色"，用户只能看到"设置不生效"。
        var hasIconColor = HexColor.TryParse(cfg.ChromeButtonIconColor, out var ia, out var ir, out var ig, out var ib);
        var hasBackground = HexColor.TryParse(cfg.ChromeButtonBackground, out var ba, out var br, out var bgg, out var bb);

        // 图标颜色三档：显式配了就用它（配置文件里的逃生口，设置窗口已不再提供该字段）；
        // 配了底色就按底色对比度自动取黑或白（用户要求：图标颜色由背景判断）；
        // 都没有才回落到主题的图标色。
        if (hasIconColor)
            IconBrush = new SolidColorBrush(Color.FromArgb(ia, ir, ig, ib));
        else if (hasBackground)
            IconBrush = new SolidColorBrush(HexColor.PreferBlackForeground(br, bgg, bb) ? Colors.Black : Colors.White);
        else
            SetResourceReference(IconBrushProperty, "ChromeButtonIconBrush");

        var background = hasBackground
            ? new SolidColorBrush(Color.FromArgb(ba, br, bgg, bb))
            : null;
        ApplyButtonBackground(BtnMinimize, background);
        ApplyButtonBackground(BtnMaximize, background);
        ApplyButtonBackground(BtnClose, background);

        ApplyState();
    }

    /// <summary>
    /// 配了固定底色就设本地值（压过样式，不随主题）；留空则 ClearValue 清掉本地值，
    /// 让样式里的 DynamicResource 重新接管。
    /// </summary>
    private static void ApplyButtonBackground(System.Windows.Controls.Button button, Brush? brush)
    {
        if (brush is null)
            button.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        else
            button.Background = brush;
    }

    private bool IsCursorInside()
    {
        // Mouse.GetPosition 与命中测试无关：即使本控件 IsHitTestVisible=False 也能拿到坐标，
        // 所以"只观察不拦截"才成立。
        var point = Mouse.GetPosition(this);
        return point.X >= 0 && point.Y >= 0 && point.X < ActualWidth && point.Y < ActualHeight;
    }

    private void OnCursorTick(object? sender, EventArgs e)
    {
        var inside = IsCursorInside();
        if (inside == _cursorInside)
            return;

        _cursorInside = inside;

        if (inside)
        {
            // 悬停 chromeHoverDelayMs 毫秒后切到非默认态
            _hoverTimer?.Stop();
            _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_hoverDelayMs) };
            _hoverTimer.Tick += (_, _) =>
            {
                _hoverTimer!.Stop();
                _hovered = true;
                ApplyState();
            };
            _hoverTimer.Start();
        }
        else
        {
            // 移出区域后回到默认态
            _hoverTimer?.Stop();
            _hovered = false;
            ApplyState();
        }
    }

    /// <summary>
    /// 可见 = 默认态 ^ 悬停态。隐藏时同时把 IsHitTestVisible 关掉，
    /// 这样覆盖区内的点击会穿透到页面（三键显示时才拦截 —— 那时整片区域本来就是按键）。
    /// </summary>
    private void ApplyState()
    {
        var visible = _hovered ? !_defaultVisible : _defaultVisible;
        ButtonsPanel.Opacity = visible ? 1 : 0;
        ButtonsPanel.IsHitTestVisible = visible;
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) =>
        MinimizeRequested?.Invoke(this, EventArgs.Empty);

    private void BtnMaximize_Click(object sender, RoutedEventArgs e) =>
        MaximizeRequested?.Invoke(this, EventArgs.Empty);

    private void BtnClose_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    public void SetMaximized(bool maximized)
    {
        MaximizeIcon.Data = Geometry.Parse(maximized ? RestoreGlyph : MaximizeGlyph);
    }
}
