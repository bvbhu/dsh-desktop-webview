using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DshDesktop.Domain;
using DshDesktop.Infrastructure;
using FluentAssertions;

namespace DshDesktop.Infrastructure.Tests;

/// <summary>
/// <see cref="HttpEndpointProbe"/> 的测试。
/// <para>
/// 这是 2026-09-24「页面反复刷新」修复的关键一环：存活检查必须走网络、**不碰 WebView**。
/// 这里用裸 <see cref="TcpListener"/> 起一个只会回一行状态行的小服务，
/// 好处是完全可控且不需要 URL ACL（<c>HttpListener</c> 在 Windows 上常需要管理员预留）。
/// </para>
/// </summary>
public class HttpEndpointProbeTests
{
    /// <summary>起一个监听回环端口、固定回一个状态码的最小 HTTP 服务。</summary>
    private sealed class StubHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _statusCode;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public StubHttpServer(int statusCode)
        {
            _statusCode = statusCode;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public string Url => $"http://127.0.0.1:{Port}/";

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return; // 已停止
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            // 读掉请求头（读到空行为止），再回状态行。
                            var stream = client.GetStream();
                            var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                            while (true)
                            {
                                var line = await reader.ReadLineAsync();
                                if (line is null || line.Length == 0)
                                    break;
                            }

                            var body = Encoding.ASCII.GetBytes($"HTTP/1.1 {_statusCode} Stub\r\n"
                                + "Content-Length: 0\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(body);
                            await stream.FlushAsync();
                        }
                        catch
                        {
                            // 客户端提前断开：忽略
                        }
                    }
                });
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* 已停 */ }
            _cts.Dispose();
        }
    }

    /// <summary>找一个当前没人监听的端口（连接必然被拒）。</summary>
    private static int GetClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task Ok_WhenServerReturns200()
    {
        using var server = new StubHttpServer(200);
        using var probe = new HttpEndpointProbe();

        var result = await probe.ProbeAsync(server.Url, CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.Ok);
        result.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Other_WhenServerReturns401()
    {
        // 401 = 服务活着、只是要 token（§4.7）。这是"服务是否在跑"必须用的判定。
        using var server = new StubHttpServer(401);
        using var probe = new HttpEndpointProbe();

        var result = await probe.ProbeAsync(server.Url, CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.Other);
        result.StatusCode.Should().Be(401);
        result.Outcome.Should().NotBe(ProbeOutcome.Unreachable);
    }

    [Fact]
    public async Task Unreachable_WhenNothingIsListening()
    {
        var url = $"http://127.0.0.1:{GetClosedPort()}/";
        using var probe = new HttpEndpointProbe();

        var result = await probe.ProbeAsync(url, CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.Unreachable);
        result.StatusCode.Should().BeNull();
    }

    [Fact]
    public async Task Unreachable_WhenUrlIsNotParseable()
    {
        using var probe = new HttpEndpointProbe();

        var result = await probe.ProbeAsync("not a url", CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.Unreachable);
    }

    [Fact]
    public async Task CallerCancellation_Propagates_NotReportedAsDown()
    {
        // 调用方取消不能被伪装成"服务没起来"——那会让上层误判并去启动服务。
        using var server = new StubHttpServer(200);
        using var probe = new HttpEndpointProbe();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await probe.ProbeAsync(server.Url, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task InjectedHttpClient_IsNotDisposedByProbe()
    {
        // 外部注入的 HttpClient 归调用方所有：probe.Dispose() 不能把它毒掉。
        using var server = new StubHttpServer(200);
        var shared = new HttpClient();
        var probe = new HttpEndpointProbe(shared);

        probe.Dispose();

        var act = async () => await shared.GetAsync(server.Url);
        await act.Should().NotThrowAsync("注入的 HttpClient 不该被 probe 释放");
        shared.Dispose();
    }
}
