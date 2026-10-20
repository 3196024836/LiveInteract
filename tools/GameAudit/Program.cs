using System.Collections.Concurrent;
using System.IO.Pipes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>本机当前用户的游戏验收服务；只接受工作区内显式写入的 JSON 请求。</summary>
internal static class Program
{
    private static readonly ConcurrentDictionary<string, JObject> commands = new();
    private static JObject latest = new();
    private static string folder = "";

    /// <summary>启动命名管道与验收请求文件循环。</summary>
    private static async Task Main(string[] args)
    {
        folder = Path.GetFullPath(args[0]); Directory.CreateDirectory(folder);
        while (!File.Exists(Path.Combine(folder, "stop")))
        {
            using var pipe = new NamedPipeServerStream("LiveInteract.Silksong.v1", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Console.WriteLine("Waiting for game; results: " + folder);
            await pipe.WaitForConnectionAsync();
            Console.WriteLine("Game connected");
            using var reader = new StreamReader(pipe, leaveOpen: true);
            var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            var receive = ReadAsync(reader);
            try
            {
                while (pipe.IsConnected && !receive.IsCompleted && !File.Exists(Path.Combine(folder, "stop")))
                {
                    foreach (var file in Directory.GetFiles(folder, "*.request.json").OrderBy(File.GetCreationTimeUtc))
                    {
                        var command = JObject.Parse(await File.ReadAllTextAsync(file));
                        var id = Path.GetFileName(file)[..^13];
                        command["_Before"] = latest.DeepClone(); commands[id] = command;
                        var kind = (string?)command["Kind"] ?? "action";
                        JObject message;
                        if (kind == "qa")
                        {
                            command["Id"] = id; message = new() { ["Kind"] = "qa", ["Text"] = command.ToString(Formatting.None) };
                        }
                        else
                        {
                            var request = (JObject)command["Request"]!; request["Id"] = id;
                            message = new() { ["Kind"] = "action", ["Request"] = request };
                        }
                        await writer.WriteLineAsync(message.ToString(Formatting.None));
                        File.Move(file, file.Replace(".request.json", ".sent.json"));
                    }
                    await Task.Delay(30);
                }
            }
            catch (IOException exception) { Console.WriteLine(exception.Message); }
            try { writer.Dispose(); } catch (IOException) { }
            pipe.Dispose();
            try { await receive; } catch (Exception exception) { Console.WriteLine(exception.Message); }
            await Task.Delay(200);
        }
    }

    /// <summary>保存游戏实际回执，不能将发出指令当作通过。</summary>
    private static async Task ReadAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            var message = JObject.Parse(line);
            if ((string?)message["Kind"] == "status")
            {
                latest = (JObject)message["Status"]!;
                await AtomicWriteAsync(Path.Combine(folder, "status.json"), latest.ToString());
            }
            else if ((string?)message["Kind"] == "result")
            {
                var result = (JObject)message["Result"]!; var id = (string)result["RequestId"]!;
                var report = new JObject { ["At"] = DateTimeOffset.Now, ["Result"] = result, ["AfterStatus"] = latest.DeepClone() };
                if (commands.TryRemove(id, out var command)) { report["Command"] = command; }
                await AtomicWriteAsync(Path.Combine(folder, id + ".result.json"), report.ToString());
                await File.AppendAllTextAsync(Path.Combine(folder, "audit-results.jsonl"), report.ToString(Formatting.None) + Environment.NewLine);
                Console.WriteLine(id + " " + result["Success"] + " " + result["Message"]?.ToString()[..Math.Min(160, result["Message"]!.ToString().Length)]);
            }
        }
    }

    /// <summary>防止其他验收程序读到半个 JSON。</summary>
    private static async Task AtomicWriteAsync(string file, string text)
    {
        await File.WriteAllTextAsync(file + ".tmp", text); File.Move(file + ".tmp", file, true);
    }
}
