using Newtonsoft.Json;

namespace LiveInteract.Core;

/// <summary>在游戏关闭时复制完整存档；恢复前移动当前版本到可恢复目录，不删除当前数据。</summary>
public sealed class SaveBackups
{
    // 目标存档与备份的明确绝对目录。
    private readonly string saves;
    private readonly string backups;
    private readonly Func<bool> running;

    /// <summary>建立存档维护服务。</summary>
    /// <param name="saveDirectory">明确存档目录。</param>
    /// <param name="backupDirectory">备份根目录。</param>
    /// <param name="running">游戏运行状态。</param>
    public SaveBackups(string saveDirectory, string backupDirectory, Func<bool>? running = null)
    {
        saves = Path.GetFullPath(saveDirectory);
        backups = Path.GetFullPath(backupDirectory);
        this.running = running ?? GameDetector.IsRunning;
        if (backups.StartsWith(saves + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || saves.StartsWith(backups + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || saves.Equals(backups, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("存档与备份目录不能互相包含。");
        }
    }

    /// <summary>创建带目录存在状态的完整快照。</summary>
    /// <returns>快照绝对目录。</returns>
    public string Create()
    {
        RequireClosed();
        Directory.CreateDirectory(backups);
        var snapshot = Path.Combine(backups, "snapshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6]); // 独立快照。
        Directory.CreateDirectory(Path.Combine(snapshot, "data"));
        var exists = Directory.Exists(saves); // 区分游戏从未生成存档的情况。
        if (exists) { CopyTree(saves, Path.Combine(snapshot, "data")); }
        File.WriteAllText(Path.Combine(snapshot, "snapshot.json"), JsonConvert.SerializeObject(new SnapshotInfo { SourceDirectory = saves, SourceExisted = exists, CreatedAt = DateTimeOffset.Now }, Formatting.Indented));
        return snapshot;
    }

    /// <summary>恢复快照前保留当前整个存档目录，不覆盖唯一原件。</summary>
    /// <param name="snapshot">由本工具创建的快照。</param>
    /// <returns>当前存档的恢复目录。</returns>
    public string Restore(string snapshot)
    {
        RequireClosed();
        snapshot = Path.GetFullPath(snapshot);
        if (!snapshot.StartsWith(backups + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { throw new IOException("只能恢复本工具备份目录中的快照。"); }
        var info = JsonConvert.DeserializeObject<SnapshotInfo>(File.ReadAllText(Path.Combine(snapshot, "snapshot.json"))) ?? throw new InvalidDataException("快照信息缺失。"); // 快照源检查。
        if (!Path.GetFullPath(info.SourceDirectory).Equals(saves, StringComparison.OrdinalIgnoreCase)) { throw new IOException("快照不属于当前存档目录。"); }
        var staged = Path.Combine(Path.GetDirectoryName(saves)!, ".LiveInteract-restore-" + Guid.NewGuid().ToString("N")); // 同卷准备完整恢复版本。
        Directory.CreateDirectory(staged);
        CopyTree(Path.Combine(snapshot, "data"), staged);
        var recovery = Path.Combine(backups, "recovery-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6]); // 旧存档保留点。
        Directory.CreateDirectory(recovery);
        var hadCurrent = Directory.Exists(saves); // 当前目录是否存在。
        if (hadCurrent) { Directory.Move(saves, Path.Combine(recovery, "data")); }
        else { Directory.CreateDirectory(Path.Combine(recovery, "data")); }
        File.WriteAllText(Path.Combine(recovery, "snapshot.json"), JsonConvert.SerializeObject(new SnapshotInfo { SourceDirectory = saves, SourceExisted = hadCurrent, CreatedAt = DateTimeOffset.Now }, Formatting.Indented));
        try
        {
            if (info.SourceExisted) { Directory.Move(staged, saves); }
            else { Directory.Delete(staged); }
        }
        catch
        {
            if (hadCurrent && !Directory.Exists(saves)) { Directory.Move(Path.Combine(recovery, "data"), saves); }
            throw;
        }
        return recovery;
    }

    /// <summary>列出可以在界面选择的快照。</summary>
    /// <returns>最新快照优先。</returns>
    public IReadOnlyList<string> List() => Directory.Exists(backups) ? Directory.EnumerateDirectories(backups).Where(item => File.Exists(Path.Combine(item, "snapshot.json"))).OrderByDescending(item => item).ToArray() : [];

    /// <summary>禁止读取正在变化的活动存档。</summary>
    private void RequireClosed()
    {
        if (running()) { throw new InvalidOperationException("请先正常退出游戏，再备份或恢复存档。"); }
    }

    /// <summary>复制只属于明确源目录的文件，拒绝重解析跳转。</summary>
    /// <param name="source">完整源目录。</param>
    /// <param name="target">完整目标目录。</param>
    private static void CopyTree(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) { throw new IOException("存档目录包含链接，未执行复制。"); }
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) { throw new IOException("存档中含链接，未执行复制：" + Path.GetFileName(entry)); }
            var destination = ModInstaller.Inside(target, Path.GetFileName(entry)); // 目标边界检查。
            if (Directory.Exists(entry)) { Directory.CreateDirectory(destination); CopyTree(entry, destination); }
            else { File.Copy(entry, destination, false); }
        }
    }

    /// <summary>快照标识，不包含登录凭据。</summary>
    private sealed class SnapshotInfo
    {
        /// <summary>完整的原始存档目录。</summary>
        public string SourceDirectory { get; set; } = "";
        /// <summary>备份时目录是否存在。</summary>
        public bool SourceExisted { get; set; }
        /// <summary>创建时间。</summary>
        public DateTimeOffset CreatedAt { get; set; }
    }
}
