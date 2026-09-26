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

    [Theory]
    // 网络层存活检查（HttpEndpointProbe）：拿到的是服务器真实应答，没有"错误页冒充 200"的问题
    [InlineData(200, ProbeOutcome.Ok)]
    [InlineData(204, ProbeOutcome.Ok)]
    [InlineData(299, ProbeOutcome.Ok)]
    // 401 = 服务活着、只是要 token（§4.7）——绝不能判成"没启动"
    [InlineData(401, ProbeOutcome.Other)]
    [InlineData(403, ProbeOutcome.Other)]
    [InlineData(404, ProbeOutcome.Other)]
    [InlineData(500, ProbeOutcome.Other)]
    // null = 连接失败/超时
    [InlineData(null, ProbeOutcome.Unreachable)]
    public void ClassifyHttpStatus_MapsStatusPerSpec(int? status, ProbeOutcome expected)
    {
        ProbeClassifier.ClassifyHttpStatus(status).Should().Be(expected);
    }

    [Fact]
    public void ClassifyHttpStatus_401_CountsAsRunning_NotAsDown()
    {
        // 2026-09-24 回归的核心：401 曾被当成"服务未启动"，连锁清掉归属 →
        // 状态行误报「复用外部实例」。存活检查必须把 401 当"活着"。
        var outcome = ProbeClassifier.ClassifyHttpStatus(401);

        outcome.Should().NotBe(ProbeOutcome.Unreachable);
        (outcome != ProbeOutcome.Unreachable).Should().BeTrue("401 说明端口有实例在听");
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
