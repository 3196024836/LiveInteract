using System.IO.Pipes;
using LiveInteract.Contracts;
using LiveInteract.Core;
using Newtonsoft.Json;
using Xunit;

namespace LiveInteract.Tests;

/// <summary>验证实际 Windows 命名管道通信；构造端不代表游戏已验证。</summary>
public sealed class PipeTests
{
    /// <summary>并发请求保持完整 JSON 行，只有回执才算执行成功。</summary>
    [Fact]
    public async Task ConcurrentRequestsWaitForMatchingReceipts()
    {
        var name = "LiveInteract.Test." + Guid.NewGuid().ToString("N"); // 不占用游戏管道。
        await using var server = new GamePipeServer(name); server.Start();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous); // 明确构造客户端。
        await client.ConnectAsync(5000);
        using var reader = new StreamReader(client, leaveOpen: true); using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonConvert.SerializeObject(new BridgeMessage { Kind = "status", Status = new() { Scene = "fixture", CanAct = true } }));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3); while (!server.IsConnected && DateTimeOffset.UtcNow < deadline) { await Task.Delay(10); }
        Assert.True(server.IsConnected);
        var requests = Enumerable.Range(0, 12).Select(_ => new ActionRequest { ActionId = "game.status" }).ToArray(); // 独立指令 ID。
        var tasks = requests.Select(request => server.ExecuteAsync(request, default)).ToArray();
        Assert.All(tasks, task => Assert.False(task.IsCompleted));
        for (var index = 0; index < requests.Length; index++)
        {
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(4)); var input = JsonConvert.DeserializeObject<BridgeMessage>(line!)!; // 实际传输后的完整行。
            Assert.Equal("action", input.Kind);
            await writer.WriteLineAsync(JsonConvert.SerializeObject(new BridgeMessage { Kind = "result", Result = new() { RequestId = input.Request!.Id, Success = true, Message = "fixture-ack" } }));
        }
        var results = await Task.WhenAll(tasks); Assert.All(results, result => Assert.True(result.Success)); Assert.Equal(12, results.Select(result => result.RequestId).Distinct().Count());
    }

    /// <summary>断线不能将未知执行结果标为成功或自动重放。</summary>
    [Fact]
    public async Task DisconnectReturnsUnknownRatherThanSuccess()
    {
        var name = "LiveInteract.Test." + Guid.NewGuid().ToString("N");
        await using var server = new GamePipeServer(name); server.Start();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        using var reader = new StreamReader(client, leaveOpen: true); using var writer = new StreamWriter(client) { AutoFlush = true };
        await writer.WriteLineAsync(JsonConvert.SerializeObject(new BridgeMessage { Kind = "status", Status = new() }));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3); while (!server.IsConnected && DateTimeOffset.UtcNow < deadline) { await Task.Delay(10); }
        var execution = server.ExecuteAsync(new() { ActionId = "game.status" }, default); // 请求已发送但未返回回执。
        Assert.NotNull(await reader.ReadLineAsync()); writer.Dispose(); client.Dispose();
        var result = await execution; Assert.False(result.Success); Assert.Contains("未知", result.Message);
    }
    /// <summary>旧Mod明确拒绝新清场指令，不能发出去后误报成功。</summary>
    [Theory]
    [InlineData("0.8.2",false)]
    [InlineData("0.8.3",true)]
    public async Task ClearSummonsChecksModAndWaitsForReceipt(string version,bool supported)
    {
        var name="LiveInteract.Test.Clear."+Guid.NewGuid().ToString("N");
        await using var server=new GamePipeServer(name);server.Start();
        using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        using var reader=new StreamReader(client,leaveOpen:true);
        using var writer=new StreamWriter(client){AutoFlush=true};
        await writer.WriteLineAsync(JsonConvert.SerializeObject(new BridgeMessage{Kind="status",Status=new(){ModVersion=version}}));
        var deadline=DateTimeOffset.UtcNow.AddSeconds(3);
        while(!server.IsConnected&&DateTimeOffset.UtcNow<deadline)await Task.Delay(10);
        var request=new ActionRequest{ActionId="game.clearSummons",Triggerer="主播"};
        var task=server.ExecuteAsync(request,default);
        if(!supported)
        {
            var rejected=await task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(rejected.Success);Assert.Equal("MOD_UPDATE_REQUIRED",rejected.ErrorCode);return;
        }
        Assert.False(task.IsCompleted);
        var message=JsonConvert.DeserializeObject<BridgeMessage>((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)))!);
        Assert.Equal("game.clearSummons",message!.Request!.ActionId);
        await writer.WriteLineAsync(JsonConvert.SerializeObject(new BridgeMessage{Kind="result",Result=new(){RequestId=request.Id,Success=true,Message="已清除友军2只、敌军1只"}}));
        var result=await task;Assert.True(result.Success);Assert.Equal(request.Id,result.RequestId);
    }
    /// <summary>复制必须使用0.9.0 Mod，旧端拒绝并保留单位等待更新。</summary>
    [Theory]
    [InlineData("0.8.7",false)]
    [InlineData("0.9.0",true)]
    public async Task CopyNextChecksModAndWaitsForReceipt(string version,bool supported)
    {
        var name="LiveInteract.Test.Copy."+Guid.NewGuid().ToString("N");
        await using var server=new GamePipeServer(name);server.Start();
        using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        using var reader=new StreamReader(client,leaveOpen:true);
        using var writer=new StreamWriter(client){AutoFlush=true};
        await writer.WriteLineAsync(JsonConvert.SerializeObject(new BridgeMessage{Kind="status",Status=new(){ModVersion=version}}));
        var deadline=DateTimeOffset.UtcNow.AddSeconds(3);
        while(!server.IsConnected&&DateTimeOffset.UtcNow<deadline)await Task.Delay(10);
        var request=new ActionRequest{ActionId="ally.copy-next",Triggerer="主播"};
        var task=server.ExecuteAsync(request,default);
        if(!supported)
        {
            var rejected=await task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(rejected.Success);Assert.Equal("MOD_UPDATE_REQUIRED",rejected.ErrorCode);return;
        }
        Assert.False(task.IsCompleted);
        var message=JsonConvert.DeserializeObject<BridgeMessage>((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)))!);
        Assert.Equal("ally.copy-next",message!.Request!.ActionId);
        await writer.WriteLineAsync(JsonConvert.SerializeObject(new BridgeMessage{Kind="result",Result=new(){RequestId=request.Id,Success=true,Message="已复制下一只合适敌人为友军"}}));
        var result=await task;Assert.True(result.Success);Assert.Equal(request.Id,result.RequestId);
    }
}
