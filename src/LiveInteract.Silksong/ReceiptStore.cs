using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using LiveInteract.Contracts;

namespace LiveInteract.Silksong;

/// <summary>游戏实际完成的回执另行落盘；重连核对不会再次修改游戏。</summary>
internal sealed class ReceiptStore
{
    private readonly string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveInteract", "GameReceipts");
    private readonly object gate = new();
    public void Save(ActionResult result)
    {
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd") + ".jsonl");
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(result) + "\n");
            using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
        }
    }
    public Task<ActionResult?> FindAsync(string id) => Task.Run(() =>
    {
        if (!Directory.Exists(directory)) { return null; }
        var needle = "\"RequestId\":" + JsonConvert.ToString(id);
        foreach (var file in Directory.GetFiles(directory, "*.jsonl").OrderByDescending(Path.GetFileName))
        {
            ActionResult? found = null;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains(needle)) { continue; }
                try { found = JsonConvert.DeserializeObject<ActionResult>(line); } catch (JsonException) { }
            }
            if (found != null) { return found; }
        }
        return null;
    });
}
