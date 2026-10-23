using System.IO.Compression;

namespace LiveInteract.Core;

/// <summary>更新包的唯一入口、受保护路径和解包边界，下载与替换共用同一份规则。</summary>
public static class UpdatePackage
{
    public const string ExecutableName = "直播互动工具箱.exe";
    public const long MaximumArchiveBytes = 128L * 1024 * 1024;
    public const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private static readonly string[] PreservedDirectories =
        ["WebView2Runtime", "data", "Logs", "SaveBackups", "DouyinBrowser", "Browser", "update"];

    /// <summary>只接受规范的三/四段数字版本，不能把版本号当成任意文件路径。</summary>
    public static Version ParseVersion(string value)
    {
        if (value == null || value.Length > 64 || value.Count(c => c == '.') is < 2 or > 3 ||
            value.Any(c => c != '.' && !char.IsAsciiDigit(c)) || !Version.TryParse(value, out var result))
        {
            throw new InvalidDataException("更新版本号无效，应为三或四段数字。");
        }
        return new Version(result.Major, result.Minor, result.Build, Math.Max(0, result.Revision));
    }

    /// <summary>用户规则、数据库及登录环境不由软件更新覆盖。</summary>
    public static bool IsPreserved(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        var name = parts.Last();
        return parts.Any(p => PreservedDirectories.Contains(p, StringComparer.OrdinalIgnoreCase)) ||
            name.Contains(".db", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("settings.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("current-profile.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("profile.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("rules.json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>包只能提供程序、默认模板、已知载荷和文档，不能借更新写任意用户文件。</summary>
    public static bool IsInstallable(string relative)
    {
        if (IsPreserved(relative)) { return false; }
        var normalized = relative.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (parts.Length == 1)
        {
            return normalized.Equals(ExecutableName, StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("distribution.json", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        }
        return parts[0].Equals("payload", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("runtimes", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("第三方许可", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("直播图", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("profiles/丝之歌-弹幕助力.json", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("profiles/使用说明.md", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>拒绝路径穿越、NTFS 数据流和大小写别名，所有返回路径都在指定根目录内。</summary>
    public static string ResolveInside(string root, string relative)
    {
        var normalized = relative.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(p)))
        {
            throw new InvalidDataException("更新包包含不安全的路径：" + relative);
        }
        var absoluteRoot = Path.GetFullPath(root);
        var result = Path.GetFullPath(Path.Combine(absoluteRoot, Path.Combine(parts)));
        if (!result.StartsWith(Path.TrimEndingDirectorySeparator(absoluteRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新文件超出目标目录。");
        }
        return result;
    }

    /// <summary>拒绝更新路径中的链接/重解析点，防止间接写出安装目录。</summary>
    public static void RequireRegularPath(string file)
    {
        for (var current = Path.GetFullPath(file); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("更新路径包含链接或重解析点：" + current);
            }
        }
    }

    /// <summary>完整预检后解包到本次独立目录；支持 GitHub 发布 ZIP 的单层外包装。</summary>
    public static string Stage(string zipFile, string version, string dataDirectory)
    {
        ParseVersion(version);
        if (new FileInfo(zipFile).Length > MaximumArchiveBytes) { throw new InvalidDataException("更新包过大。"); }
        using var archive = ZipFile.OpenRead(zipFile);
        if (archive.Entries.Count is 0 or > 10000) { throw new InvalidDataException("更新包文件数量无效。"); }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/').TrimEnd('/');
            ResolveInside(dataDirectory, name);
            if (!names.Add(name)) { throw new InvalidDataException("更新包包含重复路径：" + name); }
            var kind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (kind == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("更新包不允许包含链接。");
            }
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes) { throw new InvalidDataException("更新包解压后过大。"); }
        }
        var executable = archive.Entries.Where(e => e.Length > 0 &&
            e.FullName.Replace('\\', '/').Split('/').Last().Equals(ExecutableName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (executable.Length != 1) { throw new InvalidDataException("更新包必须包含唯一的主程序。"); }
        var exeName = executable[0].FullName.Replace('\\', '/');
        var slash = exeName.LastIndexOf('/');
        var prefix = slash < 0 ? "" : exeName[..(slash + 1)];
        if (prefix.Count(c => c == '/') > 1 || archive.Entries.Any(e =>
            e.Length > 0 && !e.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("更新包目录结构不正确。");
        }
        var staging = Path.Combine(Path.GetFullPath(dataDirectory), "update", "stage-" + version + "-" + Guid.NewGuid().ToString("N"));
        RequireRegularPath(staging);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')))
            {
                var relative = entry.FullName.Replace('\\', '/')[prefix.Length..];
                var target = ResolveInside(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
            }
            return staging;
        }
        catch
        {
            Directory.Delete(staging, true);
            throw;
        }
    }

    private static bool IsDeviceName(string part)
    {
        var name = part.Split('.')[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && name[3] is >= '1' and <= '9';
    }
}
