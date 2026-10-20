using LiveInteract.Contracts;
using LiveInteract.Core;
using Google.Protobuf;
using Newtonsoft.Json;
using Xunit;

namespace LiveInteract.Tests;

/// <summary>验证临时效果、危险动作和配置边界。</summary>
public sealed class BoundaryTests
{
    /// <summary>叠加到期后应恢复最初基础值，而不是上一次叠加值。</summary>
    [Fact]
    public void TemporaryStackRestoresOriginalValue()
    {
        var effects = new TimedEffects(); // 与游戏共用的效果容器。
        var speed = 10d; // 原始角色速度。
        effects.Apply("speed", 2, 10, EffectStacking.Stack, true, () => speed, v => speed = v, 0);
        Assert.Equal(20, speed);
        effects.Apply("speed", 3, 5, EffectStacking.Stack, true, () => speed, v => speed = v, 1);
        Assert.Equal(60, speed);
        effects.Tick(6);
        Assert.Equal(20, speed);
        effects.Tick(11);
        Assert.Equal(10, speed);
    }

    /// <summary>数值溢出的叠加不能丢失原有恢复记录。</summary>
    [Fact]
    public void OverflowDoesNotLoseOriginalRestoration()
    {
        var effects = new TimedEffects(); var value = 1d;
        effects.Apply("value", double.MaxValue, 10, EffectStacking.Stack, true, () => value, x => value = x, 0);
        Assert.Throws<InvalidOperationException>(() => effects.Apply("value", 2, 10, EffectStacking.Stack, true, () => value, x => value = x, 1));
        effects.RestoreAll(); Assert.Equal(1, value);
    }

    /// <summary>延长和手动恢复必须对同一基础值生效。</summary>
    [Fact]
    public void ExtendAndEmergencyRestore()
    {
        var effects = new TimedEffects(); // 新会话。
        var value = 1d; // 原始倍率。
        effects.Apply("damage", 4, 10, EffectStacking.Refresh, true, () => value, x => value = x, 0);
        effects.Apply("damage", 4, 5, EffectStacking.Extend, true, () => value, x => value = x, 3);
        effects.Tick(12);
        Assert.Equal(4, value);
        effects.RestoreAll();
        Assert.Equal(1, value);
        Assert.Empty(effects.Describe(12));
    }

    /// <summary>目标已经销毁时不执行属性恢复回调。</summary>
    [Fact]
    public void DestroyedTargetIsNotWritten()
    {
        var effects = new TimedEffects(); // 新会话。
        var alive = true; // 模拟场景销毁。
        var writes = 0; // 回调计数。
        effects.Apply("target", 2, 1, EffectStacking.Refresh, true, () => 1, _ => writes++, 0, () => alive);
        alive = false;
        effects.Tick(2);
        Assert.Equal(1, writes);
    }

    /// <summary>永久动作需要显式许可，越界和未知参数不能进入游戏。</summary>
    [Fact]
    public void ActionValidationBlocksPersistentAndBadParameters()
    {
        Assert.Contains(ActionCatalog.All, a => a.Id == "health.add");
        Assert.NotEmpty(ProfileStore.ValidateAction(new() { ActionId = "currency.rosaries", Arguments = new() { ["value"] = "50" } }));
        Assert.NotEmpty(ProfileStore.ValidateAction(new() { ActionId = "health.add", Arguments = new() { ["value"] = "NaN" } }));
        Assert.NotEmpty(ProfileStore.ValidateAction(new() { ActionId = "health.add", Arguments = new() { ["wrong"] = "1" } }));
        Assert.Empty(ProfileStore.ValidateAction(new() { ActionId = "health.add", Arguments = new() { ["value"] = "2" } }));
    }

    /// <summary>导入的配置不得自动执行，并拒绝未来格式。</summary>
    [Fact]
    public void ImportedRulesAreDisabledAndVersionChecked()
    {
        var json = JsonConvert.SerializeObject(new RuleProfile { Rules = [new() { Enabled = true }] }); // 外部文档。
        Assert.False(Assert.Single(ProfileStore.Read(json).Rules).Enabled);
        Assert.Throws<InvalidDataException>(() => ProfileStore.Read("{\"SchemaVersion\":99}"));
    }
}

/// <summary>构造协议数据只用于验证解析，不作为真实抖音接入的证明。</summary>
public sealed class PacketDecoderTests
{
    /// <summary>解析嵌套帧中的真实字段编号。</summary>
    [Fact]
    public void DecodesChatAndGiftFields()
    {
        var decoder = new DouyinPacketDecoder(); // 被测解析器。
        var common = Message((2, 101L)); // 平台消息 ID。
        var user = Message((1, 42L), (3, "测试观众")); // 用户字段。
        var chat = Message((1, common), (2, user), (3, "回血")); // 弹幕载荷。
        var envelope = Message((1, "WebcastChatMessage"), (2, chat), (3, 101L)); // 消息信封。
        var frame = Message((8, Message((1, envelope)))); // 外层 PushFrame。
        var item = Assert.Single(decoder.DecodeFrame(frame, "123")); // 解析后的事件。
        Assert.Equal("回血", item.Text);
        Assert.Equal("42", item.UserId);
        Assert.Equal("123", item.RoomId);

        var gift = Message((1, Message((2, 102L))), (2, 9L), (5, 3L), (7, user), (9, 1L), (11, 777L), (15, Message((10, 1L), (16, "小心心")))); // 连击结束载荷。
        var giftEnvelope = Message((1, "WebcastGiftMessage"), (2, gift), (3, 102L)); // 礼物消息。
        var eventGift = Assert.Single(decoder.DecodeFrame(Message((8, Message((1, giftEnvelope)))), "123")); // 解析结果。
        Assert.Equal(LiveEventType.Gift, eventGift.Type);
        Assert.Equal(3, eventGift.Count);
        Assert.True(eventGift.ComboEnded);
        Assert.True(eventGift.IsCumulative);
        Assert.Equal("小心心", eventGift.GiftName);
    }

    /// <summary>无用户字段的点赞仍保持未知身份。</summary>
    [Fact]
    public void AnonymousLikesStayAnonymous()
    {
        var data = Message((1, Message((2, 303L))), (2, 6L), (3, 80L)); // 无用户对象。
        var entry = Message((1, "WebcastLikeMessage"), (2, data), (3, 303L)); // 点赞信封。
        var item = Assert.Single(new DouyinPacketDecoder().DecodeFrame(Message((8, Message((1, entry)))), "1"));
        Assert.Equal(6, item.Count);
        Assert.Equal("", item.UserId);
        Assert.Null(item.IsFollower);
    }

    /// <summary>构造最小 protobuf 消息。</summary>
    /// <param name="fields">字段号和测试值。</param>
    /// <returns>编码字节。</returns>
    /// <summary>组合礼物的每组数量不能被忽略。</summary>
    [Fact]
    public void GiftGroupQuantityAndTraceFallbackArePreserved()
    {
        var gift = Message((2, 9L), (4, 10L), (5, 3L), (6, 3L), (7, Message((1, 42L), (3, "观众甲"))), (11, 77L), (15, Message((10, 1L), (12, 123L), (16, "小心心"))), (35, "trace-unique"));
        var envelope = Message((1, "WebcastGiftMessage"), (2, gift));
        var value = Assert.Single(new DouyinPacketDecoder().DecodeFrame(Message((8, Message((1, envelope)))), "fixture"));
        Assert.Equal(30, value.Count); Assert.Equal(123, value.GiftUnitValue); Assert.Contains("trace-unique", value.Id);
    }

    /// <summary>缺少连击组号的礼物仍必须被读取并明确待核对，而不是丢弃。</summary>
    [Fact]
    public void IncompleteGiftIsKeptForReview()
    {
        var gift = Message((1, Message((2, 3001L))), (2, 9L), (5, 2L), (15, Message((10, 1L), (16, "小心心"))));
        var envelope = Message((1, "WebcastGiftMessage"), (2, gift));
        var value = Assert.Single(new DouyinPacketDecoder().DecodeFrame(Message((8, Message((1, envelope)))), "fixture"));
        Assert.True(value.NeedsReview); Assert.Contains("组", value.ReviewReason);
    }

    private static byte[] Message(params (int Key, object Value)[] fields)
    {
        using var stream = new MemoryStream(); // 输出缓冲。
        using var output = new CodedOutputStream(stream, true); // 使用成熟 protobuf 编码器。
        foreach (var field in fields)
        {
            if (field.Value is long number)
            {
                output.WriteTag(field.Key, WireFormat.WireType.Varint);
                output.WriteInt64(number);
            }
            else
            {
                output.WriteTag(field.Key, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(field.Value is byte[] bytes ? ByteString.CopyFrom(bytes) : ByteString.CopyFromUtf8((string)field.Value));
            }
        }
        output.Flush();
        return stream.ToArray();
    }
}
