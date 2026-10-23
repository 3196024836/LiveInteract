using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace LiveInteract.Core;

/// <summary>发布清单只描述版本、大小和校验值，不执行任何远端脚本。</summary>
public sealed class UpdateInfo
{
    public string Version { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public UpdateAsset? FullPackage { get; set; }
}

/// <summary>完整版必须有匹配的自带运行时更新包，不能被精简包静默降级。</summary>
public sealed class UpdateAsset
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>有界下载、强制校验、独立暂存；退出后的替换由 UpdateInstaller 负责。</summary>
public static class UpdateService
{
    public const string DefaultManifest = "https://raw.githubusercontent.com/3196024836/LiveInteract/main/update/latest.json";

    public static bool IsNewer(string remote, string local)
    {
        try { return UpdatePackage.ParseVersion(remote) > UpdatePackage.ParseVersion(local); }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>清单损坏明确报错，不能误报为“已是最新版本”。</summary>
    public static async Task<UpdateInfo?> CheckAsync(string manifestUrl, string currentVersion,
        CancellationToken token, TimeSpan timeout)
    {
        var caller = token;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        deadline.CancelAfter(timeout); token = deadline.Token;
        try
        {
            using var http = CreateClient(timeout);
            using var response = await GetAsync(http, manifestUrl, token);
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var memory = new MemoryStream();
            await CopyBoundedAsync(stream, memory, 64 * 1024, null, token);
            var json = Encoding.UTF8.GetString(memory.ToArray()).TrimStart('\uFEFF');
            var info = JsonConvert.DeserializeObject<UpdateInfo>(json) ?? throw new InvalidDataException("更新信息为空。");
            Validate(info);
            var current = UpdatePackage.ParseVersion(currentVersion);
            return UpdatePackage.ParseVersion(info.Version) > current ? info : null;
        }
        catch (OperationCanceledException error) when (!caller.IsCancellationRequested)
        {
            throw new TimeoutException("读取更新清单超时，请检查网络后重试；安装文件未改动。", error);
        }
    }

    /// <summary>每次下载使用独立文件；取消、网络失败和校验失败都清理半包。</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken token, TimeSpan timeout)
    {
        Validate(info);
        var caller = token;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        deadline.CancelAfter(timeout); token = deadline.Token;
        var target = Path.Combine(Path.GetTempPath(), "LiveInteract-" + info.Version + "-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using var http = CreateClient(timeout);
            using var response = await GetAsync(http, info.Url, token);
            if (response.Content.Headers.ContentLength is long length && length != info.Size)
            {
                throw new InvalidDataException("下载大小与发布清单不一致，没有改动安装文件。");
            }
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var read = await CopyBoundedAsync(source, file, info.Size,
                    bytes => progress?.Report(Math.Min(0.99, (double)bytes / info.Size)), token);
                if (read != info.Size) { throw new InvalidDataException("更新包下载不完整。"); }
            }
            string hash;
            await using (var stream = File.OpenRead(target))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            }
            if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新包 SHA-256 校验失败，已丢弃，没有改动任何安装文件。");
            }
            progress?.Report(1);
            return target;
        }
        catch (Exception error)
        {
            if (File.Exists(target)) { File.Delete(target); }
            if (error is OperationCanceledException && !caller.IsCancellationRequested)
            {
                throw new TimeoutException("下载更新包超时，半包已清理，请检查网络后重试。", error);
            }
            throw;
        }
    }

    /// <summary>旧完整版没有全局 .NET 依赖，缺少匹配包时保留原版本并明确说明。</summary>
    public static UpdateInfo ForInstallation(UpdateInfo info, string directory)
    {
        var bundled = File.Exists(Path.Combine(directory, "hostfxr.dll")) ||
            File.Exists(Path.Combine(directory, "coreclr.dll"));
        var metadata = Path.Combine(directory, "distribution.json");
        if (File.Exists(metadata))
        {
            var distribution = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(metadata));
            bundled |= distribution?.TryGetValue("SelfContained", out var value) == true && value is true;
        }
        if (!bundled) { return info; }
        var full = info.FullPackage ?? throw new InvalidDataException(
            "当前是自带运行时的完整版，但发布方只提供了精简更新包。请使用匹配的完整版更新，现有程序未改动。");
        var selected = new UpdateInfo { Version = info.Version, Notes = info.Notes,
            Url = full.Url, Sha256 = full.Sha256, Size = full.Size };
        Validate(selected);
        return selected;
    }

    public static Task<UpdateInfo?> CheckAsync(string manifestUrl, string currentVersion, CancellationToken token)
        => CheckAsync(manifestUrl, currentVersion, token, TimeSpan.FromSeconds(15));

    public static Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken token)
        => DownloadAsync(info, progress, token, TimeSpan.FromMinutes(10));

    public static string Stage(string zipFile, string version, string dataDirectory) =>
        UpdatePackage.Stage(zipFile, version, dataDirectory);
    public static bool IsPreserved(string relativePath) => UpdatePackage.IsPreserved(relativePath);

    private static void Validate(UpdateInfo info)
    {
        UpdatePackage.ParseVersion(info.Version);
        ValidateUri(info.Url);
        if (info.Size is <= 0 or > UpdatePackage.MaximumArchiveBytes || info.Sha256 == null ||
            info.Sha256.Length != 64 || info.Sha256.Any(c => !char.IsAsciiHexDigit(c)))
        {
            throw new InvalidDataException("发布清单缺少有效的包大小或 SHA-256，拒绝安装。");
        }
    }

    private static Uri ValidateUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 ||
            uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
        {
            throw new InvalidDataException("更新地址必须使用 HTTPS；本机测试允许 loopback HTTP。");
        }
        return uri;
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveInteract-Updater/2.0");
        return client;
    }

    /// <summary>逐跳检查跳转地址，GitHub 资产 CDN 正常跳转，禁止降级到远端明文链接。</summary>
    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, CancellationToken token)
    {
        var uri = ValidateUri(url);
        for (var redirect = 0; redirect < 6; redirect++)
        {
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null) { throw new InvalidDataException("更新地址跳转缺少目标。"); }
                uri = ValidateUri(new Uri(uri, location).AbsoluteUri);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("更新地址跳转次数过多。");
    }

    private static async Task<long> CopyBoundedAsync(Stream source, Stream target, long maximum,
        Action<long>? progress, CancellationToken token)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            total += count;
            if (total > maximum) { throw new InvalidDataException("下载内容超过发布大小限制。"); }
            await target.WriteAsync(buffer.AsMemory(0, count), token);
            progress?.Invoke(total);
        }
        return total;
    }
}
