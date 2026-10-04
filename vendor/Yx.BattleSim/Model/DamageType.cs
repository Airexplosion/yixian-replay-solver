namespace Yx.BattleSim.Model;

/// <summary>
/// 伤害类型，对应游戏 <c>DamageType</c>。防御吸收只对「非 ReduceHp」的伤害生效；
/// 无视防御只对 <see cref="Attack"/> 生效（见 BattleCharacter.ApplyDamage）。
/// </summary>
public enum DamageType
{
    /// <summary>普通攻击（走防御吸收、可被无视防御跳过、触发外伤/锋锐等攻击后效果）。</summary>
    Attack = 0,

    /// <summary>效果伤害（走防御吸收，但不算「攻击」，不触发攻击专属 buff）。</summary>
    Damage = 1,

    /// <summary>反弹伤害。</summary>
    ReflectDamage = 2,

    /// <summary>直扣血（跳过防御，直接进 hp）。</summary>
    ReduceHp = 3,
}
