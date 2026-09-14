using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DshDesktop.App;

/// <summary>
/// 取色面：色相环 + 饱和度/明度方块 + HSV/HSL/RGB 滑轨。
/// <para>
/// 单独包一层的理由见 XAML 里的注释（类名与库命名空间同名、以及把三方依赖收在一处）。
/// 对外只暴露两个东西：<see cref="Color"/> 和 <see cref="ShowAlpha"/>。
/// </para>
/// </summary>
public partial class PickerSurface : UserControl
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(PickerSurface),
        new FrameworkPropertyMetadata(Colors.Black,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ShowAlphaProperty = DependencyProperty.Register(
        nameof(ShowAlpha), typeof(bool), typeof(PickerSurface),
        new PropertyMetadata(true, OnShowAlphaChanged));

    public PickerSurface() => InitializeComponent();

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>false = 藏掉透明度滑轨（三键背景色是不透明的）。</summary>
    public bool ShowAlpha
    {
        get => (bool)GetValue(ShowAlphaProperty);
        set => SetValue(ShowAlphaProperty, value);
    }

    private static void OnShowAlphaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PickerSurface)d).Sliders.ShowAlpha = (bool)e.NewValue;
}
