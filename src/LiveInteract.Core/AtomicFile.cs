using System.Text;

namespace LiveInteract.Core;

/// <summary>同目录独立临时文件加原子替换；并发保存不争用固定 .tmp，也不先截断唯一原件。</summary>
public static class AtomicFile
{
    private static readonly object writeGate = new();
    public static void WriteAllText(string file, string content)
    {
        // Windows 并发重命名同一目标可能返回 Access denied；串行提交，不把权限错误当成可无限重试。
        lock (writeGate)
        {
            var target = Path.GetFullPath(file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                    writer.Write(content); writer.Flush(); stream.Flush(true);
                }
                File.Move(temporary, target, true);
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        }
    }
}
