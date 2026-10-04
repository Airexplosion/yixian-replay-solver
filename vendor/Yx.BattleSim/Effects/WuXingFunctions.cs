using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 五行（金木水火土）：忠实移植自 <c>CardActionBase.CheckWuXing/IsXiangSheng/IsKeZhi</c> +
/// <c>BattleCharacter.ActiveWuXingInName/GetWuXingName/GetWuXingActiveCount(Number)/JiHuoWuXingInName</c>。
///
/// 术语：
///   · 「激活」= 身上挂着对应的 <c>JiHuoXXLing</c> buff（出牌时按牌名激活，见 <see cref="ActiveWuXingInName"/>）；
///   · 「相生」= 金→水→木→火→土→金（激活的元素生出手上这张牌的元素，附加成）；
///   · 「相克」= 金→木→土→水→火→金（<see cref="GetKeZhiWuXing"/> 即此环，回合末五行聚灵按它挂克制 buff）。
///
/// <c>GetWuXingCountInDeck</c> 按整副牌 + 卡名计（仙命 417 另计五行玉屏选的牌）。
/// </summary>
public static class WuXingFunctions
{
    /// <summary>五行激活 buff（顺序：金木水火土）。</summary>
    public static readonly BuffType[] Elements =
    {
        BuffType.JiHuoJinLing, BuffType.JiHuoMuLing, BuffType.JiHuoShuiLing,
        BuffType.JiHuoHuoLing, BuffType.JiHuoTuLing,
    };

    private static readonly (string Name, BuffType Buff)[] NameMap =
    {
        ("金灵", BuffType.JiHuoJinLing),
        ("木灵", BuffType.JiHuoMuLing),
        ("水灵", BuffType.JiHuoShuiLing),
        ("火灵", BuffType.JiHuoHuoLing),
        ("土灵", BuffType.JiHuoTuLing),
    };

    private static bool ContainsAny(string s)
    {
        foreach (var (n, _) in NameMap) if (s.Contains(n)) return true;
        return false;
    }

    /// <summary>CardActionBase.CheckWuXing：该五行是否处于激活态。</summary>
    public static bool CheckWuXing(Combatant src, BuffType jihuoWuXing)
    {
        if (src.HasBuff(jihuoWuXing)) return true;
        if (src.HasBuff(BuffType.LongMaJingShen)) return true;
        if (src.HasBuff(BuffType.UsedWuXing))
        {
            var used = (BuffType)src.GetBuffValue(BuffType.UsedWuXing);
            if (used == jihuoWuXing) return true;
            // 相生：用过的那个元素生出的元素也算激活。
            if (GetXiangSheng(used) == jihuoWuXing) return true;
            // 天赋137：只要涉及火灵就算。
            if (src.HasTalent(137) && (used == BuffType.JiHuoHuoLing || jihuoWuXing == BuffType.JiHuoHuoLing)) return true;
        }
        // 牌库里有五行聚灵牌（7030077 / 7040077）也算激活。
        if (src.Subs.HasCardInDeck(src, 7030077) || src.Subs.HasCardInDeck(src, 7040077)) return true;
        return false;
    }

    /// <summary>相生环：金→水、水→木、木→火、火→土、土→金（GetKeZhiWuXing 的反向环）。</summary>
    public static BuffType GetXiangSheng(BuffType e) => e switch
    {
        BuffType.JiHuoJinLing => BuffType.JiHuoShuiLing,
        BuffType.JiHuoShuiLing => BuffType.JiHuoMuLing,
        BuffType.JiHuoMuLing => BuffType.JiHuoHuoLing,
        BuffType.JiHuoHuoLing => BuffType.JiHuoTuLing,
        BuffType.JiHuoTuLing => BuffType.JiHuoJinLing,
        _ => e,
    };

    /// <summary>克制环：木→土→水→火→金→木（BattleCharacter.GetKeZhiWuXing）。</summary>
    public static BuffType GetKeZhiWuXing(BuffType e) => e switch
    {
        BuffType.JiHuoMuLing => BuffType.JiHuoTuLing,
        BuffType.JiHuoTuLing => BuffType.JiHuoShuiLing,
        BuffType.JiHuoShuiLing => BuffType.JiHuoHuoLing,
        BuffType.JiHuoHuoLing => BuffType.JiHuoJinLing,
        BuffType.JiHuoJinLing => BuffType.JiHuoMuLing,
        _ => e,
    };

    /// <summary>GetWuXingName：取牌名里的五行名（无则空串）。</summary>
    public static string GetWuXingName(string name)
    {
        foreach (var (n, _) in NameMap) if (name.Contains(n)) return n;
        return "";
    }

    /// <summary>IsXiangSheng(a, b)：a 的元素生出 b 的元素（xiangShengZhiHuo 时火可配任意）。</summary>
    public static bool IsXiangSheng(string a, string b, bool xiangShengZhiHuo)
    {
        if (!ContainsAny(a) || !ContainsAny(b)) return false;
        if (IsXiangShengPair(a, "金灵", b, "水灵") || IsXiangShengPair(a, "木灵", b, "火灵")
            || IsXiangShengPair(a, "土灵", b, "金灵") || IsXiangShengPair(a, "水灵", b, "木灵")
            || IsXiangShengPair(a, "火灵", b, "土灵")) return true;
        if (xiangShengZhiHuo && ((a.Contains("火灵") && !b.Contains("火灵")) || (!a.Contains("火灵") && b.Contains("火灵"))))
            return true;
        return false;
    }

    private static bool IsXiangShengPair(string a, string an, string b, string bn)
        => a.Contains(an) && b.Contains(bn);

    /// <summary>IsXiangSheng(wuxing, cardName)：已激活的 wuxing 生出这张牌的元素。</summary>
    public static bool IsXiangSheng(BuffType wuxing, string cardName, bool xiangShengZhiHuo)
    {
        if (!ContainsAny(cardName)) return false;
        if ((wuxing == BuffType.JiHuoTuLing && cardName.Contains("金灵"))
            || (wuxing == BuffType.JiHuoShuiLing && cardName.Contains("木灵"))
            || (wuxing == BuffType.JiHuoHuoLing && cardName.Contains("土灵"))
            || (wuxing == BuffType.JiHuoMuLing && cardName.Contains("火灵"))
            || (wuxing == BuffType.JiHuoJinLing && cardName.Contains("水灵"))) return true;
        if (xiangShengZhiHuo && ((wuxing == BuffType.JiHuoHuoLing && !cardName.Contains("火灵"))
                                 || (wuxing != BuffType.JiHuoHuoLing && cardName.Contains("火灵")))) return true;
        return false;
    }

    /// <summary>IsKeZhi(a, b)：a 的元素克 b 的元素。</summary>
    public static bool IsKeZhi(string a, string b)
        => (a.Contains("金灵") && b.Contains("木灵")) || (a.Contains("木灵") && b.Contains("土灵"))
        || (a.Contains("土灵") && b.Contains("水灵")) || (a.Contains("水灵") && b.Contains("火灵"))
        || (a.Contains("火灵") && b.Contains("金灵"));

    /// <summary>isCardNameActived：牌名对应的五行已激活。</summary>
    public static bool IsCardNameActived(Combatant src, string cardName)
    {
        foreach (var (n, buff) in NameMap)
            if (cardName.Contains(n) && src.HasBuff(buff)) return true;
        return false;
    }

    /// <summary>ActiveWuXingInName：按牌名激活五行（出牌前调用，永久累加）。</summary>
    public static bool ActiveWuXingInName(Combatant src, string name)
    {
        bool any = false;
        foreach (var (n, buff) in NameMap)
        {
            if (name.Contains(n)) { src.ModifyBuffValue(buff, 1); any = true; }
        }
        return any;
    }

    /// <summary>JiHuoWuXingInName：同 ActiveWuXingInName，但命中即返回（等价语义）。</summary>
    public static bool JiHuoWuXingInName(Combatant src, string cardName) => ActiveWuXingInName(src, cardName);

    /// <summary>GetWuXingActiveCount：已激活的五行**种类数**。</summary>
    public static int GetWuXingActiveCount(Combatant src)
    {
        int n = 0;
        foreach (var b in Elements) if (src.HasBuff(b)) n++;
        return n;
    }

    /// <summary>GetWuXingActiveNumber：已激活五行的**层数之和**。</summary>
    public static int GetWuXingActiveNumber(Combatant src)
    {
        int n = 0;
        foreach (var b in Elements) if (src.HasBuff(b)) n += src.GetBuffValue(b);
        return n;
    }

    /// <summary>
    /// GetWuXingCountInDeck：整副牌里出现的五行**种类数**（+若干加成）。
    /// 原码：按牌名收集不重复的元素名；baseId 292 取 otherParams[1] 的最小值累加；
    /// baseId 7000101 累加 otherParams[0]；刻印76、仙命147（有水灵则 +1）。
    /// 仙命 417：私有天赋数据 199（五行玉屏选的牌）也按牌名计入种类。
    ///   ⚠ 原码 `privateData.talentDatas[199]` 是字典下标，没有这条数据时会抛异常（真实对局里 417 与 199 成对出现）；sim 按空表。
    /// </summary>
    public static int GetWuXingCountInDeck(Combatant src)
    {
        var seen = new List<string>();
        bool has292 = false;
        int min292 = 0;
        int add7000101 = 0;
        foreach (var card in src.Board)
        {
            int baseId = CardTypes.BaseId(card.Id);
            if (baseId == 292)
            {
                // 原码：`num3 = o[1]; num = o[1]; if (num > num3) num = num3;` —— 那个「取小」永远不成立，
                // 实际是**最后一张** 292 的 o[1] 覆盖前面的（早先 sim 取了最小值）。
                min292 = src.Config.CardOtherParams(card.Id).At(1);
                has292 = true;
            }
            if (baseId == 7000101) add7000101 += src.Config.CardOtherParams(card.Id).At(0);
            string name = src.Config.CardName(card.Id);
            foreach (var (n, _) in NameMap)
                if (name.Contains(n) && !seen.Contains(n)) seen.Add(n);
        }
        if (src.HasFateStrategy(417) && src.PrivateTalentDatas.TryGetValue(199, out var yp))
            foreach (int id in yp)
            {
                string name2 = src.Config.CardName(id);
                foreach (var (n, _) in NameMap)
                    if (name2.Contains(n) && !seen.Contains(n)) seen.Add(n);
            }
        int count = seen.Count + add7000101;
        if (src.Subs.HasKeYinType(src, 76)) count += src.Subs.KeYinOtherparam(src, 76, 0);
        if (has292 && min292 > 0) count += min292;
        if (src.HasFateStrategy(147) && seen.Contains("火灵")) count++;
        return count;
    }
}
