using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 牌型谓词（P5）：忠实移植自 BattleCharacter.Is*（崩拳/云剑/狂剑/灵剑/剑阵/星弈/普通攻击）+ CardFactory.GetBaseCardId。
/// 判定 = 牌名 contains 关键字 + baseId 列表 + buff/仙命/炼化 gate（后者读 combatant）。
/// 灵悟牌（天赋 192 + talentDatas[189]）、卡 19 被天赋改名都已接入；只按牌描述判的少数 OR 扩展分支仍跳过并注明；
///   主判定（名 + baseId + 常见 buff/仙命/炼化）已忠实，足以点亮加攻/仙命/炼化里的牌型分支。
/// </summary>
public static class CardTypes
{
    /// <summary>GetCardRarity：id/10000%100。</summary>
    public static int Rarity(int cardId) => (cardId / 10000) % 100;

    /// <summary>GetBaseCardId：id - rarity*10000。</summary>
    public static int BaseId(int cardId) => cardId - Rarity(cardId) * 10000;

    /// <summary>普通攻击：baseId ∈ {0, 286}。</summary>
    public static bool IsPuTongGongJi(int cardId)
    {
        int b = BaseId(cardId);
        return b == 0 || b == 286;
    }

    public static bool IsBengQuan(Combatant c, int cardId)
    {
        if (c.Config.CardName(cardId).Contains("崩拳") || c.HasBuff(BuffType.BengTianBu)) return true;
        int b = BaseId(cardId);
        if (b == 10000080 || b == 10000087) return true;
        if (c.HasBuff(BuffType.XiaZhangPaiSuanBengQuan)) return true;
        return false;
    }

    /// <summary>牌名：卡 19 带 10096 / 20096 / 30096 时是改过的名字（狂剑•澄心 / 云剑•澄心 / 澄心•无极），其余取配置。</summary>
    public static string NameOf(Combatant c, int cardId) => Card19Functions.NameOf(c, cardId) ?? c.Config.CardName(cardId);

    /// <summary>
    /// 这张牌的境界：卡上被效果改过（<see cref="BattleCard.Level"/> &gt; 0，目前只有卡 19）就用卡上的，
    /// 否则取卡表静态值。原码里读的 <c>cardConfig.level</c> 就是「改过之后的配置」，所以两者都要看。
    /// </summary>
    public static int LevelOf(Combatant c, BattleCard card) => card.Level > 0 ? card.Level : c.Config.CardLevel(card.Id);

    /// <summary>同上，剑意（<see cref="BattleCard.JianYi"/> &gt; 0 优先）。</summary>
    public static int JianYiOf(Combatant c, BattleCard card) => card.JianYi > 0 ? card.JianYi : c.Config.CardJianYi(card.Id);

    public static bool IsYunJian(Combatant c, int cardId)
    {
        string n = NameOf(c, cardId);
        if (n.Contains("云剑") || c.HasBuff(BuffType.XiaZhangPaiSuanZuoYunJian)) return true;
        if (cardId == 19 && c.HasTalent(20096)) return true;
        if (c.HasBuff(BuffType.KuangLongTunYun) && IsKuangJian(c, cardId)) return true;
        if (c.HasBuff(BuffType.YunJianZhouTian) && c.Config.CardCareer(cardId) == CareerZhenFaShi) return true;
        int b = BaseId(cardId);
        if (b == 213) return true;
        if (c.Subs.HasKeYinType(c, 146) && b == 1000025) return true;
        // 万能剑：层数记账 WanNengJianShengXiaoCengJi ≤ WanNengJian 时，任何牌都算云剑（记账 / 扣层见 CardHookFunctions）。
        if (b == 261 || (c.HasBuff(BuffType.WanNengJian)
                         && c.GetBuffValue(BuffType.WanNengJianShengXiaoCengJi) <= c.GetBuffValue(BuffType.WanNengJian))) return true;
        if (c.IsLingWuCard(cardId)) return true;   // 天赋 192 + 灵悟牌（talentDatas[189]，原码 13599 / 13674 / 13738 / 13766）
        return false;
    }

    public static bool IsKuangJian(Combatant c, int cardId)
    {
        string n = NameOf(c, cardId);
        if (n.Contains("狂剑")) return true;
        if (c.HasBuff(BuffType.KuangLongTunYun) && (n.Contains("云剑") || c.HasBuff(BuffType.XiaZhangPaiSuanZuoYunJian))) return true;
        int b = BaseId(cardId);
        if (b == 213 || b == 331 || b == 401 || b == 261) return true;
        if (c.Config.CardCareer(cardId) == CareerQinShi && c.HasFateStrategy(319)) return true;
        if (c.HasFateStrategy(322) && n.Contains("猫")) return true;
        if (c.HasFateStrategy(381) && c.Config.CardDesc(cardId).Contains("[击伤]")) return true;
        if (c.Config.CardRarity(cardId) >= 1 && c.GetBuffValue(BuffType.KuangJianYunJi) > 0) return true;
        if (c.Subs.HasKeYinType(c, 146) && b == 1000025) return true;
        // 万能剑 / 下张牌算作狂剑（原码 ignoreTempBuff 缺省 false，唯一传 true 的调用点是 IsJianZhen，与此无关）。
        if (c.HasBuff(BuffType.WanNengJian)
            && c.GetBuffValue(BuffType.WanNengJianShengXiaoCengJi) <= c.GetBuffValue(BuffType.WanNengJian)) return true;
        if (c.HasBuff(BuffType.XiaZhangPaiSuanZuoKuangJian)
            && c.GetBuffValue(BuffType.XiaZhangPaiSuanZuoKuangJianCengJi) <= c.GetBuffValue(BuffType.XiaZhangPaiSuanZuoKuangJian)) return true;
        if (c.IsLingWuCard(cardId)) return true;   // 天赋 192 + 灵悟牌（talentDatas[189]，原码 13599 / 13674 / 13738 / 13766）
        return false;
    }

    /// <summary>
    /// IsJian（13568）：牌名含「剑」；例外是「id == 19 且带天赋 30096」那张不算。
    /// 目前只被「剑气当额外伤害打出去」（OnAfterExecuted 23427）用到。
    /// </summary>
    /// <summary>Career 枚举（原码 408398）：QinShi = 3、ZhenFaShi = 5。</summary>
    private const int CareerQinShi = 3;
    private const int CareerZhenFaShi = 5;

    public static bool IsJian(Combatant c, int cardId)
        => NameOf(c, cardId).Contains("剑") && (cardId != 19 || !c.HasTalent(30096));

    public static bool IsLingJian(Combatant c, int cardId)
    {
        if (NameOf(c, cardId).Contains("灵剑")) return true;
        int b = BaseId(cardId);
        if (b == 213) return true;
        if (c.Subs.HasKeYinType(c, 146) && b == 1000025) return true;
        if (c.IsLingWuCard(cardId)) return true;   // 天赋 192 + 灵悟牌（talentDatas[189]，原码 13599 / 13674 / 13738 / 13766）
        return false;
    }

    public static bool IsJianZhen(Combatant c, int cardId)
    {
        if (NameOf(c, cardId).Contains("剑阵")) return true;
        if (BaseId(cardId) == 213) return true;
        if (c.IsLingWuCard(cardId)) return true;   // 天赋 192 + 灵悟牌（talentDatas[189]，原码 13599 / 13674 / 13738 / 13766）
        // 卡组里有 312 → 非隐藏、非秘术的狂剑牌也算剑阵（312 自己是隐藏牌，不算）。
        bool has312 = false;
        for (int j = 0; j < c.Board.Count; j++)
            if (BaseId(c.Board[j].Id) == 312) { has312 = true; break; }
        if (has312 && IsKuangJian(c, cardId))
        {
            var meta = (c.Config as Config.JsonBattleConfig)?.Card(cardId);
            if (meta is not null && !meta.Hidden && meta.Subcategory != SubcategoryMiShu) return true;
        }
        return false;
    }

    /// <summary>
    /// YiGuaZiJieCheck（原码 24791）：有天赋 197，且出战牌组（lastRoundData.usedCards = 本回合摆上的牌）里
    /// **恰有 2 张**八卦牌（巽 / 坤 / 震 / 坎 / 艮 / 兑 / 离 / 乾）。
    /// </summary>
    public static bool YiGuaZiJieCheck(Combatant c)
    {
        if (!c.HasTalent(197)) return false;
        int n = 0;
        for (int i = 0; i < c.Board.Count; i++)
        {
            switch (BaseId(c.Board[i].Id))
            {
                case 4000001: case 4000002: case 4000003: case 4000015:
                case 4000016: case 4000025: case 4000034: case 4000026:
                    n++;
                    break;
            }
        }
        return n == 2;
    }

    /// <summary>Subcategory 枚举里的 MiShu（秘术）＝ 4。</summary>
    private const int SubcategoryMiShu = 4;

    public static bool IsXingYi(Combatant c, int cardId)
        => c.Config.CardName(cardId).Contains("星弈");

    /// <summary>IsLianBeng：连崩 —— baseId==10000035，或 id 恰为 10030082/10040082（注意判的是**整 id**）。</summary>
    public static bool IsLianBeng(int cardId)
        => BaseId(cardId) == 10000035 || cardId == 10030082 || cardId == 10040082;

    /// <summary>后招标记：只存在于<b>卡描述文本</b>里，没有任何结构化字段（实测 66 张卡带它）。</summary>
    public const string HouZhaoMarker = "[后招]：";

    /// <summary>IsHouZhao：卡描述含 <c>[后招]：</c>。</summary>
    public static bool IsHouZhao(Combatant c, int cardId)
        => c.Config.CardDesc(cardId).Contains(HouZhaoMarker);
}
