using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using Newtonsoft.Json;
using LiveInteract.Contracts;

namespace LiveInteract.Silksong;

/// <summary>网络线程只收发纯数据；关键回执不因 256 条容量或断线清空，心跳单独合并。</summary>
internal sealed class ModPipeClient : IDisposable
{
    private readonly ConcurrentQueue<BridgeMessage> incoming = new();
    private readonly ConcurrentQueue<BridgeMessage> outgoing = new();
    private readonly Thread worker;
    private BridgeMessage? latestStatus;
    private volatile bool stopping;
    private volatile bool connected;
    private NamedPipeClientStream? current;
    private int disconnected;
    public event Action<string>? Diagnostic;
    public event Action<ActionRequest>? RequestAbandoned;
    public bool Connected => connected;

    public ModPipeClient()
    {
        worker = new Thread(Run) { IsBackground = true, Name = "LiveInteract pipe" }; worker.Start();
    }
    public bool TryRead(out BridgeMessage message) => incoming.TryDequeue(out message!);
    public bool ConsumeDisconnect() => Interlocked.Exchange(ref disconnected, 0) != 0;
    public void Send(BridgeMessage message)
    {
        if (message.Kind == "status") { Interlocked.Exchange(ref latestStatus, message); }
        else { outgoing.Enqueue(message); }
    }
    public void Dispose()
    {
        stopping = true; current?.Dispose(); worker.Join(1500);
    }
    private void Run()
    {
        while (!stopping)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "LiveInteract.Silksong.v1", PipeDirection.InOut, PipeOptions.Asynchronous);
                current = pipe; pipe.Connect(1000);
                using var reader = new StreamReader(pipe);
                var writer = new StreamWriter(pipe) { AutoFlush = true };
                try
                {
                    connected = true; var read = reader.ReadLineAsync();
                    while (!stopping && pipe.IsConnected)
                    {
                        for (var n = 0; n < 64 && outgoing.TryPeek(out var next); n++)
                        {
                            writer.WriteLine(JsonConvert.SerializeObject(next)); outgoing.TryDequeue(out _);
                        }
                        var status = Interlocked.Exchange(ref latestStatus, null);
                        if (status != null) { writer.WriteLine(JsonConvert.SerializeObject(status)); }
                        if (!read.Wait(10)) { continue; }
                        var line = read.GetAwaiter().GetResult(); if (line == null) { break; }
                        if (line.Length > 1024 * 1024) { throw new IOException("指令超过 1 MB，拒绝反序列化。"); }
                        while (!stopping && incoming.Count >= 512) { Thread.Sleep(10); }
                        var message = JsonConvert.DeserializeObject<BridgeMessage>(line, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None, MaxDepth = 24 });
                        if (message != null) { incoming.Enqueue(message); }
                        read = reader.ReadLineAsync();
                    }
                }
                finally { try { writer.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { } }
            }
            catch (Exception exception) when (exception is IOException || exception is TimeoutException || exception is JsonException || exception is ObjectDisposedException || exception is AggregateException)
            {
                if (connected) { Diagnostic?.Invoke(exception.Message); }
            }
            finally
            {
                if (connected) { Interlocked.Exchange(ref disconnected, 1); }
                connected = false; current = null;
                while (incoming.TryDequeue(out var unhandled))
                {
                    if (unhandled.Kind == "action" && unhandled.Request != null) { RequestAbandoned?.Invoke(unhandled.Request); }
                }
                // 结果队列保留；新的控制台可通过稳定指令 ID 核对，不能当作未发生直接重放。
                Interlocked.Exchange(ref latestStatus, null);
            }
            if (!stopping) { Thread.Sleep(250); }
        }
    }
}
