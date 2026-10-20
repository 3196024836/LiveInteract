namespace LiveInteract.Core;

/// <summary>只接收抖音自己的直播 WebSocket；不使用包含域名的字符串来信任任意主机。</summary>
public static class DouyinEndpoint
{
    /// <summary>接受实际平台可能使用的 douyin / iesdouyin / amemv 直播消息域名。</summary>
    public static bool IsAllowed(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "wss" || uri.UserInfo.Length > 0) { return false; }
        var allowed = new[] { "douyin.com", "iesdouyin.com", "amemv.com" }.Any(domain => uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
        return allowed && (uri.Host.Contains("webcast", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.StartsWith("/webcast/", StringComparison.OrdinalIgnoreCase));
    }
}
