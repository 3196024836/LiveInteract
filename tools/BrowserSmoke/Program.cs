using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Windows;
using Google.Protobuf;
using LiveInteract.Contracts;
using LiveInteract.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>验证内嵌浏览器实际二进制收包与透明页渲染，只使用本机构造数据，不接触平台账号。</summary>
internal static class Program
{
    /// <summary>在独立 STA 窗口运行原生 WebView2 验证。</summary>
    /// <param name="args">输出目录和固定运行时目录。</param>
    [STAThread]
    private static void Main(string[] args)
    {
        var directory = Path.GetFullPath(args[0]); Directory.CreateDirectory(directory);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
        var window = new Window { Content = view, Width = 900, Height = 650, Left = -20000, Top = -20000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Loaded += async (_, _) =>
        {
            var result = new Dictionary<string, object?> { ["RealDouyinVerified"] = false, ["AccountUsed"] = false };
            WebApplication? fixture = null; OverlayHost? overlay = null;
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(args[1], Path.Combine(directory, "isolated-browser-data"));
                await view.EnsureCoreWebView2Async(environment);
                result["BrowserVersion"] = environment.BrowserVersionString;
                var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
                var port = FreePort(); builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port)); fixture = builder.Build(); fixture.UseWebSockets();
                var frame = Frame();
                fixture.Map("/fixture", async context =>
                {
                    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
                    using var socket = await context.WebSockets.AcceptWebSocketAsync();
                    await socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, context.RequestAborted);
                    await Task.Delay(1000);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture", context.RequestAborted);
                });
                await fixture.StartAsync();
                var received = new TaskCompletionSource<LiveEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
                var decoder = new DouyinPacketDecoder();
                view.CoreWebView2.GetDevToolsProtocolEventReceiver("Network.webSocketFrameReceived").DevToolsProtocolEventReceived += (_, e) =>
                {
                    try
                    {
                        var payload = JObject.Parse(e.ParameterObjectAsJson)["response"];
                        if ((int?)payload?["opcode"] != 2) { return; }
                        var input = decoder.DecodeFrame(Convert.FromBase64String((string)payload!["payloadData"]!), "fixture").Single(); received.TrySetResult(input);
                    }
                    catch (Exception exception) { received.TrySetException(exception); }
                };
                await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
                view.NavigateToString("<!doctype html><meta charset=utf-8><body style='background:#10222a;color:#85e1c8;font:24px sans-serif;padding:40px'>本机二进制收包验证<script>new WebSocket('ws://127.0.0.1:" + port + "/fixture')</script>");
                var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var rule = new RuleDefinition { Enabled = true, Trigger = new() { Type = LiveEventType.Chat, Keywords = ["回血"] } };
                result["NativeCdpFrameReceived"] = true; result["DecodedText"] = actual.Text; result["RuleMatched"] = new RuleEngine().Evaluate(actual, [rule]).Count == 1;
                overlay = new OverlayHost(); await overlay.StartAsync(FreePort());
                overlay.Publish(new { Enabled = true, FontSize = 24, Corner = "BottomLeft", Accent = "#79E0C8", NoticeSeconds = 30, Notice = new { At = DateTimeOffset.UtcNow, Title = "本地展示预览 · 弹幕触发补血", Detail = "这是构造展示，不代表真实直播或实际游戏。", Success = true, Simulation = true }, Effects = new[] { "移动速度 ×1.5 · 12.0s", "无限丝线 · 8.5s" }, Progress = new[] { new { Name = "累计点赞", Current = 65, Threshold = 100 } } });
                var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); view.CoreWebView2.NavigationCompleted += (_, _) => navigated.TrySetResult();
                view.CoreWebView2.Navigate(overlay.Url); await navigated.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Task.Delay(800);
                using (var file = File.Create(Path.Combine(directory, "overlay-native.png"))) { await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file); }
                result["OverlayRendered"] = true; result["Passed"] = (bool)result["RuleMatched"]!;
            }
            catch (Exception exception) { result["Passed"] = false; result["Error"] = exception.ToString(); }
            finally
            {
                view.Dispose(); if (overlay != null) { await overlay.DisposeAsync(); } if (fixture != null) { await fixture.StopAsync(); await fixture.DisposeAsync(); }
                File.WriteAllText(Path.Combine(directory, "browser-smoke.json"), JsonConvert.SerializeObject(result, Formatting.Indented)); window.Close(); application.Shutdown(result.TryGetValue("Passed", out var passed) && passed is true ? 0 : 1);
            }
        };
        window.Show(); application.Run();
    }

    /// <summary>选择本机空闲端口用于独立测试。</summary>
    /// <returns>临时端口。</returns>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var result = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return result;
    }

    /// <summary>构造包含弹幕的最小协议帧。</summary>
    /// <returns>本机测试二进制。</returns>
    private static byte[] Frame()
    {
        var common = Message((2, 8123L)); var user = Message((1, 42L), (3, "本机构造观众")); var body = Message((1, common), (2, user), (3, "回血"));
        return Message((8, Message((1, Message((1, "WebcastChatMessage"), (2, body), (3, 8123L))))));
    }

    /// <summary>使用成熟编码器构造测试字段。</summary>
    /// <param name="fields">测试字段。</param>
    /// <returns>编码字节。</returns>
    private static byte[] Message(params (int Key, object Value)[] fields)
    {
        using var stream = new MemoryStream(); using var writer = new CodedOutputStream(stream, true);
        foreach (var field in fields)
        {
            if (field.Value is long number) { writer.WriteTag(field.Key, WireFormat.WireType.Varint); writer.WriteInt64(number); }
            else { writer.WriteTag(field.Key, WireFormat.WireType.LengthDelimited); writer.WriteBytes(field.Value is byte[] bytes ? ByteString.CopyFrom(bytes) : ByteString.CopyFromUtf8((string)field.Value)); }
        }
        writer.Flush(); return stream.ToArray();
    }
}
