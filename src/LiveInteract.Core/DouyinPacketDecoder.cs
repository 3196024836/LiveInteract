using System.Globalization;
using System.IO.Compression;
using System.Text;
using Google.Protobuf;
using LiveInteract.Contracts;

namespace LiveInteract.Core;

/// <summary>解析网页已收到的 PushFrame 与事件，不签名请求、操作账号或主动发送平台消息。</summary>
public sealed class DouyinPacketDecoder
{
    // 只有增量缺失时才使用已观测的总点赞数推导，首包绝不计入历史总量。
    private readonly Dictionary<string, ulong> likeTotals = new();

    /// <summary>协议不完整或不支持时向调用方报告。</summary>
    public event Action<string>? Warning;

    /// <summary>持久保存只含房间累计值的协议状态，首包不会补算历史点赞。</summary>
    public string SaveState() => Newtonsoft.Json.JsonConvert.SerializeObject(likeTotals);
    public void LoadState(string json)
    {
        likeTotals.Clear(); if (json.Length == 0) { return; }
        foreach (var pair in Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, ulong>>(json)!) { likeTotals[pair.Key] = pair.Value; }
    }

    /// <summary>解析二进制帧，未知消息类型保留为未支持而不是伪造事件。</summary>
    /// <param name="data">浏览器收到的帧。</param>
    /// <param name="roomId">当前房间。</param>
    /// <returns>可以识别的输入事件。</returns>
    public IReadOnlyList<LiveEvent> DecodeFrame(byte[] data, string roomId, string frameKey = "")
    {
        if (data.Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("平台消息超过 16 MB，拒绝解压。");
        }
        var frame = Fields.Read(data); // 外层包。
        var payload = frame.Bytes(8).FirstOrDefault(); // PushFrame 的事件载荷。
        if (payload == null)
        {
            payload = data;
        }
        if (payload.Length > 2 && payload[0] == 0x1f && payload[1] == 0x8b)
        {
            using var compressed = new MemoryStream(payload); // 压缩输入。
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress); // 标准 gzip 解码。
            using var output = new MemoryStream(); // 有界输出缓冲。
            var buffer = new byte[8192]; // 逐块检查解压后的大小。
            int read; // 本轮读取数量。
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > 16 * 1024 * 1024)
                {
                    throw new InvalidDataException("平台消息解压后超过限制。");
                }
                output.Write(buffer, 0, read);
            }
            payload = output.ToArray();
        }
        var response = Fields.Read(payload); // 消息集合。
        var result = new List<LiveEvent>(); // 已识别的事件。
        var index = 0;
        foreach (var entryBytes in response.Bytes(1))
        {
            var entry = Fields.Read(entryBytes); // 单条消息信封。
            var method = entry.Text(1); // 平台消息名。
            var bodyBytes = entry.Bytes(2).FirstOrDefault(); // 事件二进制体。
            if (bodyBytes == null || !method.StartsWith("Webcast", StringComparison.Ordinal))
            {
                continue;
            }
            var body = Fields.Read(bodyBytes); // 事件字段集合。
            var common = body.Nested(1); // 公共元数据。
            var id = common.Number(2); // 公共消息 ID。
            if (id == 0)
            {
                id = entry.Number(3);
            }
            var trace = method == "WebcastGiftMessage" ? body.Text(35) : "";
            var missingId = id == 0 && trace.Length == 0;
            var identity = id > 0 ? id.ToString(CultureInfo.InvariantCulture) : trace.Length > 0 ? "trace:" + trace : "frame:" + (frameKey.Length > 0 ? frameKey : Guid.NewGuid().ToString("N")) + ":" + index;
            index++;
            var item = new LiveEvent { Id = method + ":" + identity, RoomId = common.Number(3) > 0 ? common.Number(3).ToString(CultureInfo.InvariantCulture) : roomId, NeedsReview = missingId, ReviewReason = missingId ? "平台没有提供消息唯一标识，已保存待核对，不能安全自动去重" : "" };
            var created = common.Number(4); // 平台 Unix 事件时间。
            if (created > 0 && created < 253402300799UL) { item.OccurredAt = DateTimeOffset.FromUnixTimeSeconds((long)created); }
            Fields user; // 身份对象由各消息结构决定。
            switch (method)
            {
                case "WebcastChatMessage":
                case "WebcastEmojiChatMessage":
                    item.Type = LiveEventType.Chat;
                    item.Text = body.Text(method == "WebcastChatMessage" ? 3 : 5);
                    user = body.Nested(2);
                    break;
                case "WebcastGiftMessage":
                    item.Type = LiveEventType.Gift;
                    item.GiftId = body.Number(2).ToString(CultureInfo.InvariantCulture);
                    item.GiftName = body.Nested(15).Text(16);
                    item.GiftUnitValue = (long)Math.Min((ulong)long.MaxValue, body.Nested(15).Number(12));
                    var repeat = Math.Max(1UL, Math.Max(body.Number(5), body.Number(6)));
                    var group = Math.Max(1UL, body.Number(4));
                    item.Count = (long)Math.Min((ulong)long.MaxValue, repeat > ulong.MaxValue / group ? ulong.MaxValue : repeat * group);
                    item.ComboEnded = body.Number(9) == 1;
                    item.ComboId = body.Number(11) == 0 ? "" : body.Number(11).ToString(CultureInfo.InvariantCulture);
                    item.IsCumulative = item.ComboId.Length > 0;
                    if (body.Nested(15).Number(10) != 0 && item.ComboId.Length == 0)
                    {
                        item.NeedsReview = true;
                        item.ReviewReason = "连击礼物缺少分组标识，不能确定本次新增量，已保留待核对";
                        Warning?.Invoke(item.ReviewReason);
                    }
                    user = body.Nested(7);
                    break;
                case "WebcastLikeMessage":
                    item.Type = LiveEventType.Like;
                    var delta = body.Number(2); // 平台提供的增量。
                    var total = body.Number(3); // 平台提供的总量。
                    if (delta == 0 && total > 0 && likeTotals.TryGetValue(item.RoomId, out var previous))
                    {
                        delta = total >= previous ? total - previous : 0;
                    }
                    if (total > 0)
                    {
                        likeTotals[item.RoomId] = total;
                    }
                    item.Count = (long)Math.Min((ulong)long.MaxValue, delta);
                    user = body.Nested(5);
                    break;
                case "WebcastMemberMessage":
                    item.Type = LiveEventType.Enter;
                    user = body.Nested(2);
                    break;
                case "WebcastSocialMessage":
                    if (body.Number(4) != 1 && body.Number(4) != 3)
                    {
                        continue;
                    }
                    item.Type = body.Number(4) == 1 ? LiveEventType.Follow : LiveEventType.Share;
                    user = body.Nested(2);
                    break;
                case "WebcastFansclubMessage":
                    item.Type = LiveEventType.FanClub;
                    item.Text = body.Text(3);
                    user = body.Nested(4);
                    break;
                default:
                    continue;
            }

            // 仅传播明确提供的用户字段；关注关系没有可靠语义时保持未知。
            item.UserId = user.Number(1) == 0 ? "" : user.Number(1).ToString(CultureInfo.InvariantCulture);
            item.Nickname = user.Text(3) is { Length: > 0 } nickname ? nickname : "观众";
            var fans = user.Nested(24).Nested(1); // 可选粉丝团数据。
            if (fans.Has(2))
            {
                item.FanLevel = (int)Math.Min(10000UL, fans.Number(2));
            }
            if (item.Type == LiveEventType.Follow)
            {
                item.IsFollower = true;
            }
            result.Add(item);
        }
        return result;
    }

    /// <summary>由 Google.Protobuf 完成有界 wire 解码；只读取已知字段，跳过其他 wire 类型。</summary>
    private sealed class Fields
    {
        // 同一编号可能是 repeated 字段。
        private readonly Dictionary<int, List<byte[]>> bytes = new();
        private readonly Dictionary<int, ulong> numbers = new();

        /// <summary>读取当前消息层，不递归展开不需要的字段。</summary>
        /// <param name="data">当前层字节。</param>
        /// <returns>字段表。</returns>
        public static Fields Read(byte[] data)
        {
            var result = new Fields(); // 当前层字段。
            using var input = new CodedInputStream(data); // 经过验证的协议读取器。
            var count = 0; // 字段数量上限。
            uint tag; // 当前 wire 标签。
            while ((tag = input.ReadTag()) != 0)
            {
                if (++count > 20000)
                {
                    throw new InvalidDataException("平台消息字段数量超过上限。");
                }
                var number = WireFormat.GetTagFieldNumber(tag); // 字段编号。
                if (WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                {
                    if (!result.bytes.TryGetValue(number, out var list))
                    {
                        list = [];
                        result.bytes[number] = list;
                    }
                    list.Add(input.ReadBytes().ToByteArray());
                }
                else if (WireFormat.GetTagWireType(tag) == WireFormat.WireType.Varint)
                {
                    result.numbers[number] = input.ReadUInt64();
                }
                else
                {
                    input.SkipLastField();
                }
            }
            return result;
        }

        /// <summary>获取重复字节字段。</summary>
        /// <param name="key">字段号。</param>
        /// <returns>该字段全部载荷。</returns>
        public IEnumerable<byte[]> Bytes(int key) => bytes.GetValueOrDefault(key) ?? Enumerable.Empty<byte[]>();

        /// <summary>读取嵌套消息。</summary>
        /// <param name="key">字段号。</param>
        /// <returns>嵌套字段表。</returns>
        public Fields Nested(int key) => Bytes(key).FirstOrDefault() is { } data ? Read(data) : new();

        /// <summary>读取 UTF-8 文本。</summary>
        /// <param name="key">字段号。</param>
        /// <returns>缺失时为空字符串。</returns>
        public string Text(int key) => Bytes(key).FirstOrDefault() is { } data ? Encoding.UTF8.GetString(data) : "";

        /// <summary>读取无符号数值。</summary>
        /// <param name="key">字段号。</param>
        /// <returns>缺失时为零。</returns>
        public ulong Number(int key) => numbers.GetValueOrDefault(key);

        /// <summary>判断数值字段是否确实存在。</summary>
        /// <param name="key">字段号。</param>
        /// <returns>字段存在返回真。</returns>
        public bool Has(int key) => numbers.ContainsKey(key);
    }
}
