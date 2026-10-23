using System.Reflection;
using System.Text.Json;

// 仅作为更新测试的旧/新应用，不连接直播或游戏，不读取实际用户配置。
var version = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
if (args.Length == 2 && args[0] == "--hold")
{
    var signal = Path.GetFullPath(args[1]);
    File.WriteAllText(signal + ".pid",Environment.ProcessId.ToString());
    File.WriteAllText(signal + ".ready", version);
    var until = DateTime.UtcNow.AddSeconds(30);
    while (!File.Exists(signal) && DateTime.UtcNow < until) { await Task.Delay(50); }
    return;
}
File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("LIVEINTERACT_HOME")??AppContext.BaseDirectory, "update-restarted.json"),
    JsonSerializer.Serialize(new { Version = version, Pid = Environment.ProcessId, At = DateTimeOffset.UtcNow }));
