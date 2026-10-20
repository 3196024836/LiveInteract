using Google.Protobuf;
using LiveInteract.Contracts;
using LiveInteract.Core;
using Newtonsoft.Json;

/// <summary>构造协议输入接到真正游戏：不把它冒充抖音实播，也不允许在原始用户档运行。</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var folder = Path.GetFullPath(args[0]); Directory.CreateDirectory(folder); var report = new Dictionary<string, object?> { ["SourceIsSynthetic"] = true, ["RealDouyinTraffic"] = false, ["ActualGame"] = true };
        try
        {
            await using var runtime = new LiveRuntime(true, Path.Combine(folder, "receiver"));
            await Wait(() => runtime.Game.IsConnected, 40);
            if (runtime.Game.Status?.DiagnosticSession != "isolated-QA") { throw new InvalidOperationException("游戏没有证明测试存档已隔离，拒绝执行。"); }
            await Wait(() => runtime.Game.Status?.CanAct == true, 30);
            var before = runtime.Game.Status!.ShieldPoints;
            var profile = new RuleProfile
            {
                Rules = new[] { LiveEventType.Chat, LiveEventType.Gift, LiveEventType.Like }.Select(type => new RuleDefinition { Name = "构造输入实机验收", Enabled = true, Trigger = new() { Type = type, Threshold = 1 }, Root = new() { ActionId = "health.blue", Arguments = new() { ["value"] = "1" } } }).ToList()
            };
            runtime.ApplyProfile(profile); runtime.SetEnabled(true);
            var messages = new List<(int, object)>(); var sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (var n = 0; n < 60; n++)
            {
                var id = sequence + n; var common = Message((2, id), (3, 987654321L), (4, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                var user = Message((1, 1000L + n), (3, "构造验收观众" + n)); string method; byte[] body;
                if (n < 40) { method = "WebcastChatMessage"; body = Message((1, common), (2, user), (3, "验收")); }
                else if (n < 50) { method = "WebcastGiftMessage"; body = Message((1, common), (2, 9L), (4, 1L), (5, 3L), (6, 3L), (7, user), (9, 1L), (11, id), (15, Message((10, 1L), (16, "小心心")))); }
                else { method = "WebcastLikeMessage"; body = Message((1, common), (2, 3L), (3, 30L), (5, user)); }
                messages.Add((1, Message((1, method), (2, body), (3, id))));
            }
            var frame = Message((8, Message(messages.ToArray())));
            runtime.CaptureFrame(frame, "fixture"); runtime.CaptureFrame(frame, "fixture");
            await Wait(() => runtime.Completed == 100 && runtime.Pending == 0, 90); await Task.Delay(500);
            var after = runtime.Game.Status!.ShieldPoints; var jobs = runtime.Journal.QueryJobs();
            report["UniqueEvents"] = runtime.Received; report["ExpectedUniqueEvents"] = 60; report["ExpectedEffectUnits"] = 100; report["ConfirmedUnits"] = jobs.Sum(job => job.Succeeded); report["Pending"] = runtime.Pending; report["Failed"] = jobs.Sum(job => job.Failed); report["Unknown"] = jobs.Sum(job => job.Unknown); report["ShieldBefore"] = before; report["ShieldAfter"] = after;
            var request = new ActionRequest { Id = "dedupe-" + Guid.NewGuid().ToString("N"), ActionId = "health.blue", Arguments = new() { ["value"] = "1" }, Triggerer = "构造去重校验", EffectText = "增加临时护盾 1点" };
            var first = await runtime.Game.ExecuteAsync(request, default); var repeated = await runtime.Game.ExecuteAsync(request, default); await Task.Delay(500);
            var dedupeAfter = runtime.Game.Status!.ShieldPoints; var receipt = await runtime.Game.QueryReceiptAsync(request.Id, default);
            report["RepeatedCommandFirst"] = first; report["RepeatedCommandReplay"] = repeated; report["RepeatedCommandAppliedOnlyOnce"] = dedupeAfter == after + 1; report["ReadOnlyReceiptFound"] = receipt.Success;
            report["Passed"] = runtime.Received == 60 && jobs.Sum(job => job.Succeeded) == 100 && after == before + 100 && dedupeAfter == after + 1 && runtime.Pending == 0 && receipt.Success;
        }
        catch (Exception exception) { report["Passed"] = false; report["Error"] = exception.ToString(); }
        await File.WriteAllTextAsync(Path.Combine(folder, "actual-game-end-to-end.json"), JsonConvert.SerializeObject(report, Formatting.Indented)); Console.WriteLine(JsonConvert.SerializeObject(report, Formatting.Indented)); return report.GetValueOrDefault("Passed") is true ? 0 : 1;
    }
    private static async Task Wait(Func<bool> predicate, int seconds)
    {
        var end = DateTimeOffset.UtcNow.AddSeconds(seconds); while (!predicate() && DateTimeOffset.UtcNow < end) { await Task.Delay(50); } if (!predicate()) { throw new TimeoutException("未达到预期实际状态"); }
    }
    private static byte[] Message(params (int Key, object Value)[] fields)
    {
        using var stream = new MemoryStream(); using var writer = new CodedOutputStream(stream, true);
        foreach (var field in fields)
        {
            if (field.Value is long value) { writer.WriteTag(field.Key, WireFormat.WireType.Varint); writer.WriteInt64(value); }
            else { writer.WriteTag(field.Key, WireFormat.WireType.LengthDelimited); writer.WriteBytes(field.Value is byte[] bytes ? ByteString.CopyFrom(bytes) : ByteString.CopyFromUtf8((string)field.Value)); }
        }
        writer.Flush(); return stream.ToArray();
    }
}
