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

/// <summary>本人操作登录的内置直播间窗口，只观察页面实际收到的 WebSocket 事件；抖音与哔哩哔哩共用同一套收包路径。</summary>
internal sealed class LiveSourceWindow : Window
{
    /// <summary>抖音平台标识，同时作为设置里的键。</summary>
    public const string DouyinKey = "douyin";
    /// <summary>哔哩哔哩平台标识；帧的房间标识会带上 bili: 前缀以便选择解析器。</summary>
    public const string BilibiliKey = "bilibili";

    // 独立浏览器数据目录与有界解析通道。
    private readonly WebView2 browser = new();
    private readonly TextBox address;
    private readonly CheckBox consent;
    private readonly TextBlock status;
    private readonly Button connect;
    private readonly Button installRuntime;
    private readonly ComboBox platformBox;
    private readonly ComboBoxItem douyinItem = new() { Content = "抖音", Tag = DouyinKey };
    private readonly ComboBoxItem bilibiliItem = new() { Content = "哔哩哔哩", Tag = BilibiliKey };
    private readonly string userData;
    private readonly LiveRuntime runtime;
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    // 平台消息通道已建立时的状态文本；状态栏据此显示“已连接”。
    private const string ChannelOpen = "消息通道已建立，分类数据以实际收包为准";
    private string connectionState = "尚未连接";
    private readonly HashSet<string> tracked = new();
    private readonly Dictionary<string, string> rememberedRoom = new() { [DouyinKey] = "", [BilibiliKey] = "" };

    private string platform = DouyinKey;
    private string roomId = "";
    private long packets;
    private CoreWebView2Environment? environment;

    /// <summary>状态只表示实际已发生的连接或收包情况。</summary>
    public event Action<string>? SourceStatusChanged;
    /// <summary>向主窗口保存公开直播间地址：平台标识与实际地址。</summary>
    public event Action<string, string>? SelectionChanged;
    /// <summary>本窗口暂停互动时报告简短原因，供主窗口状态显示。</summary>
    public event Action<string>? Paused;
    /// <summary>本人勾选或取消风险确认时通知主窗口记住选择。</summary>
    public event Action<bool>? ConsentChanged;

    /// <summary>建立手动登录与接入验证窗口。</summary>
    /// <param name="runtime">当前工具会话。</param>
    /// <param name="dataDirectory">当前 Windows 用户专用浏览器数据。</param>
    /// <param name="platform">上次使用的平台。</param>
    /// <param name="douyinRoom">上次填写的抖音地址。</param>
    /// <param name="bilibiliRoom">上次填写的哔哩哔哩地址。</param>
    /// <param name="consentAccepted">上次是否已勾选风险确认。</param>
    public LiveSourceWindow(LiveRuntime runtime, string dataDirectory, string platform, string douyinRoom, string bilibiliRoom, bool consentAccepted)
    {
        this.runtime = runtime; userData = dataDirectory;
        this.platform = platform == BilibiliKey ? BilibiliKey : DouyinKey;
        rememberedRoom[DouyinKey] = douyinRoom; rememberedRoom[BilibiliKey] = bilibiliRoom;
        Title = "连接直播间 · 花花直播互动"; Width = 1120; Height = 820; MinWidth = 900; MinHeight = 680; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid(); // 设置区和实际网页。
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); Content = root;
        var panel = new StackPanel { Margin = new(20, 18, 20, 15) }; // 清楚区分浏览网页与收到数据。
        panel.Children.Add(new TextBlock { Text = "连接你的直播间", FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "非官方本地接入：可能因平台变化、消息缺失或风控而失效。登录及验证由你本人完成。", Foreground = Ui.Brush("#EDBC88"), FontSize = 13, Margin = new(0, 9, 0, 5) });
        var platformRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 4, 0, 6) }; // 平台选择。
        platformRow.Children.Add(new TextBlock { Text = "直播平台", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) });
        platformBox = new ComboBox { Width = 150 }; platformBox.Items.Add(douyinItem); platformBox.Items.Add(bilibiliItem);
        platformBox.SelectedItem = this.platform == BilibiliKey ? bilibiliItem : douyinItem; platformBox.SelectionChanged += (_, _) => SwitchPlatform(); platformRow.Children.Add(platformBox);
        panel.Children.Add(platformRow);
        consent = new CheckBox { Content = "我了解上述风险，同意本次打开本人直播间并验证接入", IsChecked = consentAccepted }; panel.Children.Add(consent);
        consent.Click += (_, _) => ConsentChanged?.Invoke(consent.IsChecked == true);
        installRuntime=new Button{Content="在软件内准备浏览器组件",Visibility=Visibility.Collapsed,HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,8,0,0)};
        installRuntime.Click+=async(_,_)=>
        {
            installRuntime.IsEnabled=false;SetStatus("正在通过内置安装器准备浏览器组件…");
            try{await RuntimeComponents.EnsureWebViewAsync();installRuntime.Visibility=Visibility.Collapsed;await ConnectAsync();}
            catch(Exception error){SetStatus("组件准备未完成："+error.Message);runtime.Log("组件安装",error.Message,false);}
            finally{installRuntime.IsEnabled=true;}
        };panel.Children.Add(installRuntime);
        var row = new DockPanel { Margin = new(0, 9, 0, 0) }; // 地址栏与显式连接按钮。
        connect = new Button { Content = "打开并监听", Style = (Style)FindResource("PrimaryButton"), Margin = new(10, 0, 0, 0) }; DockPanel.SetDock(connect, Dock.Right); row.Children.Add(connect);
        var addressLabel = new TextBlock { Text = "直播间号或地址", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) }; DockPanel.SetDock(addressLabel, Dock.Left); row.Children.Add(addressLabel);
        address = new TextBox { Text = rememberedRoom[this.platform], ToolTip = "直播间短号或直播页地址" }; row.Children.Add(address); panel.Children.Add(row);
        status = new TextBlock { Text = "尚未连接。打开网页不代表弹幕、礼物和点赞均已验证。", Foreground = Ui.Brush("#A8BAAD"), Margin = new(0, 11, 0, 0), FontSize = 13 }; panel.Children.Add(status); root.Children.Add(panel);
        browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 16, 25, 35); Grid.SetRow(browser, 1); root.Children.Add(browser);
        connect.Click += async (_, _) => await ConnectAsync();
        statusTimer.Tick += (_, _) =>
        {
            if (packets == 0) { return; }
            // 主行只给分类数量，帧级计数放到提示里。
            SetStatus((connectionState == ChannelOpen ? "已连接" : connectionState) + " · 弹幕 " + runtime.CountForType(LiveEventType.Chat) + " / 礼物 " + runtime.CountForType(LiveEventType.Gift) + " / 点赞 " + runtime.CountForType(LiveEventType.Like));
            status.ToolTip = "实收 " + packets + " 帧 · 待解析 " + runtime.Journal.PendingFrames + " · 待核对帧 " + runtime.Journal.ReviewFrames;
        };
        statusTimer.Start();
        Closing += (_, e) =>
        {
            // 互动进行中关闭会暂停新触发，先由本人确认。
            if (runtime.Enabled && MessageBox.Show(this, "关闭后会暂停互动，排队保留。确定关闭？", "关闭直播间窗口", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; return; }
            RememberRoom(); statusTimer.Stop(); Pause("直播间窗口已关闭"); browser.Dispose(); SourceStatusChanged?.Invoke("直播间窗口已关闭；收到的输入和排队任务已保留");
        };
    }

    /// <summary>暂停新触发并报告原因；排队任务保留。</summary>
    /// <param name="reason">显示在主窗口状态上的简短原因。</param>
    private void Pause(string reason)
    {
        Paused?.Invoke(reason); runtime.SetEnabled(false);
    }

    /// <summary>切换平台时保存当前地址，并换成该平台上次使用的地址。</summary>
    private void SwitchPlatform()
    {
        RememberRoom();
        platform = (platformBox.SelectedItem as ComboBoxItem)?.Tag as string == BilibiliKey ? BilibiliKey : DouyinKey;
        address.Text = rememberedRoom[platform];
        address.ToolTip = platform == BilibiliKey ? "直播间号，或 live.bilibili.com/房间号 地址" : "直播间短号、电脑端地址或 v.douyin.com 分享链接";
        tracked.Clear(); roomId = ""; packets = 0; connectionState = "尚未连接";
        SetStatus(platform == BilibiliKey ? "已切到哔哩哔哩：打开自己的直播间后确认收包。" : "已切到抖音：打开自己的直播间后确认收包。");
        SelectionChanged?.Invoke(platform, address.Text);
    }

    private void RememberRoom() => rememberedRoom[platform] = address.Text;

    /// <summary>创建本用户浏览器环境，订阅协议后才导航。</summary>
    /// <returns>连接准备任务。</returns>
    private async Task ConnectAsync()
    {
        if (consent.IsChecked != true) { SetStatus("请先阅读并勾选本次接入确认。"); return; }
        if (!TryRoomAddress(address.Text, platform, out var url)) { SetStatus(platform == BilibiliKey ? "请输入直播间号或 live.bilibili.com 的直播页地址。" : "请输入直播间短号、live.douyin.com 地址或抖音分享链接，不是账号主页。"); return; }
        connect.IsEnabled = false;
        try
        {
            if (browser.CoreWebView2 == null)
            {
                var fixedRuntime = Path.Combine(App.InstallDirectory, "WebView2Runtime"); // 发行包可携带固定浏览器运行时。
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
                    var endpoint = (string?)value["url"] ?? ""; // 仅筛选当前平台自己的弹幕通道。
                    if (IsAllowedEndpoint(endpoint)) { tracked.Add((string?)value["requestId"] ?? ""); connectionState = ChannelOpen; }
                };
                browser.CoreWebView2.GetDevToolsProtocolEventReceiver("Network.webSocketFrameReceived").DevToolsProtocolEventReceived += (_, eventArgs) => ReceiveFrame(eventArgs.ParameterObjectAsJson);
                browser.CoreWebView2.GetDevToolsProtocolEventReceiver("Network.webSocketClosed").DevToolsProtocolEventReceived += (_, eventArgs) =>
                {
                    var id = (string?)JObject.Parse(eventArgs.ParameterObjectAsJson)["requestId"] ?? ""; // 已观察的连接标识。
                    if (tracked.Remove(id)) { connectionState = "消息连接已断开，已收到的任务仍保留，等待页面重连"; runtime.Log("接入断线", connectionState, false); SetStatus(connectionState); }
                };
                browser.CoreWebView2.DocumentTitleChanged += (_, _) =>
                {
                    if (browser.CoreWebView2.DocumentTitle.Contains("验证")) { Pause("平台需要本人完成验证"); connectionState = "平台需要本人完成验证，暂停新触发"; SetStatus(connectionState); }
                };
                browser.CoreWebView2.NavigationCompleted += (_, eventArgs) =>
                {
                    if (!eventArgs.IsSuccess) { SetStatus("页面打开失败：" + eventArgs.WebErrorStatus + "。请检查网络或重新连接。"); return; }
                    if (Uri.TryCreate(browser.Source?.ToString(), UriKind.Absolute, out var current))
                    {
                        var actual = RoomFrom(current, platform); // 公开房间号，不读取 Cookie。
                        if (actual.Length > 0) { roomId = actual; address.Text = current.GetLeftPart(UriPartial.Path); rememberedRoom[platform] = address.Text; SelectionChanged?.Invoke(platform, address.Text); }
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
            tracked.Clear(); connectionState = "页面已打开，等待实际消息"; roomId = RoomFrom(url, platform);
            browser.CoreWebView2.Navigate(url.AbsoluteUri);
            RememberRoom(); SelectionChanged?.Invoke(platform, url.GetLeftPart(UriPartial.Path));
            SetStatus("正在打开网页，尚未收到实际互动。礼物、点赞与弹幕会分别记录验证状态。");
        }
        catch (WebView2RuntimeNotFoundException) { installRuntime.Visibility=Visibility.Visible; SetStatus("本机缺少WebView2组件，请点上方按钮在软件内自动准备后重新连接。"); }
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
            runtime.CaptureFrame(bytes, platform == BilibiliKey ? "bili:" + roomId : roomId); // 房间前缀决定使用哪个平台解析器。
        }
        catch (Exception exception) { Pause("直播接收异常，详情见消息与队列"); connectionState = "接收无法安全落盘，禁止启用送礼互动：" + exception.Message; runtime.Log("严重接入错误", connectionState, false); SetStatus(connectionState); }
    }

    /// <summary>只接受当前平台自己的弹幕通道。</summary>
    private bool IsAllowedEndpoint(string endpoint) => platform == BilibiliKey ? BilibiliEndpoint.IsAllowed(endpoint) : DouyinEndpoint.IsAllowed(endpoint);

    /// <summary>更新状态，不把网页载入当作数据接入成功。</summary>
    /// <param name="value">实际状态。</param>
    private void SetStatus(string value) { if (status.Text == value) { return; } status.Text = value; SourceStatusChanged?.Invoke(value); }

    /// <summary>接受房间短号与当前平台的直播页地址，不接受任意外部站点。</summary>
    /// <param name="input">主播输入。</param>
    /// <param name="platform">当前平台。</param>
    /// <param name="uri">规范化地址。</param>
    /// <returns>输入符合允许范围返回真。</returns>
    private static bool TryRoomAddress(string input, string platform, out Uri uri)
    {
        input = input.Trim();
        if (platform == BilibiliKey)
        {
            if (input.Length > 0 && input.All(char.IsDigit)) { input = "https://live.bilibili.com/" + input; }
            if (Uri.TryCreate(input, UriKind.Absolute, out var bili) && bili.Scheme == "https" && bili.UserInfo.Length == 0 && bili.Host.Equals("live.bilibili.com", StringComparison.OrdinalIgnoreCase))
            {
                uri = bili; return true;
            }
            uri = null!; return false;
        }
        if (input.Length > 0 && input.All(char.IsDigit)) { input = "https://live.douyin.com/" + input; }
        if (Uri.TryCreate(input, UriKind.Absolute, out var candidate) && candidate.Scheme == "https" && candidate.UserInfo.Length == 0 &&
            (candidate.Host == "live.douyin.com" || candidate.Host == "v.douyin.com" || candidate.Host == "www.iesdouyin.com"))
        {
            uri = candidate; return true;
        }
        uri = null!; return false;
    }

    /// <summary>从地址中取出公开房间号；哔哩哔哩取最后一段数字。</summary>
    private static string RoomFrom(Uri uri, string platform)
    {
        if (platform == BilibiliKey)
        {
            foreach (var part in uri.AbsolutePath.Trim('/').Split('/').Reverse())
            {
                if (part.Length > 0 && part.All(char.IsDigit)) { return part; }
            }
            return "";
        }
        return uri.AbsolutePath.Trim('/').Split('/')[0];
    }
}
