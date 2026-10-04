namespace Yx.BattleSim.Model;

/// <summary>
/// 一次伤害的载体，对应游戏 <c>DamageInfo</c>。移植保留可变字段（<see cref="HitDef"/> 在
/// ApplyDamage 内被回写），因为原逻辑依赖它——单场战斗内的可变工作态，不跨战斗共享。
/// </summary>
public sealed class DamageInfo
{
    public required Combatant Source { get; init; }
    public DamageType Type { get; init; }
    public int Damage { get; set; }

    /// <summary>是否已扣到防御（ApplyDamage 里命中 def 时置 true）。</summary>
    public bool HitDef { get; set; }

    /// <summary>跳过外伤判定（部分效果伤害用）。</summary>
    public bool SkipWoundCheck { get; init; }

    /// <summary>锋锐加成值（随伤害传递，落血文本/护体判定用；ApplyDamage 内累积）。</summary>
    public int FengRui { get; set; }

    public static DamageInfo Create(Combatant source, DamageType type, int damage, bool skipWoundCheck = false)
        => new() { Source = source, Type = type, Damage = damage, SkipWoundCheck = skipWoundCheck };
}
