using System.Net;
using System.Security.Cryptography;
using LiveInteract.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Xunit;

namespace LiveInteract.Tests;

/// <summary>真实 loopback HTTP 覆盖清单、跳转、下载、取消、篡改和截断，不访问公开更新源。</summary>
public sealed class UpdateDownloadTests
{
    [Fact]
    public async Task ManifestRedirectDownloadAndHashUseRealHttp()
    {
        var bytes = new byte[8193]; Random.Shared.NextBytes(bytes);
        await using var server = await LocalServer.Start(bytes);
        var info = await UpdateService.CheckAsync(server.Url + "/manifest", "0.8.0.0", default);
        Assert.NotNull(info);
        var progress = new DirectProgress();
        var zip = await UpdateService.DownloadAsync(info, progress, default);
        try
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(zip));
            Assert.Equal(1, progress.Values.Last());
        }
        finally { File.Delete(zip); }
        Assert.Null(await UpdateService.CheckAsync(server.Url + "/manifest", "0.8.1.0", default));
    }

    [Theory]
    [InlineData("bad-hash")]
    [InlineData("no-hash")]
    [InlineData("wrong-size")]
    [InlineData("oversized")]
    [InlineData("truncated")]
    [InlineData("downgrade")]
    [InlineData("loop")]
    public async Task BadPublicationCannotLeaveUsableOrPartialDownload(string failure)
    {
        var bytes = new byte[100];
        await using var server = await LocalServer.Start(bytes);
        var info = server.Info();
        info.Version = "0.8.1." + Random.Shared.Next(100000, 999999);
        if (failure == "bad-hash") { info.Sha256 = new string('F', 64); }
        if (failure == "no-hash") { info.Sha256 = ""; }
        if (failure == "wrong-size") { info.Size++; }
        if (failure == "oversized") { info.Url = server.Url + "/oversized"; }
        if (failure == "truncated") { info.Url = server.Url + "/truncated"; }
        if (failure == "downgrade") { info.Url = server.Url + "/downgrade"; }
        if (failure == "loop") { info.Url = server.Url + "/loop"; }
        await Assert.ThrowsAnyAsync<Exception>(() => UpdateService.DownloadAsync(info, null, default));
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "LiveInteract-" + info.Version + "-*.zip"));
    }

    [Fact]
    public async Task CancellationRemovesPartialFileAndConcurrentDownloadsDoNotCollide()
    {
        await using var server = await LocalServer.Start(new byte[10000]);
        var info = server.Info(); info.Url = server.Url + "/slow";
        info.Version = "0.8.1." + Random.Shared.Next(100000, 999999);
        using var cancellation = new CancellationTokenSource();
        var progress = new DirectProgress { OnValue = value => { if (value > 0) { cancellation.Cancel(); } } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateService.DownloadAsync(info, progress, cancellation.Token));
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "LiveInteract-" + info.Version + "-*.zip"));
        info.Url = server.Url + "/package";
        var results = await Task.WhenAll(UpdateService.DownloadAsync(info, null, default), UpdateService.DownloadAsync(info, null, default));
        try { Assert.NotEqual(results[0], results[1]); }
        finally { foreach (var file in results) { File.Delete(file); } }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{broken")]
    public async Task InvalidManifestReportsFailureInsteadOfLatest(string json)
    {
        await using var server = await LocalServer.Start(new byte[1], json);
        await Assert.ThrowsAnyAsync<Exception>(() => UpdateService.CheckAsync(server.Url + "/manifest", "0.8.0", default));
    }

    [Fact]
    public async Task BodyStallHasAnOverallDownloadDeadlineAfterHeaders()
    {
        await using var server = await LocalServer.Start(new byte[100]);
        var info = server.Info(); info.Url = server.Url + "/slow";
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
        await Assert.ThrowsAsync<TimeoutException>(() => UpdateService.DownloadAsync(info, null, caller.Token,
            TimeSpan.FromMilliseconds(150)));
        Assert.False(caller.IsCancellationRequested);
        Assert.True(server.SlowBodyStarted);
    }

    [Fact]
    public async Task BodyStallHasAnOverallManifestDeadlineAfterHeaders()
    {
        await using var server = await LocalServer.Start(new byte[100]);
        // 排除本地服务首次连接/JIT耗时；这里要验证的是响应头之后的正文超时。
        await UpdateService.CheckAsync(server.Url + "/manifest", "0.8.0", default);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
        await Assert.ThrowsAsync<TimeoutException>(() => UpdateService.CheckAsync(server.Url + "/slow", "0.8.0",
            caller.Token, TimeSpan.FromMilliseconds(150)));
        Assert.False(caller.IsCancellationRequested);
        Assert.True(server.SlowBodyStarted);
    }

    private sealed class DirectProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];
        public Action<double>? OnValue { get; init; }
        public void Report(double value) { Values.Add(value); OnValue?.Invoke(value); }
    }

    private sealed class LocalServer(WebApplication app, byte[] bytes, string url) : IAsyncDisposable
    {
        public string Url => url;
        public volatile bool SlowBodyStarted;
        public UpdateInfo Info() => new() { Version = "0.8.1", Url = url + "/redirect",
            Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        public static async Task<LocalServer> Start(byte[] bytes, string? manifest = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            LocalServer? server = null;
            app.MapGet("/manifest", () => Results.Text(manifest ?? JsonConvert.SerializeObject(server!.Info())));
            app.MapGet("/redirect", () => Results.Redirect("/package"));
            app.MapGet("/downgrade", () => Results.Redirect("http://example.invalid/package"));
            app.MapGet("/loop", () => Results.Redirect("/loop"));
            app.MapGet("/package", () => Results.Bytes(bytes));
            app.MapGet("/oversized", async context =>
            {
                await context.Response.StartAsync();
                await context.Response.Body.WriteAsync(new byte[bytes.Length + 1]);
            });
            app.MapGet("/truncated", async context =>
            {
                await context.Response.StartAsync();
                await context.Response.Body.WriteAsync(new byte[bytes.Length - 1]);
            });
            app.MapGet("/slow", async context =>
            {
                context.Response.ContentLength = bytes.Length;
                await context.Response.Body.WriteAsync(bytes.AsMemory(0, 1));
                await context.Response.Body.FlushAsync();
                server!.SlowBodyStarted = true;
                try { await Task.Delay(TimeSpan.FromSeconds(5), context.RequestAborted); }
                catch (OperationCanceledException) { }
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            server = new(app, bytes, address);
            return server;
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
