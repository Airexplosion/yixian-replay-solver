namespace Yx.BattleSim.Model;

/// <summary>
/// 一张摆上场的牌的静态配置（来自游戏 CardConfig，字段对照见 battle-rules-reverse note）。
/// 这是不可变输入：<see cref="BattleInput"/> 里每个格子引用一张。效果参数在 <see cref="OtherParams"/>，
/// 具体机制由 Effects 注册表按卡类型/关键字解释（数据驱动，对应游戏 ~5 个 CardAction 基类）。
/// </summary>
public sealed record BattleCard
{
    /// <summary>卡 id（CardConfig f1）。</summary>
    public required int Id { get; init; }

    /// <summary>攻击（f8）。</summary>
    public int Attack { get; init; }

    /// <summary>防御（f11）。</summary>
    public int Def { get; init; }

    /// <summary>段数：一次出牌拆成几段攻击，每段单独结算（f10）。至少 1。</summary>
    public int AttackCount { get; init; } = 1;

    /// <summary>灵气增减（f7，正回负耗）。</summary>
    public int Anima { get; init; }

    /// <summary>CardConfig.randomAttack：随机攻击的**上限**（`{attack}～{randomAttack}攻` 那一族）。
    /// 见 <see cref="Effects.CardEffects.Default"/> 里 FallbackCardAction 的随机分支。</summary>
    public int RandomAttack { get; init; }

    /// <summary>CardConfig.randomDef：随机防御的**上限**（`{def}～{randomDef}防`），仅 9 张。</summary>
    public int RandomDef { get; init; }

    /// <summary>体魄（f21）。</summary>
    public int Physique { get; init; }

    /// <summary>效果参数（CardConfig otherParams）。</summary>
    public IReadOnlyList<int> OtherParams { get; init; } = [];

    /// <summary>是否自带「无视防御」关键字（攻击跳过对方 def）。</summary>
    public bool IgnoreDefense { get; init; }

    /// <summary>
    /// 打完后本回合可再行动一次（对应 cardConfig.actionAgain）。
    /// 注意：游戏里这个字段会被卡效果**运行期改写**，所以 sim 把它复制到
    /// <see cref="Combatant.CurrentCardActionAgain"/> 上再判——避免污染调用方复用的 BattleCard（跨场泄漏）。
    /// </summary>
    public bool ActionAgain { get; init; }

    /// <summary>牌名（仅诊断用，可空以免带入厂商文本）。</summary>
    public string? Name { get; init; }

    /// <summary>
    /// 被「按持有者天赋改牌面」的效果改写过的境界（目前的唯一来源是卡 19 <c>UpdateCardInfo</c>）。
    /// 0 = 没被改过，取卡表静态值 —— 读的时候一律走 <see cref="Effects.CardTypes.LevelOf"/>。
    /// </summary>
    public int Level { get; init; }

    /// <summary>同上，被改写过的剑意；0 = 取卡表静态值（<see cref="Effects.CardTypes.JianYiOf"/>）。</summary>
    public int JianYi { get; init; }
}
