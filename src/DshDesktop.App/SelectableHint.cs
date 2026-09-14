using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace DshDesktop.App;

/// <summary>
/// 一行「字段名 + 灰色说明」，<b>可选中、可复制</b>。
/// <para>
/// 为什么是 <see cref="RichTextBox"/> 派生而不是 <see cref="TextBlock"/>：
/// ① WPF 的 TextBlock 根本不能选中（<c>IsTextSelectionEnabled</c> 是 UWP 的属性，WPF 没有）；
/// ② 参考稿里"字段名 + 说明"是<b>一条内联文字流</b>，长说明折行后回到左边界。若改用
/// "标签列 + 说明列"的两列布局，折行会缩进到说明列，与参考稿肉眼可见地不一致；
/// ③ 只有富文本能同时做到"内联两种颜色 + 可选中 + 自动撑高"。
/// </para>
/// <para>
/// 自动撑高已实测：放进 <see cref="StackPanel"/> 后 Measure，两行文本得到的高度正好是两行
/// （30.48px），没有 RichTextBox 常见的"撑成固定高度"问题，也没有出现内部滚动条。
/// </para>
/// </summary>
public class SelectableHint : RichTextBox
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label), typeof(string), typeof(SelectableHint),
            new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(SelectableHint),
            new PropertyMetadata(null, OnContentChanged));

    static SelectableHint()
    {
        // RichTextBox 内部那个 ScrollViewer 会把滚轮事件标记为已处理（它默认以为自己在滚），
        // 而这里内部滚动是关的 —— 事件被吃掉后外层 ScrollViewer 收不到，表现为
        // "鼠标停在说明文字上，页面就滚不动"。页面上有十来条说明，这个死区必须堵掉。
        // handledEventsToo: true 是必需的：事件在被上层看到之前就已经是 Handled 了。
        EventManager.RegisterClassHandler(
            typeof(SelectableHint),
            MouseWheelEvent,
            new MouseWheelEventHandler(OnWheelAnyCase),
            handledEventsToo: true);
    }

    private static void OnWheelAnyCase(object sender, MouseWheelEventArgs e) => e.Handled = false;

    /// <summary>字段名（深色、半粗）。为空则不输出这一段。</summary>
    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>说明 / 示例（灰色）。<c>\n</c> 会变成真正的换行。</summary>
    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public SelectableHint()
    {
        // 外观上一律"退化"成一段普通文字：这层壳只是为了拿到选中能力。
        IsReadOnly = true;
        IsTabStop = false;          // 不占 Tab 焦点顺序，但鼠标仍可拖选
        IsDocumentEnabled = false;  // 文档里万一出现超链接也不可点
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Cursor = Cursors.IBeam;
        // RichTextBox 不吃 App.xaml 里那个隐式的 TextBlock 样式，字号得自己给
        FontSize = 12;

        Rebuild();
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SelectableHint)d).Rebuild();

    private void Rebuild()
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };

        if (!string.IsNullOrEmpty(Label))
        {
            var label = new Run(Label) { FontWeight = FontWeights.SemiBold };
            label.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            paragraph.Inlines.Add(label);
            paragraph.Inlines.Add(new Run("  "));
        }

        var lines = (Text ?? string.Empty).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                paragraph.Inlines.Add(new LineBreak());

            var run = new Run(lines[i]);
            run.SetResourceReference(TextElement.ForegroundProperty, "SubtleTextBrush");
            paragraph.Inlines.Add(run);
        }

        Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
    }
}
