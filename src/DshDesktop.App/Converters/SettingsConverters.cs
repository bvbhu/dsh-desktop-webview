using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DshDesktop.App.Converters;

/// <summary>
/// 枚举 ↔ bool。用于把一组 RadioButton 绑到同一个枚举属性上：
/// <c>IsChecked="{Binding ServiceStrategy, Converter={StaticResource EnumBool}, ConverterParameter=ProbeThenStart}"</c>。
/// <para>
/// 比"每个取值写一个 bool 代理属性"省事，也不会出现"代理属性与枚举对不上"的静默错位。
/// ConverterParameter 必须与枚举成员名一致（顺手也算一处编译期查不到的契约，改名时要同步）。
/// </para>
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
            return false;

        return value.Equals(Enum.Parse(value.GetType(), parameter.ToString()!));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 只在"被选中"时回写：取消选中不写，否则一组单选会互相把对方清成默认值。
        if (value is true && parameter is not null)
            return Enum.Parse(targetType, parameter.ToString()!);

        return DependencyProperty.UnsetValue;
    }
}

/// <summary>非空字符串 → Visible，空 → Collapsed。用于"有校验错误才显示那条红条"。</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

/// <summary>
/// bool 取反。用于「跟随主题」勾选框与它所管辖的颜色选择器之间的联动：
/// 勾上 = 跟随主题 = 颜色选择器不可用（<c>IsEnabled=False</c>），两者恰好相反。
/// <para>
/// 只做单向 Convert：这些联动目标（IsEnabled / Visibility）从不回写源，
/// 留一个 <c>UnsetValue</c> 的 ConvertBack 反而能挡住误加的 TwoWay 绑定。
/// </para>
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

/// <summary>double → 两位小数文本。用于滑杆旁边的实时数值。</summary>
public sealed class DoubleToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? d.ToString("0.00", CultureInfo.InvariantCulture) : "0.00";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

/// <summary>
/// bool → Visible / Collapsed / Hidden。用于"开关一个选项就实时显隐一批配置项"的联动
/// （未启用拖动层就藏起拖动层配置、勾了跟随系统就藏起取色器）。
/// <para>
/// ConverterParameter（不区分大小写）：
/// <c>Invert</c> —— 取反，true → Collapsed；
/// <c>InvertHidden</c> —— 取反且用 <see cref="Visibility.Hidden"/> 而非 Collapsed。
/// Hidden 的意义：控件看不见但**占位还在**，行高不随切换变化 ——
/// 「跟随系统」勾选/取消时取色器那行不能一高一低。
/// </para>
/// <para>只做单向 Convert：Visibility 从不回写源，ConvertBack 留 UnsetValue 挡误加的 TwoWay 绑定。</para>
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is true;
        var hidden = false;
        if (parameter is string p)
        {
            if (p.Equals("Invert", StringComparison.OrdinalIgnoreCase))
                visible = !visible;
            else if (p.Equals("InvertHidden", StringComparison.OrdinalIgnoreCase))
                (visible, hidden) = (!visible, true);
        }

        if (visible)
            return Visibility.Visible;
        return hidden ? Visibility.Hidden : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
