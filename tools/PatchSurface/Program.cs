using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

/// <summary>在真实游戏程序集上逐个解析本 Mod 的补丁目标，提前暴露“补丁目标不存在”这种会让修复静默失效的问题。</summary>
internal static class Program
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Harmony 按“类型 + 成员名”解析的补丁面；新增补丁时同步登记。</summary>
    private static readonly (Type Owner, string Member)[] Targets =
    [
        (typeof(GetHero), "OnEnter"),
        (typeof(DamageHero), "OnEnable"), (typeof(DamageHero), "OnTriggerEnter2D"), (typeof(DamageHero), "OnCollisionEnter2D"),
        (typeof(HealthManager), "Hit"), (typeof(HealthManager), "TakeDamage"),
        (typeof(HeroController), "TakeDamage"),
        (typeof(PlayerData), "TakeHealth"), (typeof(PlayerData), "SetBool"), (typeof(PlayerData), "SetInt"), (typeof(PlayerData), "SetFloat"), (typeof(PlayerData), "SetString"),
        (typeof(MonoBehaviour), "StartCoroutine"),
        (typeof(GetCanSeeHero), "OnEnter"), (typeof(GetCanSeeHero), "OnUpdate"),
        (typeof(GetCanSeeHeroV2), "OnEnter"), (typeof(GetCanSeeHeroV2), "OnUpdate"),
        (typeof(MatchHeroFacing), "DoMatch"),
        (typeof(PositionToHeroClamped), "Update"),
        (typeof(ClockworkHatchling), "Break"), (typeof(ClockworkHatchling), "Update"),
        (typeof(Fsm), "Update"), (typeof(Fsm), "FixedUpdate"), (typeof(Fsm), "LateUpdate"), (typeof(Fsm), "Event"),
        (typeof(UnityEngine.Object), "Instantiate"),
    ];

    private static int Main()
    {
        var failed = 0;
        foreach (var (owner, member) in Targets)
        {
            var resolved = Resolve(owner, member);
            if (resolved == null) { failed++; Console.WriteLine($"FAIL  {owner.Name}.{member} 未找到"); }
            else { Console.WriteLine($"PASS  {owner.Name}.{member}"); }
        }
        Console.WriteLine($"补丁目标 {Targets.Length} 个，缺失 {failed} 个。");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>按 Harmony 的方式沿继承链查找成员；重载方法只要存在同名即可。</summary>
    private static MethodInfo? Resolve(Type owner, string member)
    {
        for (var type = owner; type != null; type = type.BaseType)
        {
            var found = type.GetMethods(Any | BindingFlags.DeclaredOnly).FirstOrDefault(method => method.Name == member);
            if (found != null) { return found; }
        }
        return null;
    }
}
