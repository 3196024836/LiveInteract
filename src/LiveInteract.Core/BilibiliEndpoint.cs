namespace LiveInteract.Core;

/// <summary>只接收哔哩哔哩弹幕长连接；用域名后缀 + 路径双重判断，不信任含域名的任意地址。</summary>
public static class BilibiliEndpoint
{
    /// <summary>接受直播网页实际使用的弹幕 WebSocket（broadcastlv / comet / chat 子域）。</summary>
    public static bool IsAllowed(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "wss" || uri.UserInfo.Length > 0) { return false; }
        var host = uri.Host;
        if (!host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase) && !host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)) { return false; }
        var isDanmakuHost = host.Contains("broadcastlv", StringComparison.OrdinalIgnoreCase) || host.Contains("comet", StringComparison.OrdinalIgnoreCase) || host.StartsWith("chat.", StringComparison.OrdinalIgnoreCase);
        return isDanmakuHost && uri.AbsolutePath.Contains("sub", StringComparison.OrdinalIgnoreCase);
    }
}
