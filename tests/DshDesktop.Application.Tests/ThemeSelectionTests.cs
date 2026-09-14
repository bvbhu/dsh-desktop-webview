using DshDesktop.Application;
using FluentAssertions;

namespace DshDesktop.Application.Tests;

public class ThemeSelectionTests
{
    [Theory]
    [InlineData(1, AppTheme.Light)]
    [InlineData(0, AppTheme.Dark)]
    [InlineData(null, AppTheme.Dark)] // 键/值缺失、读取被拒
    [InlineData(2, AppTheme.Dark)]    // 意外取值一律回落默认暗色
    [InlineData(-1, AppTheme.Dark)]
    public void FromAppsUseLightTheme_MapsExpected(int? appsUseLightTheme, AppTheme expected)
    {
        ThemeSelection.FromAppsUseLightTheme(appsUseLightTheme).Should().Be(expected);
    }
}
