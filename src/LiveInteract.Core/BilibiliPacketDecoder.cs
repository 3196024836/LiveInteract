using System.IO;
using System.IO.Compression;
using System.Text;
using LiveInteract.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiveInteract.Core;

/// <summary>解析哔哩哔哩网页已经收到的弹幕长连接数据包；只读不发送，不认识的消息不伪造事件。</summary>
public sealed class BilibiliPacketDecoder
{
    /// <summary>单个数据包与解压后的上限，防止压缩炸弹。</summary>
    private const int MaxPacket = 16 * 1024 * 1024;
    /// <summary>只记住房间点赞总量用于增量；首次出现的总量直接作为基线，不补算历史。</summary>
    private readonly Dictionary<string, long> likeTotals = new();

    /// <summary>协议不完整时向调用方报告。</summary>
    public event Action<string>? Warning;

    public string SaveState() => JsonConvert.SerializeObject(likeTotals);

    public void LoadState(string json)
    {
        likeTotals.Clear();
        if (json.Length == 0) { return; }
        foreach (var pair in JsonConvert.DeserializeObject<Dictionary<string, long>>(json)!) { likeTotals[pair.Key] = pair.Value; }
    }

    /// <summary>解析一个浏览器收到的帧，返回可识别的互动事件。</summary>
    /// <param name="data">原始帧字节。</param>
    /// <param name="roomId">房间标识，可带 bili: 前缀。</param>
    /// <param name="frameKey">帧标识，用于没有平台 id 的消息去重。</param>
    public IReadOnlyList<LiveEvent> DecodeFrame(byte[] data, string roomId, string frameKey = "")
    {
        var room = roomId.StartsWith("bili:", StringComparison.Ordinal) ? roomId[5..] : roomId;
        var result = new List<LiveEvent>();
        Walk(data, room, frameKey, result, 0);
        return result;
    }

    /// <summary>按 16 字节头逐包处理；压缩包递归展开，嵌套过深直接放弃。</summary>
    private void Walk(byte[] data, string room, string frameKey, List<LiveEvent> result, int depth)
    {
        if (depth > 4) { Warning?.Invoke("弹幕包嵌套过深，已跳过不处理"); return; }
        if (data.Length > MaxPacket) { throw new InvalidDataException("平台消息超过 16 MB，拒绝处理。"); }
        var offset = 0;
        while (offset + 16 <= data.Length)
        {
            var total = ReadUInt32(data, offset);
            var headerLength = ReadUInt16(data, offset + 4);
            var version = ReadUInt16(data, offset + 6);
            var operation = ReadUInt32(data, offset + 8);
            if (headerLength < 16 || total < headerLength || offset + total > data.Length) { return; }
            var body = new byte[total - headerLength];
            Buffer.BlockCopy(data, offset + headerLength, body, 0, body.Length);
            offset += (int)total;
            switch (version)
            {
                case 2: Walk(Inflate(body, false), room, frameKey, result, depth + 1); break;
                case 3: Walk(Inflate(body, true), room, frameKey, result, depth + 1); break;
                default:
                    if (operation is 5 or 7 or 8) { Parse(Encoding.UTF8.GetString(body), room, frameKey, result); }
                    break;
            }
        }
    }

    /// <summary>zlib（协议 2）或 brotli（协议 3），解压结果仍受同一上限约束。</summary>
    private static byte[] Inflate(byte[] body, bool brotli)
    {
        using var input = new MemoryStream(body);
        using Stream stream = brotli ? new BrotliStream(input, CompressionMode.Decompress) : new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaxPacket) { throw new InvalidDataException("平台消息解压后超过限制。"); }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    /// <summary>解析命令 JSON；只处理明确认识的互动命令。</summary>
    private void Parse(string json, string room, string frameKey, List<LiveEvent> result)
    {
        if (json.Length == 0) { return; }
        JObject root;
        try { root = JObject.Parse(json); }
        catch (JsonException) { return; }
        var command = Str(root["cmd"]);
        var index = result.Count;
        LiveEvent item;
        switch (command)
        {
            case "DANMU_MSG":
                {
                    var info = root["info"] as JArray;
                    if (info == null || info.Count < 3) { return; }
                    var user = info[2] as JArray;
                    item = new LiveEvent
                    {
                        Type = LiveEventType.Chat,
                        Text = Str(info[1]),
                        UserId = user is { Count: > 0 } ? Str(user[0]) : "",
                        Nickname = user is { Count: > 1 } && Str(user[1]).Length > 0 ? Str(user[1]) : "观众"
                    };
                    break;
                }
            case "SEND_GIFT":
                {
                    var data = root["data"];
                    if (data == null) { return; }
                    var name = Str(data["giftName"]);
                    if (name.Length == 0) { return; }
                    var count = Math.Max(1, (long?)data["num"] ?? 1);
                    // 金瓜子单价只有在平台明确给出 gold 时才可信；免费/银瓜子礼物保持 0。
                    var price = string.Equals(Str(data["coin_type"]), "gold", StringComparison.OrdinalIgnoreCase) ? (long?)data["price"] ?? 0 : 0;
                    item = new LiveEvent
                    {
                        Type = LiveEventType.Gift,
                        GiftId = Str(data["tid"]),
                        GiftName = name,
                        GiftUnitValue = Math.Max(0, price),
                        Count = count,
                        ComboTotal = count,
                        ComboEnded = true,
                        UserId = Str(data["uid"]),
                        Nickname = Str(data["uname"]).Length > 0 ? Str(data["uname"]) : "观众"
                    };
                    break;
                }
            case "LIKE_INFO_V3_CLICK":
                // 单次点击不带累计值；点赞统一按下面的房间总量增量结算，避免与 CLICK 重复计数。
                return;
            case "LIKE_INFO_V3_UPDATE":
                {
                    var data = root["data"];
                    var total = (long?)data?["click_count"] ?? 0;
                    if (total <= 0) { return; }
                    if (!likeTotals.TryGetValue(room, out var previous)) { likeTotals[room] = total; return; }
                    var delta = total - previous;
                    likeTotals[room] = total;
                    if (delta <= 0) { return; }
                    item = new LiveEvent { Type = LiveEventType.Like, Count = delta, ComboTotal = total, ComboEnded = true, Nickname = "观众" };
                    break;
                }
            case "INTERACT_WORD":
                {
                    var data = root["data"];
                    if (data == null) { return; }
                    var kind = (int?)data["msg_type"] ?? 0;
                    var type = kind switch { 1 => LiveEventType.Enter, 2 => LiveEventType.Follow, 3 => LiveEventType.Share, _ => LiveEventType.Enter };
                    item = new LiveEvent
                    {
                        Type = type,
                        UserId = Str(data["uid"]),
                        Nickname = Str(data["uname"]).Length > 0 ? Str(data["uname"]) : "观众",
                        IsFollower = type == LiveEventType.Follow ? true : null
                    };
                    break;
                }
            case "SUPER_CHAT_MESSAGE":
                {
                    var data = root["data"];
                    if (data == null) { return; }
                    var user = data["user_info"];
                    item = new LiveEvent
                    {
                        Type = LiveEventType.Chat,
                        Text = Str(data["message"]),
                        UserId = Str(user?["uid"]),
                        Nickname = Str(user?["uname"]).Length > 0 ? Str(user?["uname"]) : "观众"
                    };
                    break;
                }
            default:
                return;
        }
        item.RoomId = room;
        item.Id = "bili:" + command + ":" + (frameKey.Length > 0 ? frameKey : "frame") + ":" + index;
        result.Add(item);
    }

    private static string Str(JToken? token) => token == null || token.Type == JTokenType.Null ? "" : token.Type == JTokenType.String ? token.Value<string>() ?? "" : token.ToString();

    private static int ReadUInt16(byte[] data, int offset) => data[offset] << 8 | data[offset + 1];

    private static uint ReadUInt32(byte[] data, int offset) => (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
}
