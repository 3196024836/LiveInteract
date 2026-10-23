using System.Collections.Concurrent;
using System.IO.Pipes;
using LiveInteract.Contracts;
using Newtonsoft.Json;

namespace LiveInteract.Core;

/// <summary>仅向当前 Windows 用户开放的 JSONL 游戏桥，收到执行回执后才报告成功。</summary>
public sealed class GamePipeServer : IGameAdapter, IAsyncDisposable
{
    /// <summary>与专用 Mod 共用的本机管道名。</summary>
    public const string PipeName = "LiveInteract.Silksong.v1";
    // 未返回结果的指令及互斥写入器。
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ActionResult>> pending = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private StreamWriter? writer;
    private Task? worker;
    private readonly string pipeName;
    private DateTimeOffset lastSeen;

    /// <summary>最近心跳仍有效时连接可用。</summary>
    public bool IsConnected => writer != null && Status?.ProtocolVersion == 1 && DateTimeOffset.UtcNow - lastSeen < TimeSpan.FromSeconds(5);
    /// <summary>游戏最近上报的真实状态。</summary>
    public GameStatus? Status { get; private set; }
    /// <summary>游戏心跳事件。</summary>
    public event Action<GameStatus>? StatusChanged;
    /// <summary>连接与协议诊断。</summary>
    public event Action<string>? Diagnostic;

    /// <summary>建立桥接器，测试可使用独立管道而不占用真实游戏。</summary>
    /// <param name="pipeName">可选的独立测试管道名。</param>
    public GamePipeServer(string? pipeName = null) => this.pipeName = pipeName ?? PipeName;

    /// <summary>开始等待游戏连接，不启动游戏或修改文件。</summary>
    public void Start() => worker ??= Task.Run(ListenAsync);

    /// <summary>发送单次指令并等待实际回执。</summary>
    /// <param name="request">待执行指令。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>执行结果。</returns>
    public async Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        var errors = ActionValidation.Validate(request); // 防止界面以外的调用绕过校验。
        if (errors.Count > 0 || !IsConnected)
        {
            return new ActionResult { RequestId = request.Id, ErrorCode = errors.Count > 0 ? "VALIDATION" : "NOT_CONNECTED", Retryable = errors.Count == 0, Message = errors.Count > 0 ? string.Join("；", errors) : "等待游戏连接 / 新心跳，任务仍保留在队列。" };
        }
        var needs06=request.ActionId.StartsWith("summon.",StringComparison.Ordinal)||request.ActionId=="currency.rain"||request.ActionId=="ally.native.understore-thrower"||(request.DurationSeconds==0&&request.ActionId is "enemy.growStack" or "enemy.shrinkStack");
        var needs08=request.Arguments.ContainsKey("faction")||MonsterCatalog.Find(request.ActionId)?.SourceScene.Length>0||request.ActionId is "summon.named" or "summon.random";
        var required=request.ActionId=="ally.copy-next"?new Version(0,9,0):request.ActionId=="game.clearSummons"?new Version(0,8,3):needs08?new Version(0,8,0):needs06?new Version(0,6,0):request.ActionId.StartsWith("ally.native.",StringComparison.Ordinal)?new Version(0,5,0):null;
        if(required!=null&&(!Version.TryParse(Status?.ModVersion,out var installed)||installed<required))
            return new ActionResult{RequestId=request.Id,ErrorCode="MOD_UPDATE_REQUIRED",Retryable=true,Message="当前游戏Mod版本不支持此效果。请正常退出游戏，在Mod与存档安装/修复"+required+"再启动；本单位尚未发送，仍保留排队。"};
        var completion = new TaskCompletionSource<ActionResult>(TaskCreationOptions.RunContinuationsAsynchronously); // 唯一回执槽。
        if (!pending.TryAdd(request.Id, completion))
        {
            return Failure(request.Id, "相同指令正在执行，不会重复发送。");
        }
        try
        {
            await SendAsync(new() { Kind = "action", Request = request }, cancellationToken);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (TimeoutException)
        {
            return Failure(request.Id, "等待游戏回执超时，结果未知；不会自动重发。");
        }
        catch (IOException exception)
        {
            return Failure(request.Id, "游戏连接断开，结果未知：" + exception.Message);
        }
        finally
        {
            pending.TryRemove(request.Id, out _);
        }
    }

    /// <summary>读取指定指令已完成的回执，不会再次触发效果。</summary>
    public async Task<ActionResult> QueryReceiptAsync(string id, CancellationToken token)
    {
        if (!IsConnected) { return new() { RequestId = id, ErrorCode = "NOT_CONNECTED", Message = "游戏未连接，暂时无法核对" }; }
        var completion = new TaskCompletionSource<ActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) { return new() { RequestId = id, ErrorCode = "UNKNOWN", Message = "该指令仍在等待回执" }; }
        try
        {
            await SendAsync(new() { Kind = "receipt", Text = id }, token);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return new() { RequestId = id, ErrorCode = "UNKNOWN", Message = "核对未完成：" + exception.Message }; }
        finally { pending.TryRemove(id, out _); }
    }

    /// <summary>发送紧急恢复命令。</summary>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>发送完成任务。</returns>
    public Task ResetAsync(CancellationToken cancellationToken) => SendAsync(new() { Kind = "reset" }, cancellationToken);

    /// <summary>应用游戏内提示设置。</summary>
    /// <param name="options">设置 JSON。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>发送完成任务。</returns>
    public Task ConfigureAsync(string options, CancellationToken cancellationToken = default) => SendAsync(new() { Kind = "settings", Text = options }, cancellationToken);

    /// <summary>停止桥接并解除所有等待。</summary>
    /// <returns>清理任务。</returns>
    public async ValueTask DisposeAsync()
    {
        if (writer != null)
        {
            try
            {
                await ResetAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or ObjectDisposedException)
            {
                Diagnostic?.Invoke("退出时连接已关闭：" + exception.Message);
            }
        }
        shutdown.Cancel();
        if (worker != null)
        {
            await worker;
        }
        shutdown.Dispose();
        writeGate.Dispose();
    }

    /// <summary>等待 Mod 连接并读取消息。</summary>
    /// <returns>监听任务。</returns>
    private async Task ListenAsync()
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); // 仅当前用户可连接。
                await pipe.WaitForConnectionAsync(shutdown.Token);
                using var reader = new StreamReader(pipe, leaveOpen: true); // 一行一条消息。
                using var currentWriter = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true }; // 共享受控写入器。
                writer = currentWriter;
                Diagnostic?.Invoke("游戏桥已连接，等待状态心跳。");
                while (!shutdown.IsCancellationRequested && await reader.ReadLineAsync(shutdown.Token) is { } line)
                {
                    if (line.Length > 1024 * 1024)
                    {
                        throw new InvalidDataException("游戏消息超过 1 MB。");
                    }
                    var message = JsonConvert.DeserializeObject<BridgeMessage>(line, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None, MaxDepth = 24 }); // 固定 DTO 反序列化。
                    if (message?.Kind == "status" && message.Status is { } status)
                    {
                        if (status.ProtocolVersion != 1 || status.GameId != "silksong")
                        {
                            throw new InvalidDataException("游戏桥版本不匹配，请重新准备游戏 Mod。");
                        }
                        Status = status;
                        lastSeen = DateTimeOffset.UtcNow;
                        StatusChanged?.Invoke(status);
                    }
                    else if (message?.Kind == "diagnostic") { Diagnostic?.Invoke("游戏端错误：" + message.Text); }
                    else if (message?.Kind == "result" && message.Result is { } result && pending.TryGetValue(result.RequestId, out var completion))
                    {
                        completion.TrySetResult(result);
                    }
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Diagnostic?.Invoke("游戏桥：" + exception.Message);
            }
            finally
            {
                writer = null;
                lastSeen = default;
                foreach (var pair in pending)
                {
                    pair.Value.TrySetResult(Failure(pair.Key, "连接已断开，指令结果未知；不会自动重放。"));
                }
            }
            if (!shutdown.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>串行写入信封，避免并发动作交错 JSON。</summary>
    /// <param name="message">信封。</param>
    /// <param name="token">取消信号。</param>
    /// <returns>写入任务。</returns>
    private async Task SendAsync(BridgeMessage message, CancellationToken token)
    {
        await writeGate.WaitAsync(token);
        try
        {
            var current = writer ?? throw new IOException("游戏尚未连接。"); // 锁内取得有效写入器。
            await current.WriteLineAsync(JsonConvert.SerializeObject(message).AsMemory(), token);
            await current.FlushAsync(token);
        }
        catch (ObjectDisposedException exception)
        {
            throw new IOException("游戏管道已关闭，结果未知。", exception);
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>建立明确失败的回执。</summary>
    /// <param name="id">指令标识。</param>
    /// <param name="message">失败原因。</param>
    /// <returns>失败回执。</returns>
    private static ActionResult Failure(string id, string message) => new() { RequestId = id, Message = message, ErrorCode = message.Contains("未知", StringComparison.Ordinal) ? "UNKNOWN" : "TRANSPORT" };
}
