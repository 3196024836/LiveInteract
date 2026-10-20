using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace LiveInteract.Core;

/// <summary>仅监听回环地址的只读 OBS 展示服务，不提供游戏控制端点。</summary>
public sealed class OverlayHost : IAsyncDisposable
{
    // 当前展示状态和独立 SSE 客户端。
    private readonly ConcurrentDictionary<Guid, System.Threading.Channels.Channel<string>> clients = new();
    private WebApplication? application;
    private string latest = "{}";
    /// <summary>浏览器源地址。</summary>
    public string Url { get; private set; } = "";

    /// <summary>启动本机只读服务。</summary>
    /// <param name="port">固定本机端口。</param>
    /// <returns>启动任务。</returns>
    public async Task StartAsync(int port = 17573)
    {
        var builder = WebApplication.CreateSlimBuilder(); // 无额外配置文件与外部服务。
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        application = builder.Build();
        application.MapGet("/overlay", async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'none'";
            using var resource = typeof(OverlayHost).Assembly.GetManifestResourceStream("LiveInteract.Core.overlay.html"); // 内置展示页。
            await resource!.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        application.MapGet("/health", () => Results.Text("LiveInteract readonly overlay ready"));
        application.MapGet("/events", async context =>
        {
            if (clients.Count >= 10) { context.Response.StatusCode = 429; return; }
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";
            var id = Guid.NewGuid(); // 当前独立客户端。
            var channel = System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(20) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest }); // 慢客户端不阻塞游戏。
            clients[id] = channel;
            channel.Writer.TryWrite(latest);
            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(context.RequestAborted))
                {
                    await context.Response.WriteAsync("data: " + item + "\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            finally { clients.TryRemove(id, out _); }
        });
        await application.StartAsync();
        Url = "http://127.0.0.1:" + port + "/overlay";
    }

    /// <summary>广播已经脱敏的展示数据。</summary>
    /// <param name="state">纯展示状态。</param>
    public void Publish(object state)
    {
        latest = JsonConvert.SerializeObject(state);
        foreach (var channel in clients.Values) { channel.Writer.TryWrite(latest); }
    }

    /// <summary>关闭本机监听。</summary>
    /// <returns>关闭任务。</returns>
    public async ValueTask DisposeAsync()
    {
        foreach (var channel in clients.Values) { channel.Writer.TryComplete(); }
        if (application != null) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); try { await application.StopAsync(timeout.Token); } catch (OperationCanceledException) { } await application.DisposeAsync(); }
    }
}
