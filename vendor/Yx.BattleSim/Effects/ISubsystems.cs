using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 伤害/血量管线里对**尚未移植子系统**的调用接缝：刻印(P4)、五行(P5)、天赋共鸣(P5)、开关(游戏模式)。
/// 忠实移植时这些分支的**结构就位**（原代码怎么判就怎么判），行为经此接口取得；
/// <see cref="NeutralSubsystems"/> 一律返回「无效果」，等各子系统上线时换成真实实现即可点亮，
/// 逐条对 oracle 校验（见覆盖账）。这样 ApplyDamage/ModifyHp 的结构不因子系统缺席而失真或缺分支。
/// </summary>
public interface ISubsystems
{
    /// <summary>KeYinCardFunctions.HasKeYinType(src, keYinId)。</summary>
    bool HasKeYinType(Combatant c, int keYinId);

    /// <summary>KeYinCardFunctions.GetTotalKeYinOtherparam(src, keYinTypeId, index)。</summary>
    int KeYinOtherparam(Combatant c, int keYinTypeId, int index);

    /// <summary>CardActionBase.CheckWuXing(src, wuxingBuff)：五行是否激活。</summary>
    bool CheckWuXing(Combatant c, BuffType wuxingBuff);

    /// <summary>IsTalentResonanceEffective(resonanceTalentId)。</summary>
    bool IsTalentResonanceEffective(Combatant c, int resonanceTalentId);

    /// <summary>共鸣一次性标志：读。</summary>
    bool CheckTalentResonanceTempFlag(Combatant c, int resonanceTalentId);

    /// <summary>共鸣一次性标志：写。</summary>
    void SetTalentResonanceTempFlag(Combatant c, int resonanceTalentId, bool value);

    /// <summary>OpenManager.IsOpen(openType)：游戏模式开关（如同修后手补偿允许复活）。</summary>
    bool IsOpen(int openType);

    /// <summary>GetDebuffCount()：身上负面 buff 计数（虚化之类按此加伤）。</summary>
    int DebuffCount(Combatant c);

    /// <summary>HasCardInDeck(cardBaseId)：牌堆里是否有某底牌（气势 累加/倍率 的分流条件）。</summary>
    bool HasCardInDeck(Combatant c, int cardBaseId);

    /// <summary>HasTalentResonance(resonanceTalentId)：是否携带某共鸣（与「是否生效」不同）。</summary>
    bool HasTalentResonance(Combatant c, int resonanceTalentId);
}

/// <summary>全部返回「无效果」的默认实现（子系统未上线时用）。</summary>
public sealed class NeutralSubsystems : ISubsystems
{
    public static readonly NeutralSubsystems Instance = new();

    public bool HasKeYinType(Combatant c, int keYinId) => false;
    public int KeYinOtherparam(Combatant c, int keYinTypeId, int index) => 0;
    public bool CheckWuXing(Combatant c, BuffType wuxingBuff) => false;
    public bool IsTalentResonanceEffective(Combatant c, int resonanceTalentId) => false;
    public bool CheckTalentResonanceTempFlag(Combatant c, int resonanceTalentId) => false;
    public void SetTalentResonanceTempFlag(Combatant c, int resonanceTalentId, bool value) { }
    public bool IsOpen(int openType) => false;
    public int DebuffCount(Combatant c) => 0;
    public bool HasCardInDeck(Combatant c, int cardBaseId) => false;
    public bool HasTalentResonance(Combatant c, int resonanceTalentId) => false;
}

/// <summary>
/// 默认子系统：把**炼化(P4)** 两钩子做实（读 Combatant.BattleKeYinCards），其余仍中性（五行/共鸣/牌库/debuff 待 P5）。
/// 这是 Combatant.Subs 的默认值，使伤害管线里的刻印分支随炼化卡自动点亮。
/// </summary>
public sealed class DefaultSubsystems : ISubsystems
{
    public static readonly DefaultSubsystems Instance = new();

    public bool HasKeYinType(Combatant c, int keYinId) => KeYinCardFunctions.HasKeYinType(c, keYinId);
    public int KeYinOtherparam(Combatant c, int keYinTypeId, int index) => KeYinCardFunctions.GetTotalKeYinOtherparam(c, keYinTypeId, index);
    public bool CheckWuXing(Combatant c, BuffType wuxingBuff) => WuXingFunctions.CheckWuXing(c, wuxingBuff);   // 五行已上线
    public bool IsTalentResonanceEffective(Combatant c, int id) => c.IsTalentResonanceEffective(id);   // 共鸣已上线
    public bool CheckTalentResonanceTempFlag(Combatant c, int id) => c.CheckTalentResonanceTempFlag(id);
    public void SetTalentResonanceTempFlag(Combatant c, int id, bool value) => c.SetTalentResonanceTempFlag(id, value);
    // 功能开关：目前 sim 只读一个 —— OpenTongXiuHouShouBuChang（同修后手补偿，Combatant 里的占位 id 1）。
    // 实机是**开着**的：开着时 ModifyHp 一律 canRevive，血量 ≤0 时仍能回血（b6_56 逐步对拍：飞鸿踏雪先扣到 -1、后招再 +1 回到 0）。
    public bool IsOpen(int openType) => openType == 1;
    public int DebuffCount(Combatant c) => c.GetDebuffCount();                  // BuffConfig 已接入，debuff 判定上线
    public bool HasCardInDeck(Combatant c, int cardId)
    {
        // 游戏里取 m_BattleDeck[i].cardInfo.id；本 sim 的 Board 就是按格顺序的牌。
        // 传进来的既有 baseId（10000084/372）也有整 id（7030077），两种都比。
        foreach (var b in c.Board)
            if (b.Id == cardId || CardTypes.BaseId(b.Id) == cardId) return true;
        return false;
    }
    public bool HasTalentResonance(Combatant c, int id) => c.HasTalentResonance(id);
}
