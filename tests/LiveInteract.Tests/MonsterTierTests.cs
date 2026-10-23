using System.Reflection;
using LiveInteract.Contracts;
using LiveInteract.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LiveInteract.Tests;

[CollectionDefinition("Monster pools", DisableParallelization = true)]
public sealed class MonsterPoolCollection { }

/// <summary>目录分类与抽签契约；不把历史召唤成功当作主动战斗验收。</summary>
[Collection("Monster pools")]
public sealed class MonsterTierTests:IDisposable
{
    private readonly ApprovedSummonsScope approved=new();
    public void Dispose()=>approved.Dispose();
    private static MonsterDefinition Pick(string id, string pool)
    {
        var method = typeof(NativeSummons).GetMethod("RandomMonster", new[] { typeof(string), typeof(string) });
        Assert.NotNull(method);
        try { return (MonsterDefinition)method.Invoke(null, new object[] { id, pool })!; }
        catch (TargetInvocationException error) { throw error.InnerException!; }
    }
    private static string Tier(MonsterDefinition monster)
    {
        var property = typeof(MonsterDefinition).GetProperty("Tier");
        Assert.NotNull(property);
        return property.GetValue(monster)!.ToString()!;
    }

    [Fact]
    public void EveryEntryExplicitlyDeclaresTierAndRationale()
    {
        using var input = typeof(MonsterCatalog).Assembly.GetManifestResourceStream("LiveInteract.Contracts.monsters.json");
        using var reader = new StreamReader(input!);
        var rows = JArray.Parse(reader.ReadToEnd());
        Assert.Equal(234, rows.Count);
        Assert.Equal(234, MonsterCatalog.All.Select(m => m.Id).Distinct().Count());
        Assert.All(rows, row =>
        {
            Assert.Contains((string?)row["Tier"], new[] { "Weak", "Strong", "Boss" });
            Assert.False(string.IsNullOrWhiteSpace((string?)row["TierReason"]));
            Assert.Equal((bool)row["IsBoss"]!, (string?)row["Tier"] == "Boss");
        });
        Assert.All(MonsterCatalog.All, m => Assert.Equal(m.IsBoss, Tier(m) == "Boss"));
    }

    [Theory]
    [InlineData("spawn.bone-goomba", "Weak")]
    [InlineData("spawn.bone-spitter", "Weak")]
    [InlineData("spawn.pilgrim-bell-thrower", "Weak")]
    [InlineData("spawn.slab-fly-large", "Strong")]
    [InlineData("spawn.song-handmaiden", "Strong")]
    [InlineData("spawn.coral-warrior-grey", "Strong")]
    [InlineData("spawn.coral-hunter", "Boss")]
    public void OriginalStatsAndAttackComplexityDetermineTier(string id, string expected)
    {
        Assert.Equal(expected, Tier(MonsterCatalog.Find(id)!));
    }

    [Theory]
    [InlineData("weak")][InlineData("strongBoss")][InlineData("ordinary")][InlineData("all")]
    public void PoolsMatchExactBoundariesAndRetryChoice(string pool)
    {
        var expected = MonsterCatalog.All.Where(m => m.CanFight && m.SummonEligible && (pool switch
        {
            "weak" => Tier(m) == "Weak",
            "strongBoss" => Tier(m) is "Strong" or "Boss",
            "ordinary" => m.RandomEligible && !m.IsBoss,
            _ => m.RandomEligible
        })).OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(expected);
        foreach (var index in Enumerable.Range(0, 160))
        {
            var id = "tier-retry-" + index;
            var chosen = Pick(id, pool);
            Assert.Equal(expected[NativeSummons.Choice(id, expected.Length)].Id, chosen.Id);
            Assert.Equal(chosen.Id, Pick(id, pool).Id);
        }
    }

    [Fact]
    public void LegacyPoolsAndBoolOverloadKeepTheirMeanings()
    {
        foreach (var i in Enumerable.Range(0, 160))
        {
            Assert.Equal(NativeSummons.RandomMonster("legacy-" + i, false).Id, Pick("legacy-" + i, "ordinary").Id);
            Assert.Equal(NativeSummons.RandomMonster("legacy-" + i, true).Id, Pick("legacy-" + i, "all").Id);
        }
        Assert.Throws<ArgumentException>(() => Pick("unknown", "strong"));
    }

    [Theory]
    [InlineData("spawn.lilypad-fly")][InlineData("spawn.lifeblood-fly")][InlineData("spawn.fields-flock-flyers")][InlineData("spawn.surface-scuttler")][InlineData("spawn.coral-swimmer-small")]
    public void NoAttackUnitsAreHiddenAndRejectedThroughEveryNamedPath(string id)
    {
        var monster = MonsterCatalog.Find(id)!;
        Assert.False(monster.CanFight);
        Assert.DoesNotContain(ActionCatalog.UserFacing, a => a.Id == id);
        Assert.Contains(ActionValidation.Validate(new() { ActionId = id }), e => e.Contains("没有攻击"));
        foreach (var name in monster.Aliases.Concat(new[] { monster.Id, monster.Name, monster.ObjectName }).Distinct())
        {
            Assert.Contains("没有攻击", Assert.Throws<ArgumentException>(() => SummonCommands.Resolve(name)).Message);
            foreach (var action in new[] { "summon.named", "summon.named.ally", "summon.named.enemy" })
                Assert.Contains(ActionValidation.Validate(new() { ActionId = action, Arguments = new() { ["name"] = name } }), e => e.Contains("没有攻击"));
        }
    }

    [Theory]
    [InlineData("spawn.lilypad-fly")][InlineData("spawn.lifeblood-fly")][InlineData("spawn.fields-flock-flyers")][InlineData("spawn.surface-scuttler")][InlineData("spawn.coral-swimmer-small")]
    public void LegacyNamedAssetAndConversionCallsAlsoRejectNoAttackUnits(string id)
    {
        var monster = MonsterCatalog.Find(id)!;
        foreach (var name in monster.Aliases.Concat(new[] { monster.Id, monster.Name, monster.ObjectName, monster.Asset }).Where(n => n.Length > 0).Distinct())
        {
            foreach (var action in new[] { "enemy.spawn", "enemy.asset", "boss.asset" })
            {
                var key = action == "enemy.spawn" ? "enemy" : "asset";
                Assert.Contains(ActionValidation.Validate(new() { ActionId = action, Arguments = new() { [key] = name } }), e => e.Contains("没有攻击"));
            }
        }
        Assert.Contains("没有攻击", Assert.Throws<ArgumentException>(() => NativeSummons.Convert(monster, 1)).Message);
    }

    [Fact]
    public void PoolChoicesExposeAllFourAndOmittedLegacyPoolStaysOrdinary()
    {
        foreach (var id in new[] { "summon.random", "summon.random.ally", "summon.random.enemy" })
        {
            var parameter = ActionCatalog.All.Single(a => a.Id == id).Parameters.Single(p => p.Key == "pool");
            Assert.Equal(new[] { "all", "ordinary", "strongBoss", "weak" }, parameter.Choices.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal("ordinary", parameter.DefaultValue);
            Assert.Contains("旧版", parameter.Choices["ordinary"]);
            Assert.Contains("旧版", parameter.Choices["all"]);
        }
    }

    [Fact]
    public void CombatTierFiltersExposeOnlyCombatUnits()
    {
        foreach (var monster in MonsterCatalog.All.Where(m => m.CanFight && m.SummonEligible))
        {
            var action = Assert.Single(ActionCatalog.UserFacing, a => a.Id == monster.Id);
            Assert.Equal(Tier(monster) switch { "Weak" => "弱怪", "Strong" => "强怪", _ => "Boss" }, action.Category);
        }
    }

    [Fact]
    public void CombatFlagAndEligibilityIndependentlyGateEveryPoolIncludingBoss()
    {
        // 临时改变目录标记仅用于证明两道过滤独立生效；整个 collection 不并行并在 finally 还原。
        var monster = MonsterCatalog.Find("spawn.lilypad-fly")!;
        var boss = MonsterCatalog.All.First(m => m.IsBoss && m.RandomEligible);
        var eligible = monster.RandomEligible;
        try
        {
            monster.RandomEligible = true;
            boss.CanFight = false;
            foreach (var i in Enumerable.Range(0, 400))
            {
                var id = "filter-" + i;
                foreach (var pool in new[] { "weak", "strongBoss", "ordinary", "all" }) Assert.True(Pick(id, pool).CanFight);
                Assert.True(NativeSummons.RandomMonster(id, false).CanFight);
                Assert.True(NativeSummons.RandomMonster(id, true).CanFight);
                Assert.True(NativeSummons.Boss(id).CanFight);
            }
        }
        finally { monster.RandomEligible = eligible; boss.CanFight = true; }
    }

    [Theory]
    [InlineData("ordinary", "旧版普通池")][InlineData("all", "旧版全池")]
    [InlineData("weak", "弱怪池")][InlineData("strongBoss", "强怪 + Boss池")]
    public void RuleDisplayMakesNewAndLegacyPoolMeaningVisible(string pool, string label)
    {
        var step = new ActionStep { ActionId = "summon.random", Arguments = new() { ["pool"] = pool, ["power"] = "4" } };
        Assert.Contains(label, RuleDisplay.Effect(step));
        Assert.Contains("4×", RuleDisplay.Effect(step));
    }

    [Fact]
    public void OldCustomPoolAndPowerAreNotAutoMigrated()
    {
        var profile = new RuleProfile { Rules = new() { new() { Name = "旧礼物", Root = new() { ActionId = "summon.random.enemy", Arguments = new() { ["pool"] = "all", ["power"] = "4", ["count"] = "3" } } } } };
        var read = ProfileStore.Read(Newtonsoft.Json.JsonConvert.SerializeObject(profile), false);
        Assert.Equal("all", read.Rules[0].Root.Arguments["pool"]);
        Assert.Equal("4", read.Rules[0].Root.Arguments["power"]);
        Assert.Equal("3", read.Rules[0].Root.Arguments["count"]);
    }
}
