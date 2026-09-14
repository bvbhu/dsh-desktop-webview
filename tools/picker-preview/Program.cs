using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DshDesktop.App;

namespace PickerPreview;

/// <summary>
/// Renders <see cref="ColorPickerField"/> in its collapsed state to a PNG, so the layout can be
/// eyeballed without UI-automating the live app (which this sandbox blocks).
/// </summary>
/// <remarks>
/// Run: <c>dotnet run --project tools/picker-preview -c Debug -- &lt;out.png&gt; [light|dark]</c>
/// <para>
/// Why a real WPF app rather than a PowerShell script: the control lives in a net8.0-windows
/// assembly, and Windows PowerShell 5.1 is on .NET Framework — it cannot load that assembly at
/// all. A net8.0-windows host renders it directly.
/// </para>
/// <para>
/// The window is placed far offscreen before Show(): it must be shown (not just measured) for
/// the template and DynamicResource lookups to resolve, but it must never flash on the user's
/// desktop.
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var outFile = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "picker-preview.png");
        var dark = args.Length > 1 && args[1].Equals("dark", StringComparison.OrdinalIgnoreCase);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        MergeTheme(app.Resources, dark);

        var panel = new StackPanel { Margin = new Thickness(24) };

        // 1) empty (AllowEmpty) -> shows the "theme" placeholder
        panel.Children.Add(CreateField("", allowEmpty: true, showAlpha: false));
        // 2) filled, opaque
        panel.Children.Add(CreateField("#2196F3", allowEmpty: true, showAlpha: false));
        // 3) drag-strip case: alpha slider + cannot be empty
        panel.Children.Add(CreateField("#800000FF", allowEmpty: false, showAlpha: true));
        // 4) invalid value -> swatch outline should go red, label "非法"
        panel.Children.Add(CreateField("not-a-color", allowEmpty: true, showAlpha: false));

        var window = new Window
        {
            Width = 460,
            Height = 300,
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = new Border
            {
                Background = (Brush)app.Resources["WindowBackgroundBrush"],
                Padding = new Thickness(8),
                Child = panel,
            },
        };

        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        window.UpdateLayout();

        var w = (int)Math.Ceiling(panel.ActualWidth);
        var h = (int)Math.Ceiling(panel.ActualHeight);
        if (w <= 0 || h <= 0)
        {
            Console.Error.WriteLine($"layout produced {w}x{h}");
            return 1;
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(panel);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(outFile))
            encoder.Save(fs);

        window.Close();
        app.Shutdown();

        Console.WriteLine($"saved {outFile} ({w}x{h})");
        return 0;
    }

    private static ColorPickerField CreateField(string hex, bool allowEmpty, bool showAlpha) =>
        new()
        {
            ColorHex = hex,
            AllowEmpty = allowEmpty,
            ShowAlphaSlider = showAlpha,
            Margin = new Thickness(0, 0, 0, 18),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

    /// <summary>
    /// Builds the theme dictionary by hand. We cannot point ResourceDictionary.Source at
    /// Themes/Light.xaml: that pack:// URI uses three commas, and the URI parser reads ",,," as a
    /// port and throws. Loading the app assembly's own resources avoids that entirely, but the
    /// keys we need are few, so mirroring them here is the honest, simple option.
    /// </summary>
    private static void MergeTheme(ResourceDictionary target, bool dark)
    {
        string bg, panel, border, input, text, subtle, swatch, hover;

        if (dark)
        {
            bg = "#1E1E1E"; panel = "#252525"; border = "#3C3C3C"; input = "#1B1B1B";
            text = "#F0F0F0"; subtle = "#9A9A9A"; swatch = "#5A5A5A"; hover = "#3A3A3A";
        }
        else
        {
            bg = "#FFFFFF"; panel = "#F7F7F7"; border = "#E0E0E0"; input = "#FFFFFF";
            text = "#1A1A1A"; subtle = "#6B6B6B"; swatch = "#C0C0C0"; hover = "#ECECEC";
        }

        target["WindowBackgroundBrush"] = Brush(bg);
        target["PanelBackgroundBrush"] = Brush(panel);
        target["PanelBorderBrush"] = Brush(border);
        target["InputBackgroundBrush"] = Brush(input);
        target["TextBrush"] = Brush(text);
        target["SubtleTextBrush"] = Brush(subtle);
        target["SwatchBorderBrush"] = Brush(swatch);
        target["ButtonHoverBackgroundBrush"] = Brush(hover);
        target["AccentBrush"] = Brush(dark ? "#4C9AFF" : "#0A84FF");

        // SettingsButtonStyle is defined in App.xaml; the ✕ button needs it to render at all.
        target["SettingsButtonStyle"] = BuildSettingsButtonStyle(dark);

        // HintTextStyle is referenced by the popup's "预设颜色" label. The popup's content is
        // parsed when the control's BAML loads, so a missing key throws at construction time
        // even though we never open the popup here.
        var hint = new Style(typeof(TextBlock));
        hint.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brush(subtle)));
        hint.Setters.Add(new Setter(TextBlock.FontSizeProperty, 11.0));
        hint.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        target["HintTextStyle"] = hint;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>
    /// Minimal stand-in for App.xaml's SettingsButtonStyle (that file is an Application resource
    /// and is not merged here). Only used so the ✕ shows a button frame in the preview.
    /// </summary>
    private static Style BuildSettingsButtonStyle(bool dark)
    {
        var style = new Style(typeof(Button));
        var border = dark ? "#3C3C3C" : "#D0D0D0";
        var hover = dark ? "#3A3A3A" : "#ECECEC";

        var template = new ControlTemplate(typeof(Button));
        var factory = new FrameworkElementFactory(typeof(Border), "Bd");
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        factory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        factory.SetValue(Border.BorderBrushProperty, Brush(border));
        factory.SetValue(Border.BackgroundProperty, Brush(dark ? "#252525" : "#FFFFFF"));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        factory.AppendChild(content);
        template.VisualTree = factory;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, Brush(hover), "Bd"));
        template.Triggers.Add(hoverTrigger);

        style.Setters.Add(new Setter(Button.TemplateProperty, template));
        style.Setters.Add(new Setter(Button.CursorProperty, System.Windows.Input.Cursors.Hand));
        return style;
    }
}
