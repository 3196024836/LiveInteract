using System.IO.Compression;
using System.Text;
using LiveInteract.Contracts;
using LiveInteract.Core;
using Xunit;
namespace LiveInteract.Tests;

/// <summary>哔哩哔哩弹幕长连接解析：包结构、压缩批次、点赞增量与礼物单价的回归基线。</summary>
public sealed class BilibiliDecoderTests
{
    private static byte[] Packet(string json, ushort version, uint operation = 5)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var total = 16 + body.Length;
        var result = new byte[total];
        result[0] = (byte)(total >> 24); result[1] = (byte)(total >> 16); result[2] = (byte)(total >> 8); result[3] = (byte)total;
        result[5] = 16;
        result[6] = (byte)(version >> 8); result[7] = (byte)version;
        result[11] = (byte)operation;
        result[15] = 1;
        Buffer.BlockCopy(body, 0, result, 16, body.Length);
        return result;
    }

    private static byte[] Compressed(string json, bool brotli)
    {
        var inner = Packet(json, 0);
        using var output = new MemoryStream();
        using (Stream compressor = brotli ? new BrotliStream(output, CompressionLevel.Optimal, true) : new ZLibStream(output, CompressionLevel.Optimal, true)) { compressor.Write(inner); }
        var payload = output.ToArray();
        var frame = new byte[16 + payload.Length];
        var total = frame.Length;
        frame[0] = (byte)(total >> 24); frame[1] = (byte)(total >> 16); frame[2] = (byte)(total >> 8); frame[3] = (byte)total;
        frame[5] = 16; frame[6] = 0; frame[7] = brotli ? (byte)3 : (byte)2;
        frame[11] = 5; frame[15] = 1;
        Buffer.BlockCopy(payload, 0, frame, 16, payload.Length);
        return frame;
    }

    [Fact] public void OnlyBilibiliDanmakuHostsAreAccepted()
    {
        Assert.True(BilibiliEndpoint.IsAllowed("wss://broadcastlv.chat.bilibili.com/sub"));
        Assert.True(BilibiliEndpoint.IsAllowed("wss://tx-bj-live-comet-02.chat.bilibili.com/sub"));
        Assert.False(BilibiliEndpoint.IsAllowed("wss://ws-api.bilibili.com/x"));
        Assert.False(BilibiliEndpoint.IsAllowed("wss://evil.com/sub"));
        Assert.False(BilibiliEndpoint.IsAllowed("ws://broadcastlv.chat.bilibili.com/sub")); // 只接受加密连接。
        Assert.False(BilibiliEndpoint.IsAllowed("wss://user@broadcastlv.chat.bilibili.com/sub"));
    }

    [Fact] public void PlainDanmakuBecomesChatEvent()
    {
        var frame = Packet("{\"cmd\":\"DANMU_MSG\",\"info\":[[],\"测试弹幕\",[12345,\"观众甲\"]]}", 0);
        var item = Assert.Single(new BilibiliPacketDecoder().DecodeFrame(frame, "bili:8", "7"));
        Assert.Equal(LiveEventType.Chat, item.Type);
        Assert.Equal("测试弹幕", item.Text);
        Assert.Equal("观众甲", item.Nickname);
        Assert.Equal("12345", item.UserId);
        Assert.Equal("8", item.RoomId);
        Assert.Contains("bili:", item.Id);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void CompressedBatchYieldsEveryEvent(bool brotli)
    {
        var json = "{\"cmd\":\"SEND_GIFT\",\"data\":{\"giftName\":\"小心心\",\"num\":3,\"uid\":9,\"uname\":\"送礼人\",\"price\":100,\"coin_type\":\"gold\",\"tid\":\"31036\"}}";
        var items = new BilibiliPacketDecoder().DecodeFrame(Compressed(json, brotli), "bili:8", "9");
        var gift = Assert.Single(items);
        Assert.Equal(LiveEventType.Gift, gift.Type);
        Assert.Equal("小心心", gift.GiftName);
        Assert.Equal(3, gift.Count);
        Assert.Equal(100, gift.GiftUnitValue);
        Assert.Equal("31036", gift.GiftId);
    }

    [Fact] public void SilverGiftKeepsZeroUnitValue()
    {
        var json = "{\"cmd\":\"SEND_GIFT\",\"data\":{\"giftName\":\"辣条\",\"num\":1,\"uname\":\"甲\",\"price\":100,\"coin_type\":\"silver\"}}";
        var gift = Assert.Single(new BilibiliPacketDecoder().DecodeFrame(Packet(json, 0), "bili:8", "1"));
        Assert.Equal(0, gift.GiftUnitValue); // 免费礼物不按金瓜子计价。
    }

    [Fact] public void LikeUsesRoomTotalDeltaAndIgnoresFirstBaseline()
    {
        var decoder = new BilibiliPacketDecoder();
        var first = decoder.DecodeFrame(Packet("{\"cmd\":\"LIKE_INFO_V3_UPDATE\",\"data\":{\"click_count\":130}}", 0), "bili:8", "1");
        Assert.Empty(first); // 首包只作基线，不补算历史点赞。
        var second = decoder.DecodeFrame(Packet("{\"cmd\":\"LIKE_INFO_V3_UPDATE\",\"data\":{\"click_count\":260}}", 0), "bili:8", "2");
        Assert.Equal(130, Assert.Single(second).Count);
        Assert.Empty(decoder.DecodeFrame(Packet("{\"cmd\":\"LIKE_INFO_V3_CLICK\",\"data\":{\"uname\":\"甲\"}}", 0), "bili:8", "3"));
    }

    [Fact] public void InteractWordMapsFollowAndEnter()
    {
        var follow = Assert.Single(new BilibiliPacketDecoder().DecodeFrame(Packet("{\"cmd\":\"INTERACT_WORD\",\"data\":{\"uid\":9,\"uname\":\"关注者\",\"msg_type\":2}}", 0), "bili:8", "1"));
        Assert.Equal(LiveEventType.Follow, follow.Type);
        Assert.True(follow.IsFollower);
        var enter = Assert.Single(new BilibiliPacketDecoder().DecodeFrame(Packet("{\"cmd\":\"INTERACT_WORD\",\"data\":{\"uid\":9,\"uname\":\"进来的人\",\"msg_type\":1}}", 0), "bili:8", "2"));
        Assert.Equal(LiveEventType.Enter, enter.Type);
    }

    [Fact] public void SuperChatArrivesAsChat()
    {
        var json = "{\"cmd\":\"SUPER_CHAT_MESSAGE\",\"data\":{\"message\":\"置顶留言\",\"user_info\":{\"uid\":5,\"uname\":\"老板\"}}}";
        var item = Assert.Single(new BilibiliPacketDecoder().DecodeFrame(Packet(json, 0), "bili:8", "1"));
        Assert.Equal(LiveEventType.Chat, item.Type);
        Assert.Equal("置顶留言", item.Text);
        Assert.Equal("老板", item.Nickname);
    }

    [Fact] public void TruncatedOrUnknownFramesAreIgnoredWithoutThrowing()
    {
        var decoder = new BilibiliPacketDecoder();
        Assert.Empty(decoder.DecodeFrame(new byte[] { 1, 2, 3 }, "bili:8", "1"));
        Assert.Empty(decoder.DecodeFrame(Packet("{\"cmd\":\"ONLINE_RANK_COUNT\"}", 0), "bili:8", "2"));
        Assert.Empty(decoder.DecodeFrame(Packet("这不是 JSON", 0), "bili:8", "3"));
    }

    [Fact] public void OversizedDecompressionIsRejected()
    {
        // 压缩后很小、解压后超过上限的包必须被拒绝，避免压缩炸弹。
        var big = new string('x', 17 * 1024 * 1024);
        var frame = Compressed("{\"cmd\":\"DANMU_MSG\",\"info\":[[],\"" + big + "\",[1,\"甲\"]]}", true);
        Assert.Throws<InvalidDataException>(() => new BilibiliPacketDecoder().DecodeFrame(frame, "bili:8", "1"));
    }
}
