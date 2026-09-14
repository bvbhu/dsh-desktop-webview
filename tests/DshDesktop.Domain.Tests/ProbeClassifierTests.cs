using DshDesktop.Domain;
using FluentAssertions;

namespace DshDesktop.Domain.Tests;

/// <summary>
/// 探测分类的回归测试。钉住 2026-09-13 实测到的误判：
/// 端口无服务时 WebView2 错误页会以 200 触发 WebResourceResponseReceived，
/// 若信状态码不信导航结果，连接拒绝会被误判为在线。
/// </summary>
public class ProbeClassifierTests
{
    [Theory]
    // 导航失败 + 200：错误页污染，必须判为连接失败（本次回归的场景）
    [InlineData(false, 200, ProbeOutcome.Unreachable)]
    [InlineData(false, 204, ProbeOutcome.Unreachable)]
    [InlineData(false, 302, ProbeOutcome.Unreachable)]
    // 导航失败 + 无状态码：纯连接拒绝
    [InlineData(false, null, ProbeOutcome.Unreachable)]
    // 导航失败 + 4xx/5xx：服务器活着但报错 → 打开配置窗口（§4.2 不枚举状态码）
    [InlineData(false, 401, ProbeOutcome.Other)]
    [InlineData(false, 403, ProbeOutcome.Other)]
    [InlineData(false, 404, ProbeOutcome.Other)]
    [InlineData(false, 500, ProbeOutcome.Other)]
    [InlineData(false, 503, ProbeOutcome.Other)]
    // 导航成功：2xx 与无状态码视为在线
    [InlineData(true, null, ProbeOutcome.Ok)]
    [InlineData(true, 200, ProbeOutcome.Ok)]
    [InlineData(true, 204, ProbeOutcome.Ok)]
    [InlineData(true, 226, ProbeOutcome.Ok)]
    // 导航成功但非 2xx → Other
    [InlineData(true, 301, ProbeOutcome.Other)]
    [InlineData(true, 401, ProbeOutcome.Other)]
    [InlineData(true, 500, ProbeOutcome.Other)]
    public void ClassifyNavigation_MapsSignalsPerSpec(
        bool navSucceeded, int? status, ProbeOutcome expected)
    {
        ProbeClassifier.ClassifyNavigation(navSucceeded, status).Should().Be(expected);
    }

    [Fact]
    public void ConnectionRefused_LeadsToLaunch_NotNavigate()
    {
        // 端到端分流：连接拒绝（无状态码）+ S2 → 启动管线，而不是直接导航
        var probe = new ProbeResult(ProbeOutcome.Unreachable, null, "ERR_CONNECTION_REFUSED");
        ServiceStrategyResolver.Resolve(ServiceStrategy.ProbeThenStart, probe)
            .Should().Be(ServiceAction.Launch);
    }

    [Fact]
    public void ErrorPagePolluted200_StillLeadsToLaunch()
    {
        // 修复前的真实事故：导航失败但状态码被错误页污染成 200。
        // 分类器输出 Unreachable 后，分流必须进入启动。
        var outcome = ProbeClassifier.ClassifyNavigation(navigationSucceeded: false, statusCode: 200);
        var probe = new ProbeResult(outcome, 200, "Unknown");
        ServiceStrategyResolver.Resolve(ServiceStrategy.ProbeThenStart, probe)
            .Should().Be(ServiceAction.Launch);
    }
}
