using Yx.BattleSim.Model;
using Yx.BattleSim.Config;

namespace Yx.BattleSim.Effects;

/// <summary>Effects that run once across both players before either opening sequence.</summary>
public static class SharedBattleStart
{
    private static bool Either(Combatant a, Combatant b, BuffType buff)
        => a.GetLastRoundPermanentBuffValue(buff) > 0 || b.GetLastRoundPermanentBuffValue(buff) > 0;
    private static void Grow(Combatant c, int amount)
    {
        if (amount > 0) { c.ModifyMaxHp(amount); c.ModifyHp(amount); }
    }
    public static void Prepare(Combatant first, Combatant second)
    {
        Grow(first, first.GetLastRoundPermanentBuffValue(BuffType.ZhanDouXianShouJiaShengMing)
            + second.GetLastRoundPermanentBuffValue(BuffType.ZhanDouXianShouJiaShengMing));
        Grow(second, first.GetLastRoundPermanentBuffValue(BuffType.ZhanDouHouShouJiaShengMing)
            + second.GetLastRoundPermanentBuffValue(BuffType.ZhanDouHouShouJiaShengMing));
        if (Either(first, second, BuffType.ShuangFangSuoYouPaiBianErJi))
        {
            LevelTwo(first); LevelTwo(second);
        }
        if (XianMoStrategyFunctions.Active(first, 1009) || XianMoStrategyFunctions.Active(second, 1009))
        {
            int count = first.Config.XianMoOtherParams(1009).At(0);
            UpgradeTail(first, count); UpgradeTail(second, count);
        }
        if (Either(first, second, BuffType.ShuangFangShengMingBianWeiXiangTong))
        {
            int max = Math.Max(first.MaxHp, second.MaxHp), hp = Math.Max(first.Hp, second.Hp);
            first.SetMaxHp(max); second.SetMaxHp(max); first.SetHp(hp); second.SetHp(hp);
        }
        if (first.BattleKeYinCards.Contains(10165) || second.BattleKeYinCards.Contains(10165))
        {
            var a = first.BattleKeYinCards.ToArray(); var b = second.BattleKeYinCards.ToArray();
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                if (a[i] == 10165) (a[i], b[i]) = (b[i], a[i]);
                if (b[i] == 10165) (a[i], b[i]) = (b[i], a[i]);
            }
            first.BattleKeYinCards = a; second.BattleKeYinCards = b;
        }
    }
    private static void LevelTwo(Combatant c)
    {
        for (int i = 0; i < c.Board.Count; i++)
        {
            int id = c.Board[i].Id, rarity = c.Config.CardRarity(id);
            if (rarity == 0 && c.Config.CanUpgrade(id)) c.ReplaceCard(i, c.Config.BuildCard(id + 10000));
            if (rarity == 2 && id != 19) c.ReplaceCard(i, c.Config.BuildCard(id - 10000));
        }
    }
    private static void UpgradeTail(Combatant c, int count)
    {
        for (int i = Math.Max(0, c.Board.Count - count); i < c.Board.Count; i++)
        {
            int id = c.Board[i].Id, rarity = c.Config.CardRarity(id);
            if (rarity == 2 || !c.Config.CanUpgrade(id) || c.Config.CardHidden(id)) continue;
            for (int n = 0; n < 2 - rarity && c.Config.CanUpgrade(c.Board[i].Id); n++)
                c.ReplaceCard(i, c.Config.BuildCard(c.Board[i].Id + 10000));
        }
    }
}
