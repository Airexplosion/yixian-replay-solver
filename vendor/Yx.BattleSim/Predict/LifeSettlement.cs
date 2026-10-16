using Yx.BattleSim.Config;
using Yx.BattleSim.Model;
namespace Yx.BattleSim.Predict;

public sealed record LifePrediction(int BaseDamage, int ReducedDamage, int Loss, int Remaining, string[] Rules);

// Read-only projection of the standard-mode death display pipeline, audited against the current hot-update assembly.
public static class LifeSettlement
{
    public static LifePrediction Calculate(Combatant loser, int damage)
    {
        int original = damage;
        var rules = new List<string>();
        if (loser.HasBuff(BuffType.CiNianQu) && damage > 0)
        { damage = Math.Max(1, damage - loser.GetBuffValue(BuffType.CiNianQu)); rules.Add("慈念曲"); }
        if (loser.IsTalentResonanceEffective(84) && damage > 0)
        { damage = Math.Max(1, damage - loser.Config.ResonanceOtherParams(84).At(0)); rules.Add("共鸣84"); }
        if (loser.HasTalent(27) && damage > loser.Config.TalentOtherParams(27).At(0))
        { damage = loser.Config.TalentOtherParams(27).At(0); rules.Add("仙命27"); }
        if (loser.HasTalent(45) && damage > 0 && damage % 2 == 0)
        { damage /= 2; rules.Add("仙命45"); }
        int reduced = damage;
        bool immune = Available(loser, 105) && damage >= loser.Config.TalentOtherParams(105).At(0);
        int protection = 0;
        foreach (int id in new[] { 7, 65, 83, 154 })
            if (Available(loser, id) && damage >= loser.TempLife) protection = id;
        if (loser.GetLastRoundPermanentBuffValue(BuffType.TianYanHuaShen) > 0 && damage >= loser.TempLife)
            protection = 354;
        // The game's later branches override the previously selected talent label.
        if (loser.GetLastRoundPermanentBuffValue(BuffType.ZhanBaiShaoKouMingYuan) > 0)
        { damage = Math.Max(0, damage - loser.GetLastRoundPermanentBuffValue(BuffType.ZhanBaiShaoKouMingYuan)); rules.Add("幻境减免"); }
        else if ((loser.IsTalentResonanceEffective(27) && Flag(loser, 27, 0)) ||
                 (loser.HasTalentResonance(106) && Flag(loser, 106, 1)))
        { damage = 0; rules.Add("共鸣免扣"); }
        else if (protection != 0)
        { damage = Math.Max(0, loser.TempLife - 1); rules.Add("保命" + protection); }
        else if (immune)
        { damage = 0; rules.Add("仙命105"); }
        // Trial scroll is displayed as deferred damage; permanent life stays unchanged.
        if (loser.GetLastRoundPermanentBuffValue(BuffType.ShiTanZhiJuan) > 0)
        { damage = 0; rules.Add("试探之卷（延后结算）"); }
        return new(original, reduced, damage, loser.TempLife - damage, rules.ToArray());
    }
    static bool Available(Combatant c, int id) => c.HasTalent(id) && c.TalentTempDatas.TryGetValue(id, out int v) && v > 0;
    static bool Flag(Combatant c, int id, int bit) => c.ResonancePermanentFlags.TryGetValue(id, out int v) && (v & (1 << bit)) != 0;
}
