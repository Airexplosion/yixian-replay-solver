using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 卡 19 澄心剑胚：牌面数值 / 名字随持有者的天赋改变 —— 忠实移植 <c>Card_19.UpdateCardInfo</c>（原码 52639），
/// 游戏在 <c>CardItem.RefreshSpecialCard</c> 里对基础 id 为 19 的牌调用它，再 <c>InitData</c> 成改过的配置。
///
/// 读的是 <c>publicData.talents</c> 与 <c>publicData.talentTempDatas</c>（天赋 92 的累计加攻）。
/// 改动（按原码顺序）：
///   · 天赋 30096 → 后面几处额外 +1（num）；92 → 攻 + tempDatas[92] + num；
///   · 10093 → 攻 + o[0]；20093 → 防清零、攻 − o[0]、防 + o[1] + num；30094 → 攻 − o[1]（剑意改为 o[0]+num）；
///   · 10095 → 灵气改为 o[0] + num；10096 → 名「狂剑•澄心」、攻 + o[1]；20096 → 名「云剑•澄心」、攻 − o[1]；
///     30096 → 名「澄心•无极」；最后攻至少 1。
///   · 另外还改**境界**（10093/20093 → 筑基 2，10094/20094/30094 → 金丹 3，10095/20095/30095 → 元婴 4，
///     10096/20096/30096 → 化神 5）与**剑意**（30094 → o[0] + num）。这两项按原码顺序「后写的覆盖先写的」。
/// </summary>
public static class Card19Functions
{
    public const int BaseId = 19;

    // Level 枚举（原码 ConfigManager 里的同名值）：LianQi 1 / ZhuJi 2 / JinDan 3 / YuanYing 4 / HuaShen 5。
    const int LevelZhuJi = 2, LevelJinDan = 3, LevelYuanYing = 4, LevelHuaShen = 5;

    /// <summary>这张牌（基础 id 19）在持有者天赋下的实际牌面；不是卡 19 原样返回。</summary>
    public static BattleCard Apply(Combatant c, BattleCard card)
    {
        if (CardTypes.BaseId(card.Id) != BaseId) return card;
        var cfg = c.Config;
        int num = c.HasTalent(30096) ? 1 : 0;
        int atk = card.Attack, def = card.Def, anima = card.Anima;
        if (c.HasTalent(92) && c.TalentTempDatas.TryGetValue(92, out int t92)) atk += t92 + num;
        if (c.HasTalent(10093)) atk += cfg.TalentOtherParams(10093).At(0);
        if (c.HasTalent(20093))
        {
            def = 0;
            atk -= cfg.TalentOtherParams(20093).At(0);
            def += cfg.TalentOtherParams(20093).At(1);
            def += num;
        }
        if (c.HasTalent(30094)) atk -= cfg.TalentOtherParams(30094).At(1);
        if (c.HasTalent(10095)) anima = cfg.TalentOtherParams(10095).At(0) + num;
        if (c.HasTalent(10096)) atk += cfg.TalentOtherParams(10096).At(1);
        if (c.HasTalent(20096)) atk -= cfg.TalentOtherParams(20096).At(1);
        if (atk < 1) atk = 1;
        // 境界与剑意：按原码顺序「后写的覆盖先写的」。0 = 没被改过（读的时候回落到卡表静态值）。
        int level = 0;
        if (c.HasTalent(10093) || c.HasTalent(20093)) level = LevelZhuJi;
        if (c.HasTalent(10094) || c.HasTalent(20094) || c.HasTalent(30094)) level = LevelJinDan;
        if (c.HasTalent(10095) || c.HasTalent(20095) || c.HasTalent(30095)) level = LevelYuanYing;
        if (c.HasTalent(10096) || c.HasTalent(20096) || c.HasTalent(30096)) level = LevelHuaShen;
        int jianYi = c.HasTalent(30094) ? cfg.TalentOtherParams(30094).At(0) + num : 0;
        return card with
        {
            Attack = atk, Def = def, Anima = anima, Name = NameOf(c, card.Id) ?? card.Name,
            Level = level, JianYi = jianYi,
        };
    }

    /// <summary>卡 19 被天赋改过的名字（10096 / 20096 / 30096，后者覆盖前者）；不是卡 19 或没改名返回 null。</summary>
    public static string? NameOf(Combatant c, int cardId)
    {
        if (CardTypes.BaseId(cardId) != BaseId) return null;
        string? n = null;
        if (c.HasTalent(10096)) n = "狂剑•澄心";
        if (c.HasTalent(20096)) n = "云剑•澄心";
        if (c.HasTalent(30096)) n = "澄心•无极";
        return n;
    }
}
