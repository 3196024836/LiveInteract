using System.Net;
using System.Net.Sockets;
using System.Text;
using LiveInteract.Core;
using Xunit;

namespace LiveInteract.Tests;

/// <summary>实际检查本机展示路由和 SSE，不替代 OBS 或真实直播画面验收。</summary>
public sealed class OverlayTests
{
    /// <summary>只有只读展示，不存在 HTTP 控制游戏接口。</summary>
    [Fact]
    public async Task LocalOverlayIsReadonlyAndStreamsEscapedData()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await using var host = new OverlayHost(); await host.StartAsync(port);
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port) };
        var html = await client.GetStringAsync("/overlay"); Assert.Contains("textContent", html); Assert.DoesNotContain("innerHTML", html);
        using var response = await client.PostAsync("/action", new StringContent("{}", Encoding.UTF8, "application/json")); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        host.Publish(new { Enabled = true, Notice = new { Title = "<img src=x onerror=alert(1)>" } });
        using var streamResponse = await client.GetAsync("/events", HttpCompletionOption.ResponseHeadersRead);
        using var stream = await streamResponse.Content.ReadAsStreamAsync(); using var reader = new StreamReader(stream);
        var first = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)); Assert.StartsWith("data: ", first); Assert.Contains("<img", first);
    }
}
