using System.Linq;
using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;

namespace Yx.BattleSim.Model;

/// <summary>
/// 防御管线：ModifyDef —— 忠实移植自 BattleCharacter.ModifyDef（剥浮字/特效，保留全部加防联动分支）。
/// 刻印(93/124)分支结构就位、走 <see cref="Combatant.Subs"/>，未移植时中性；见覆盖账 P4。
/// </summary>
public sealed partial class Combatant
{
    /// <summary>改防。忠实 ModifyDef（含各类「加防时额外加防」联动）。</summary>
    public void ModifyDef(int defDelta)
    {
        ModifyDefCore(defDelta);
        StepLog.Snap(this);
    }

    private void ModifyDefCore(int defDelta)
    {
        if (defDelta == 0) return;
        if (defDelta < 0 && Def + defDelta < 0) defDelta = -Def;

        if (defDelta > 0 && HasBuff(BuffType.MengYinLeiZhen)) return; // 不能加防
        if (defDelta > 0 && HasTalent(38)) defDelta += Config.TalentOtherParams(38).At(0);
        if (defDelta > 0 && HasFateStrategy(26)) defDelta += Config.FateOtherParams(26).At(0);
        if (defDelta > 0 && HasFateStrategy(382)) defDelta++;
        if (defDelta > 0 && Subs.IsTalentResonanceEffective(this, 20))
            defDelta += Config.ResonanceOtherParams(20).At(1);
        if (defDelta > 0 && Subs.IsTalentResonanceEffective(this, 75) && !Subs.CheckTalentResonanceTempFlag(this, 75))
        {
            Subs.SetTalentResonanceTempFlag(this, 75, true);
            defDelta += Config.ResonanceOtherParams(75).At(0);
        }
        if (defDelta > 0 && HasBuff(BuffType.KunWuJinHuan)) defDelta += GetBuffValue(BuffType.KunWuJinHuan);
        if (defDelta > 0 && HasBuff(BuffType.KunWuRongHuoHuan)) defDelta += GetBuffValue(BuffType.KunWuRongHuoHuan);

        // 刻印·结阵(93)：加防时额外加防（次数有上限），命中时记 flag —— 落防后还要反伤一次。
        bool jieZhen = false;
        if (defDelta > 0 && Subs.HasKeYinType(this, 93)
            && GetBuffValue(BuffType.KeYinJieZhenCiShu) < Subs.KeYinOtherparam(this, 93, 2))
        {
            defDelta += Subs.KeYinOtherparam(this, 93, 0);
            jieZhen = true;
            ModifyBuffValue(BuffType.KeYinJieZhenCiShu, 1);
        }
        if (defDelta > 0 && HasBuff(BuffType.XiaCiJiaFangDuoJia))
        {
            defDelta += GetBuffValue(BuffType.XiaCiJiaFangDuoJia);
            RemoveBuff(BuffType.XiaCiJiaFangDuoJia);
        }
        // 刻印 124：每张 124 刻印消耗一层机关刻文换 o[1] 防。早先是空桩。
        if (defDelta > 0 && Subs.HasKeYinType(this, 124))
        {
            int exDef = 0;
            foreach (int keyin in BattleKeYinCards)
            {
                if (keyin % 10000 == 124 && GetBuffValue(BuffType.JiGuanKeWen) > 0)
                {
                    ModifyBuffValue(BuffType.JiGuanKeWen, -1);
                    exDef += Config.KeYinOtherParams(keyin).At(1);
                }
            }
            defDelta += exDef;
        }
        if (defDelta > 0 && HasBuff(BuffType.ShunYing))
            defDelta += CeilDiv(defDelta * Config.CardOtherParams(11000019).At(0), 100);

        Def += defDelta;
        if (Def < 0) Def = 0;
        // 诊断：`trace --hp` 时连同防御变化一起打出来（带调用链），查「防从哪来 / 被谁扣掉」。
        if (HpTrace is not null && defDelta != 0)
        {
            var st = new System.Diagnostics.StackTrace(1, false);
            var chain = string.Join("<", st.GetFrames().Take(4)
                .Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name));
            HpTrace($"{Uid} def {Def} {(defDelta > 0 ? "+" : "")}{defDelta} [{chain}]");
        }

        // ---- 落防后联动（对应原码 ModifyDef 尾部，按 defDelta 正负分支，顺序照原码）----
        // 逆势：加防时按比例（11000020.o[0]%）反伤。早先缺失。
        if (defDelta > 0 && HasBuff(BuffType.NiShi))
        {
            int n = defDelta * Config.CardOtherParams(11000020).At(0) / 100;
            if (n > 0) CombatMath.ApplyDamage(this, Opponent, DamageInfo.Create(this, DamageType.ReflectDamage, n, skipWoundCheck: true));
        }
        // 梦•岿然不动：加防时按层数百分比反伤（CiShu 防重入）。早先缺失。
        if (defDelta > 0 && HasBuff(BuffType.MengKuiRanBuDong) && !HasBuff(BuffType.MengKuiRanCiShu))
        {
            ModifyBuffValue(BuffType.MengKuiRanCiShu, 1);
            int n = defDelta * GetBuffValue(BuffType.MengKuiRanBuDong) / 100;
            if (n > 0) CombatMath.ApplyDamage(this, Opponent, DamageInfo.Create(this, DamageType.ReflectDamage, n, skipWoundCheck: true));
            RemoveBuff(BuffType.MengKuiRanCiShu);
        }
        // 刻印·结阵(93) 的后半：本次命中过就反伤 o[1]。早先缺失。
        if (defDelta > 0 && Subs.HasKeYinType(this, 93) && jieZhen)
        {
            int n = Subs.KeYinOtherparam(this, 93, 1);
            if (n > 0) CombatMath.ApplyDamage(this, Opponent, DamageInfo.Create(this, DamageType.ReflectDamage, n, skipWoundCheck: true));
        }
        if (defDelta > 0 && HasBuff(BuffType.JiaFangZaoChengShangHai))
        {
            int bv = GetBuffValue(BuffType.JiaFangZaoChengShangHai);
            if (bv > 0)
                CombatMath.ApplyDamage(this, Opponent, DamageInfo.Create(this, DamageType.ReflectDamage, bv, skipWoundCheck: true));
        }
        if (defDelta < 0)
            ModifyBuffValue(BuffType.LoseDefCount, -defDelta);
        // 断崖：掉防时反伤（刻印77 加倍）、消耗。
        if (defDelta < 0 && HasBuff(BuffType.DuanYa))
        {
            ModifyBuffValue(BuffType.DuanYa, -1);
            int n = -defDelta;
            if (Subs.HasKeYinType(this, 77)) n *= 2;
            CombatMath.ApplyDamage(this, Opponent, DamageInfo.Create(this, DamageType.ReflectDamage, n, skipWoundCheck: true));
        }
        // 梦断崖：掉防时按「掉的量 / 7040086.o[0]」挂下回合加防。早先缺失。
        if (defDelta < 0 && HasBuff(BuffType.MengDuanYa))
        {
            int dv = Config.CardOtherParams(7040086).At(0);
            int n = dv == 0 ? 0 : -(int)MathF.Floor((float)defDelta / dv);
            if (n > 0) ModifyBuffValue(BuffType.XiaHuiHeJiaFang, n);
        }
        // 合八荒：掉防时把掉的那部分加回来、消耗。
        if (defDelta < 0 && HasBuff(BuffType.HeBaHuang))
        {
            ModifyBuffValue(BuffType.HeBaHuang, -1);
            ModifyDef(-defDelta);
        }
        if (defDelta > 0)
        {
            ModifyBuffValue(BuffType.JiLuJiaFang, defDelta);
            ModifyBuffValue(BuffType.JiaFangCiShu, 1);
        }
    }
}
