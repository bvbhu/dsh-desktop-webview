using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DshDesktop.Domain;

namespace DshDesktop.App;

/// <summary>
/// 颜色选择器字段。收起态 = **一个**「色块 + 选择颜色 + ▾」按钮，点开即完整取色器 + 预设色板；
/// 允许清空时右侧另有一个 <c>✕</c>（语义是"清除 → 跟随主题"，不是"打开"）。
/// <para>
/// 取色部分用三方库 PixiEditor.ColorPicker（MIT）。不自己写的原因：色相环、饱和度/明度方块、
/// 三条渐变滑轨加数值框是一整套带边界条件的绘制（色相 0/360 环绕、灰度下饱和度无意义、
/// 拖动时的重入），自己写一遍等于把这些坑重踩一次。
/// 不摆 <c>HexColorTextBox</c> —— 参考图明确要求"只允许颜色选择器，不提供输入框"，
/// 组合式的控件天然支持这个要求。
/// </para>
/// <para>
/// <b>类名不要改回 <c>ColorPicker</c></b>：那个名字与库的命名空间重名，而 XAML 生成的代码里
/// 引用是 <c>ColorPicker.ColorSliders</c> 这种不带 <c>global::</c> 的写法 ——
/// 只要本项目里存在名为 <c>ColorPicker</c> 的类型，它就会被解析成那个类型，
/// 编译期直接报"类型 ColorPicker 中不存在类型名 ColorSliders"（本文件就是这么改过来的）。
/// </para>
/// <para>
/// 解析/序列化统一走 <see cref="HexColor"/>：与设置窗口的校验、界面套用共用同一个解析器，
/// 否则又会回到"设置窗口放行、界面静默回落主题色"的老问题。
/// </para>
/// </summary>
public partial class ColorPickerField : UserControl
{
    public static readonly DependencyProperty ColorHexProperty = DependencyProperty.Register(
        nameof(ColorHex), typeof(string), typeof(ColorPickerField),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnColorHexChanged));

    public static readonly DependencyProperty AllowEmptyProperty = DependencyProperty.Register(
        nameof(AllowEmpty), typeof(bool), typeof(ColorPickerField),
        new PropertyMetadata(false, OnAllowEmptyChanged));

    public static readonly DependencyProperty ShowAlphaSliderProperty = DependencyProperty.Register(
        nameof(ShowAlphaSlider), typeof(bool), typeof(ColorPickerField),
        new PropertyMetadata(false, OnLayoutChanged));

    /// <summary>
    /// 与取色器交换颜色用的枢纽（<c>System.Windows.Media.Color</c>）。
    /// 之所以要它：三方控件只认 <c>Color</c>，而本控件的对外契约是十六进制字符串
    /// （配置里就是这么存的）。公开是为了让弹层里的绑定能落到它上面。
    /// </summary>
    public static readonly DependencyProperty HubColorProperty = DependencyProperty.Register(
        nameof(HubColor), typeof(Color), typeof(ColorPickerField),
        new FrameworkPropertyMetadata(Colors.Black,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHubColorChanged));

    private static readonly DependencyProperty PreviewBrushProperty = DependencyProperty.Register(
        nameof(PreviewBrush), typeof(Brush), typeof(ColorPickerField),
        new PropertyMetadata(Brushes.Transparent));

    private static readonly DependencyProperty PreviewBorderBrushProperty = DependencyProperty.Register(
        nameof(PreviewBorderBrush), typeof(Brush), typeof(ColorPickerField),
        new PropertyMetadata(Brushes.Transparent));

    private static readonly DependencyProperty ClearVisibilityProperty = DependencyProperty.Register(
        nameof(ClearVisibility), typeof(Visibility), typeof(ColorPickerField),
        new PropertyMetadata(Visibility.Collapsed));

    private static readonly DependencyProperty EmptyLabelVisibilityProperty = DependencyProperty.Register(
        nameof(EmptyLabelVisibility), typeof(Visibility), typeof(ColorPickerField),
        new PropertyMetadata(Visibility.Collapsed));

    /// <summary>Hex ↔ Color 双向同步时的重入闸：两个方向都会写对方，不挡一下就是无限递归。</summary>
    private bool _syncing;

    public ColorPickerField()
    {
        InitializeComponent();

        // Popup 的内容在独立视觉树里，ElementName 绑定不可靠 —— 直接给它一个自身作 DataContext，
        // 弹层内部一律用普通 {Binding ...}（Palette / HubColor / ShowAlphaSlider）。
        PalettePopup.DataContext = this;

        ApplyLayout();
        Refresh();
    }

    /// <summary>当前颜色的十六进制文本；空串 = 跟随主题（仅当 <see cref="AllowEmpty"/> 为真）。</summary>
    public string ColorHex
    {
        get => (string)GetValue(ColorHexProperty);
        set => SetValue(ColorHexProperty, value);
    }

    /// <summary>是否允许留空（三键颜色允许；拖动层颜色不允许）。</summary>
    public bool AllowEmpty
    {
        get => (bool)GetValue(AllowEmptyProperty);
        set => SetValue(AllowEmptyProperty, value);
    }

    /// <summary>是否显示透明度滑轨（ARGB 的 AA 通道）。</summary>
    public bool ShowAlphaSlider
    {
        get => (bool)GetValue(ShowAlphaSliderProperty);
        set => SetValue(ShowAlphaSliderProperty, value);
    }

    /// <summary>取色器侧的颜色枢纽，见 <see cref="HubColorProperty"/>。</summary>
    public Color HubColor
    {
        get => (Color)GetValue(HubColorProperty);
        set => SetValue(HubColorProperty, value);
    }

    public Brush PreviewBrush
    {
        get => (Brush)GetValue(PreviewBrushProperty);
        private set => SetValue(PreviewBrushProperty, value);
    }

    public Brush PreviewBorderBrush
    {
        get => (Brush)GetValue(PreviewBorderBrushProperty);
        private set => SetValue(PreviewBorderBrushProperty, value);
    }

    public Visibility ClearVisibility
    {
        get => (Visibility)GetValue(ClearVisibilityProperty);
        private set => SetValue(ClearVisibilityProperty, value);
    }

    public Visibility EmptyLabelVisibility
    {
        get => (Visibility)GetValue(EmptyLabelVisibilityProperty);
        private set => SetValue(EmptyLabelVisibilityProperty, value);
    }

    /// <summary>预设调色板：中性色 8 档，其余为红/橙/黄/绿/青/蓝/紫/粉/棕 各四档。</summary>
    public IReadOnlyList<string> Palette { get; } = new[]
    {
        "#FFFFFF", "#F5F5F5", "#E0E0E0", "#BDBDBD", "#9E9E9E", "#616161", "#424242", "#000000",
        "#FFEBEE", "#EF9A9A", "#F44336", "#B71C1C", "#FFE0B2", "#FFB74D", "#FF9800", "#E65100",
        "#FFF9C4", "#FFF176", "#FFEB3B", "#F9A825", "#E8F5E9", "#A5D6A7", "#4CAF50", "#1B5E20",
        "#E0F7FA", "#80DEEA", "#00BCD4", "#006064", "#E3F2FD", "#64B5F6", "#2196F3", "#0D47A1",
        "#F3E5F5", "#CE93D8", "#9C27B0", "#4A148C", "#FCE4EC", "#F48FB1", "#E91E63", "#AD1457",
        "#8D6E63", "#A1887F", "#795548", "#3E2723",
    };

    private static void OnColorHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ColorPickerField)d).Refresh();

    private static void OnAllowEmptyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ColorPickerField)d).Refresh();

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ColorPickerField)d).ApplyLayout();

    /// <summary>取色器改色 → 回写十六进制（会被 <see cref="_syncing"/> 挡在 Refresh 之外）。</summary>
    private static void OnHubColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (ColorPickerField)d;
        if (picker._syncing)
            return;

        var c = (Color)e.NewValue;
        picker._syncing = true;
        try
        {
            picker.ColorHex = HexColor.ToHex(c.A, c.R, c.G, c.B);
        }
        finally
        {
            picker._syncing = false;
        }
    }

    private void ApplyLayout()
    {
        // 底衬只在能调透明度时出现：不透明的颜色下面垫格子只是噪点。
        // （透明滑轨本身的显隐由弹层里绑到 PickerSurface.ShowAlpha 的表达式负责。）
        CheckerLayer.Visibility = ShowAlphaSlider ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Refresh()
    {
        var hasText = !string.IsNullOrWhiteSpace(ColorHex);
        var valid = HexColor.TryParse(ColorHex, out var a, out var r, out var g, out var b);

        PreviewBrush = valid ? new SolidColorBrush(Color.FromArgb(a, r, g, b)) : Brushes.Transparent;

        // 非空但解析不了 → 色块描边标红。静默回落主题色正是"设置疑似不生效"的来源。
        // 现在只能从配置文件里带进来（界面已无十六进制输入框），但同样要看得见。
        var invalid = hasText && !valid;
        PreviewBorderBrush = invalid
            ? new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23))
            : (Brush)FindResource("SwatchBorderBrush");

        ClearVisibility = AllowEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyLabelVisibility = valid ? Visibility.Collapsed : Visibility.Visible;
        EmptyLabel.Text = invalid ? "非法" : "主题";

        // 外部改色（含保存后回填）→ 推给取色器。
        if (valid)
        {
            PushToPicker(Color.FromArgb(a, r, g, b));
        }
        else
        {
            // 空值（跟随主题）时取色器总得从某个颜色开始：DP 默认值是纯黑，
            // 而纯黑的 S/V 都是 0 —— 打开就是一片黑方块，用户得先把明度拉起来才能选色。
            // 用主题强调色起步就没这个坎。
            if (TryFindResource("AccentBrush") is SolidColorBrush accent)
                PushToPicker(accent.Color);
        }
    }

    /// <summary>
    /// 把颜色推给取色器。值相同就不要再写：拖动滑轨时每个像素都会走一遍 Refresh，
    /// 无谓的回写会让手感发飘。
    /// </summary>
    private void PushToPicker(Color color)
    {
        if (HubColor == color)
            return;

        _syncing = true;
        try
        {
            HubColor = color;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SwatchButton_Click(object sender, RoutedEventArgs e) =>
        PalettePopup.IsOpen = !PalettePopup.IsOpen;

    private void PaletteCell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
            ColorHex = hex;

        PalettePopup.IsOpen = false;
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ColorHex = string.Empty;
}
