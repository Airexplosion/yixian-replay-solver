using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// A 类参数：服务器按**开战时的盘面**算出来的值，估值时照公式精确复现（笔记 §3.2）。
/// 「手牌」= <see cref="Combatant.LastRoundHandCards"/>（开战时的 lastRoundData.handCards，顺序即左→右，最右侧 = 最后一个）。
/// 取值方 = 出这张牌的一方；<paramref name="cardId"/> 由消费点显式传入（不读 CurrentCardId）。
/// 找不到符合条件的牌时返回 -1（与服务器写 -1 表示「没有」一致，调用处都判了 != -1）。
/// </summary>
public static class ParamFormulas
{
    private const int SubcategoryNone = 0;
    private const int LevelYuanYing = 4;

    public static int Evaluate(Combatant c, int cardId)
    {
        switch (CardTypes.BaseId(cardId))
        {
            case 9: return LingMaoLuanJian(c, cardId);
            case 262: return YinQiJian(c, cardId);
            case 395: return RightmostSustain(c);
            case 1000052: return CanYunFengTian(c, cardId);
            case 7000078: return HunYuanWuJiZhen(c, cardId);
            case 10000089: return BengQuanLianBeng(c, cardId);
            default: return -1;
        }
    }

    /// <summary>9 灵猫乱剑：总段数 = attackCount + min(保留手牌数, o[0])。</summary>
    private static int LingMaoLuanJian(Combatant c, int cardId)
    {
        int count = (c.Config as JsonBattleConfig)?.Card(cardId)?.AttackCount ?? 1;
        int cap = c.Config.CardOtherParams(cardId).At(0);
        return count + System.Math.Min(c.LastRoundHandCardCount, cap);
    }

    /// <summary>
    /// 262 幻•引气剑：从右往左第一张「常规牌（子类为 0）且加灵气（an &gt; 0）」的手牌；
    /// 等级截到 o[0] 级：<c>base + min(等级位, o[0]-1) × 10000</c>。
    /// </summary>
    private static int YinQiJian(Combatant c, int cardId)
    {
        if (c.Config is not JsonBattleConfig cfg) return -1;
        int maxTier = System.Math.Max(0, c.Config.CardOtherParams(cardId).At(0) - 1);
        var hand = c.LastRoundHandCards;
        for (int i = hand.Count - 1; i >= 0; i--)
        {
            var m = cfg.Card(hand[i]);
            if (m is null || m.Subcategory != SubcategoryNone || m.Anima <= 0) continue;
            int tier = System.Math.Min(CardTypes.Rarity(hand[i]), maxTier);
            return CardTypes.BaseId(hand[i]) + tier * 10000;
        }
        return -1;
    }

    /// <summary>395 万象碱水粽：从右往左第一张持续牌（CardType.Sustain）的手牌 id。</summary>
    private static int RightmostSustain(Combatant c)
    {
        var hand = c.LastRoundHandCards;
        for (int i = hand.Count - 1; i >= 0; i--)
            if (c.Config.CardIsSustain(hand[i])) return hand[i];
        return -1;
    }

    /// <summary>1000052 残云封天剑：追加攻 = o[0] × 手牌中「云剑」张数（牌型按 <see cref="CardTypes.IsYunJian"/>）。</summary>
    private static int CanYunFengTian(Combatant c, int cardId)
    {
        int n = 0;
        foreach (int id in c.LastRoundHandCards) if (CardTypes.IsYunJian(c, id)) n++;
        return c.Config.CardOtherParams(cardId).At(0) * n;
    }

    /// <summary>10000089 梦•崩拳连崩（元婴+）：min(手牌中崩拳张数, o[1])。</summary>
    private static int BengQuanLianBeng(Combatant c, int cardId)
    {
        int n = 0;
        foreach (int id in c.LastRoundHandCards) if (CardTypes.IsBengQuan(c, id)) n++;
        return System.Math.Min(n, c.Config.CardOtherParams(cardId).At(1));
    }

    /// <summary>
    /// 7000078 梦•混元无极阵：后一格牌的五行 → 对应的 o[0] 级「X灵印」（元婴档：「X灵阵」）。
    /// 名字精确匹配（「金灵阵」而不是「梦•金灵阵」/「极•金灵阵」）；后一格没有五行 → -1。
    /// </summary>
    private static int HunYuanWuJiZhen(Combatant c, int cardId)
    {
        if (c.Config is not JsonBattleConfig cfg) return -1;
        string next = GridFunctions.CardNameAt(c, GridFunctions.GetNextGrid(c, c.CurrentCardGrid));
        string wx = WuXingFunctions.GetWuXingName(next);
        if (wx.Length == 0) return -1;
        string target = wx + (c.Config.CardLevel(cardId) >= LevelYuanYing ? "阵" : "印");
        int tier = System.Math.Max(0, c.Config.CardOtherParams(cardId).At(0) - 1);
        foreach (var kv in cfg.AllCards)
            if (CardTypes.Rarity(kv.Key) == 0 && kv.Value.Name == target)
                return cfg.Card(kv.Key + tier * 10000) is null ? kv.Key : kv.Key + tier * 10000;
        return -1;
    }
}
