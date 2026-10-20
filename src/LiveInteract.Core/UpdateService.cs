using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace LiveInteract.Core;

/// <summary>远端发布的版本信息；只描述版本与下载地址，不执行远端脚本。</summary>
public sealed class UpdateInfo
{
    public string Version { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>检查、下载并准备更新包；替换文件由主程序退出后的独立进程完成。</summary>
public static class UpdateService
{
    /// <summary>默认更新清单：随仓库发布的 update/latest.json。</summary>
    public const string DefaultManifest = "https://raw.githubusercontent.com/3196024836/LiveInteract/main/update/latest.json";

    /// <summary>比较两个版本号；远端更新才算有更新。</summary>
    public static bool IsNewer(string remote, string local) =>
        Version.TryParse(remote, out var remoteVersion) && Version.TryParse(local, out var localVersion) && remoteVersion > localVersion;

    /// <summary>查询远端清单；没有更新或版本不可比较时返回空。</summary>
    public static async Task<UpdateInfo?> CheckAsync(string manifestUrl, string currentVersion, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LiveInteract-Updater/1.0");
        var json = await http.GetStringAsync(manifestUrl, token);
        var info = JsonConvert.DeserializeObject<UpdateInfo>(json) ?? throw new InvalidDataException("更新信息为空");
        if (info.Url.Length == 0 || !IsNewer(info.Version, currentVersion)) { return null; }
        return info;
    }

    /// <summary>下载并校验 SHA-256；校验不通过直接丢弃，不会应用到安装目录。</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken token)
    {
        var target = Path.Combine(Path.GetTempPath(), "LiveInteract-" + info.Version + ".zip");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LiveInteract-Updater/1.0");
        using var response = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? info.Size;
        await using (var source = await response.Content.ReadAsStreamAsync(token))
        await using (var file = File.Create(target))
        {
            var buffer = new byte[81920];
            long read = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, token)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, count), token);
                read += count;
                if (total > 0) { progress?.Report(Math.Min(1, (double)read / total)); }
            }
        }
        if (info.Sha256.Length > 0)
        {
            string hash;
            await using (var stream = File.OpenRead(target)) { hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)); }
            if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);
                throw new InvalidDataException("更新包校验失败，已丢弃，没有改动任何文件。");
            }
        }
        return target;
    }

    /// <summary>把更新包解压到独立暂存目录，等待重启后复制。</summary>
    public static string Stage(string zipFile, string version, string dataDirectory)
    {
        var staging = Path.Combine(dataDirectory, "update", version);
        if (Directory.Exists(staging)) { Directory.Delete(staging, true); }
        Directory.CreateDirectory(staging);
        ZipFile.ExtractToDirectory(zipFile, staging);
        return staging;
    }

    /// <summary>更新只替换程序与载荷，不碰用户数据目录。</summary>
    public static bool IsPreserved(string relativePath) =>
        relativePath.StartsWith("WebView2Runtime", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Contains(".db", StringComparison.OrdinalIgnoreCase);
}
