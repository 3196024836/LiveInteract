using System.IO;
using System.Text;
using System.Windows.Threading;
using LiveInteract.Contracts;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json.Linq;
using LiveInteract.Core;

namespace LiveInteract.Desktop;

/// <summary>本人操作登录的内置直播间窗口，只观察页面实际收到的 WebSocket 事件。</summary>
internal sealed class DouyinWindow : Window
{
    // 独立浏览器数据目录与有界解析通道。
    private readonly WebView2 browser = new();
    private readonly TextBox address;
    private readonly CheckBox consent;
    private readonly TextBlock status;
    private readonly Button connect;
    private readonly string userData;
    private readonly LiveRuntime runtime;
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string connectionState = "尚未连接";
    private readonly HashSet<string> tracked = new();

    private string roomId = "";
    private long packets;
    private CoreWebView2Environment? environment;

    /// <summary>状态只表示实际已发生的连接或收包情况。</summary>
    public event Action<string>? SourceStatusChanged;
    /// <summary>向主窗口保存公开直播间地址，不保存凭据。</summary>
    public event Action<string>? RoomChanged;

    /// <summary>建立手动登录与接入验证窗口。</summary>
    /// <param name="runtime">当前工具会话。</param>
    /// <param name="dataDirectory">当前 Windows 用户专用浏览器数据。</param>
    /// <param name="room">上次填写的公开地址。</param>
    public DouyinWindow(LiveRuntime runtime, string dataDirectory, string room)
    {
        this.runtime = runtime; userData = dataDirectory;
        Title = "连接本人抖音直播间 · LiveInteract"; Width = 1120; Height = 820; MinWidth = 900; MinHeight = 680; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid(); // 设置区和实际网页。
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); Content = root;
        var panel = new StackPanel { Margin = new(20, 18, 20, 15) }; // 清楚区分浏览网页与收到数据。
        panel.Children.Add(new TextBlock { Text = "连接你的直播间", FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "非官方本地接入：可能因平台变化、消息缺失或风控而失效。登录及验证由你本人完成。", Foreground = Ui.Brush("#EDBC88"), FontSize = 13, Margin = new(0, 9, 0, 5) });
        consent = new CheckBox { Content = "我了解上述风险，同意本次打开本人直播间并验证接入" }; panel.Children.Add(consent);
        var row = new DockPanel { Margin = new(0, 9, 0, 0) }; // 地址栏与显式连接按钮。
        connect = new Button { Content = "打开并监听", Style = (Style)FindResource("PrimaryButton"), Margin = new(10, 0, 0, 0) }; DockPanel.SetDock(connect, Dock.Right); row.Children.Add(connect);
        address = new TextBox { Text = room, ToolTip = "直播间短号、电脑端地址或 v.douyin.com 分享链接" }; row.Children.Add(address); panel.Children.Add(row);
        status = new TextBlock { Text = "尚未连接。打开网页不代表弹幕、礼物和点赞均已验证。", Foreground = Ui.Brush("#AFC4D3"), Margin = new(0, 11, 0, 0), FontSize = 13 }; panel.Children.Add(status); root.Children.Add(panel);
        browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 16, 25, 35); Grid.SetRow(browser, 1); root.Children.Add(browser);
        connect.Click += async (_, _) => await ConnectAsync();
        statusTimer.Tick += (_, _) =>
        {
            if (packets == 0) { return; }
            SetStatus(connectionState + " · 实收 " + packets + " 帧 · 弹幕 " + runtime.CountForType(LiveEventType.Chat) + " / 礼物 " + runtime.CountForType(LiveEventType.Gift) + " / 点赞 " + runtime.CountForType(LiveEventType.Like) + " · 待解析 " + runtime.Journal.PendingFrames + " · 待核对帧 " + runtime.Journal.ReviewFrames);
        };
        statusTimer.Start();
        Closing += (_, _) => { statusTimer.Stop(); runtime.SetEnabled(false); browser.Dispose(); SourceStatusChanged?.Invoke("抖音窗口已关闭；收到的输入和排队任务已保留"); };
    }

    /// <summary>创建本用户浏览器环境，订阅协议后才导航。</summary>
    /// <returns>连接准备任务。</returns>
    private async Task ConnectAsync()
    {
        if (consent.IsChecked != true) { SetStatus("请先阅读并勾选本次接入确认。"); return; }
        if (!TryRoomAddress(address.Text, out var url)) { SetStatus("请输入直播间短号、live.douyin.com 地址或抖音分享链接，不是账号主页。"); return; }
        connect.IsEnabled = false;
        try
        {
            if (browser.CoreWebView2 == null)
            {
                var fixedRuntime = Path.Combine(AppContext.BaseDirectory, "WebView2Runtime"); // 发行包可携带固定浏览器运行时。
                var folder = Directory.Exists(fixedRuntime) ? fixedRuntime : null; // 否则使用系统已安装的 Evergreen。
                environment = await CoreWebView2Environment.CreateAsync(folder, userData);
                await browser.EnsureCoreWebView2Async(environment);
                browser.CoreWebView2!.Settings.AreDevToolsEnabled = false;
                browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
                browser.CoreWebView2.PermissionRequested += (_, eventArgs) => eventArgs.State = CoreWebView2PermissionState.Deny;
                browser.CoreWebView2.DownloadStarting += (_, eventArgs) => { eventArgs.Cancel = true; SetStatus("直播页文件下载已阻止；此窗口仅用于登录和读取互动。"); };
                browser.CoreWebView2.GetDevToolsProtocolEventReceiver("Network.webSocketCreated").DevToolsProtocolEventReceived += (_, eventArgs) =>
                {
                    var value = JObject.Parse(eventArgs.ParameterObjectAsJson); // 不记录含签名的完整网络地址。
                    var endpoint = (string?)value["url"] ?? ""; // 仅筛选平台直播 WebSocket。
                    if (DouyinEndpoint.IsAllowed(endpoint)) { tracked.Add((string?)value["requestId"] ?? ""); connectionState = "消息通道已建立，分类数据以实际收包为准"; }
                };
                browser.CoreWebView2.GetDevToolsProtocolEventReceiver("Network.webSocketFrameReceived").DevToolsProtocolEventReceived += (_, eventArgs) => ReceiveFrame(eventArgs.ParameterObjectAsJson);
                browser.CoreWebView2.GetDevToolsProtocolEventReceiver("Network.webSocketClosed").DevToolsProtocolEventReceived += (_, eventArgs) =>
                {
                    var id = (string?)JObject.Parse(eventArgs.ParameterObjectAsJson)["requestId"] ?? ""; // 已观察的连接标识。
                    if (tracked.Remove(id)) { connectionState = "消息连接已断开，已收到的任务仍保留，等待页面重连"; runtime.Log("接入断线", connectionState, false); SetStatus(connectionState); }
                };
                browser.CoreWebView2.DocumentTitleChanged += (_, _) =>
                {
                    if (browser.CoreWebView2.DocumentTitle.Contains("验证")) { runtime.SetEnabled(false); connectionState = "平台需要本人完成验证，暂停新触发"; SetStatus(connectionState); }
                };
                browser.CoreWebView2.NavigationCompleted += (_, eventArgs) =>
                {
                    if (!eventArgs.IsSuccess) { SetStatus("页面打开失败：" + eventArgs.WebErrorStatus + "。请检查网络或重新连接。"); return; }
                    if (Uri.TryCreate(browser.Source?.ToString(), UriKind.Absolute, out var current) && current.Host == "live.douyin.com")
                    {
                        var actual = current.AbsolutePath.Trim('/').Split('/')[0]; // 公开房间号，不读取 Cookie。
                        if (actual.Length > 0) { roomId = actual; RoomChanged?.Invoke(current.GetLeftPart(UriPartial.Path)); }
                    }
                    if (runtime.DecodedFrames == 0) { SetStatus("页面已打开。请本人登录并开播；等待实际互动事件。"); }
                };
                browser.CoreWebView2.NewWindowRequested += async (_, eventArgs) =>
                {
                    if (!eventArgs.IsUserInitiated || !Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var popupUri) || popupUri.Scheme != "https") { eventArgs.Handled = true; return; }
                    using var deferred = eventArgs.GetDeferral(); // 允许本人点击的网页登录弹窗。
                    var view = new WebView2(); // 使用相同独立登录环境。
                    var popup = new Window { Title = "本人完成网页登录", Width = 700, Height = 760, Owner = this, Content = view, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                    popup.Show(); await view.EnsureCoreWebView2Async(environment);
                    eventArgs.NewWindow = view.CoreWebView2; eventArgs.Handled = true; popup.Closed += (_, _) => view.Dispose();
                };
                await browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            }
            tracked.Clear(); roomId = url.AbsolutePath.Trim('/'); connectionState = "页面已打开，等待实际消息";
            browser.CoreWebView2.Navigate(url.AbsoluteUri); RoomChanged?.Invoke(url.GetLeftPart(UriPartial.Path));
            SetStatus("正在打开网页，尚未收到实际互动。礼物、点赞与弹幕会分别记录验证状态。");
        }
        catch (WebView2RuntimeNotFoundException) { SetStatus("缺少 WebView2 运行时。请使用包含 WebView2Runtime 的完整便携包，或安装微软 WebView2 Runtime。"); }
        catch (Exception exception) { SetStatus("连接未完成：" + exception.Message); runtime.Log("接入错误", exception.GetType().Name + "：" + exception.Message, false); }
        finally { connect.IsEnabled = true; }
    }

    /// <summary>只提取已经由浏览器接收的直播帧，不构造平台私有请求。</summary>
    /// <param name="json">浏览器协议回调。</param>
    private void ReceiveFrame(string json)
    {
        try
        {
            var value = JObject.Parse(json); // 当前协议事件。
            if (!tracked.Contains((string?)value["requestId"] ?? "")) { return; }
            var response = value["response"]; // WebSocket 帧内容。
            var text = (string?)response?["payloadData"] ?? ""; // 浏览器提供的 payload。
            if (text.Length > 24 * 1024 * 1024) { throw new InvalidDataException("直播帧过大，已暂停互动。"); }
            var opcode = (int?)response?["opcode"] ?? 1;
            if (opcode is 8 or 9 or 10) { return; }
            var bytes = opcode == 1 ? Encoding.UTF8.GetBytes(text) : Convert.FromBase64String(text); // 二进制帧使用 CDP 标准 base64。
            Interlocked.Increment(ref packets);
            runtime.CaptureFrame(bytes, roomId);
        }
        catch (Exception exception) { runtime.SetEnabled(false); connectionState = "接收无法安全落盘，禁止启用送礼互动：" + exception.Message; runtime.Log("严重接入错误", connectionState, false); SetStatus(connectionState); }
    }

    /// <summary>更新状态，不把网页载入当作数据接入成功。</summary>
    /// <param name="value">实际状态。</param>
    private void SetStatus(string value) { if (status.Text == value) { return; } status.Text = value; SourceStatusChanged?.Invoke(value); }

    /// <summary>接受房间短号及抖音公开分享地址，不接受任意外部站点。</summary>
    /// <param name="input">主播输入。</param>
    /// <param name="uri">规范化地址。</param>
    /// <returns>输入符合允许范围返回真。</returns>
    private static bool TryRoomAddress(string input, out Uri uri)
    {
        input = input.Trim();
        if (input.Length > 0 && input.All(char.IsDigit)) { input = "https://live.douyin.com/" + input; }
        if (Uri.TryCreate(input, UriKind.Absolute, out var candidate) && candidate.Scheme == "https" && candidate.UserInfo.Length == 0 &&
            (candidate.Host == "live.douyin.com" || candidate.Host == "v.douyin.com" || candidate.Host == "www.iesdouyin.com"))
        {
            uri = candidate; return true;
        }
        uri = null!; return false;
    }
}
