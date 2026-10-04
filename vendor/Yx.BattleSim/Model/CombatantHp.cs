using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;

namespace Yx.BattleSim.Model;

/// <summary>
/// 血量管线：ModifyHp / ModifyMaxHp / AfterHpModifyEffect / OnHit —— 忠实移植自
/// BattleCharacter 同名方法（剥去动画/音效/浮字，保留全部数值与 buff 联动分支）。
/// 依赖 <see cref="Combatant.Config"/> 取 magic number、<see cref="Combatant.Subs"/> 走未移植子系统。
/// ⚠ 复活（CanRevive）与部分子系统分支属 P5，未移植前中性；见覆盖账。
/// </summary>
public sealed partial class Combatant
{
    private const int OpenTongXiuHouShouBuChang = 1; // OpenType 占位；DefaultSubsystems 视为开启（实机开着），中性桩返回 false

    /// <summary>
    /// 改血，返回实际生效的变化量（负=掉血）。忠实 ModifyHp。
    /// </summary>
    public int ModifyHp(int hpDelta, int fengRui = 0, bool canRevive = false, bool isCost = false)
    {
        int r = ModifyHpCore(hpDelta, fengRui, canRevive, isCost);
        StepLog.Snap(this);   // 逐步对拍：对应探针挂在 ModifyHp 上的后置钩子
        return r;
    }

    private int ModifyHpCore(int hpDelta, int fengRui, bool canRevive, bool isCost)
    {
        if (Subs.IsOpen(OpenTongXiuHouShouBuChang)) canRevive = true;
        if (HasBuff(BuffType.JinZhiFuHuo)) canRevive = false;

        if (isCost)
        {
            Hp += hpDelta;
            AfterHpModifyEffect(hpDelta);
            if (Subs.IsTalentResonanceEffective(this, 50) && hpDelta <= -1) ModifyHp(2);
            if (HasFateStrategy(149) && hpDelta <= -1) ModifyTiPo(1);
            return hpDelta;
        }

        if (hpDelta == 0) return 0;

        if (hpDelta < 0 && HasBuff(BuffType.XiaHuiHeKaiShiQianBuZaiSunShiShengMing)) hpDelta = 0;

        if (hpDelta > 0 && HasBuff(BuffType.WuFaJiaShengMing)) return 0;

        if (hpDelta > 0 && HasBuff(BuffType.JinLingYinPlus))
        {
            ModifyBuffValue(BuffType.FengRui, hpDelta);
            return 0;
        }

        if (hpDelta > 0 && Hp <= 0 && !canRevive) return 0;

        // 护体：挡下一次掉血并消耗（对方无「无视护体」时）。
        if (HasBuff(BuffType.HuTi) && hpDelta < 0 && !Opponent.HasBuff(BuffType.WuShiHuTi))
        {
            ModifyBuffValue(BuffType.HuTi, -1);
            ModifyBuffValue(BuffType.TempHuTi, -1);
            return 0;
        }

        // 移花接木：掉血转为等量回血并消耗。
        if (HasBuff(BuffType.YiHuaJieMu) && hpDelta < 0)
        {
            ModifyBuffValue(BuffType.YiHuaJieMu, -1);
            return ModifyHp(-hpDelta);
        }

        if (HasBuff(BuffType.MengBuDongJinGangZhen) && hpDelta < 0) hpDelta = -1;

        // 夜遁化：掉血的一半优先由防御吸收。
        if (hpDelta < 0 && HasBuff(BuffType.YeDunHua))
        {
            int absorb = -hpDelta / 2;
            if (absorb > 0 && Def > 0)
            {
                if (Def < absorb) absorb = Def;
                ModifyDef(-absorb);
                hpDelta += absorb;
            }
        }

        if (hpDelta > 0 && HasBuff(BuffType.DanHuangZong) && GetBuffValue(BuffType.DanHuangZong) > 0)
        {
            ModifyBuffValue(BuffType.ShiYu, 1);
            ModifyBuffValue(BuffType.DanHuangZong, -1);
        }

        if (hpDelta > 0)
        {
            if (HasFateStrategy(394)) hpDelta++;
            if (HasBuff(BuffType.HongZaoZong))
            {
                hpDelta += GetBuffValue(BuffType.HongZaoZong);
                RemoveBuff(BuffType.HongZaoZong);
            }
            if (HasBuff(BuffType.ShiYu)) hpDelta += GetBuffValue(BuffType.ShiYu);
            if (HasBuff(BuffType.ShiZhi)) hpDelta -= GetBuffValue(BuffType.ShiZhi);
            if (hpDelta <= 0) hpDelta = 1;
        }

        if (hpDelta > 0 && HasBuff(BuffType.ShunYing))
            hpDelta += CeilDiv(hpDelta * Config.CardOtherParams(11000019).At(0), 100);

        if (hpDelta > 0)
        {
            int addMax = 0;
            for (int i = 0; i < 4; i++)
            {
                int talentId = i * 10000 + 120;
                if (HasTalent(talentId)) addMax += Config.TalentOtherParams(talentId).At(0);
            }
            if (addMax > 0) ModifyMaxHp(addMax);
        }

        if (HpTrace is not null && hpDelta != 0)
        {
            var st = new System.Diagnostics.StackTrace(1, false);
            var chain = string.Join("<", st.GetFrames().Take(4)
                .Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name));
            HpTrace($"{Uid} hp {Hp} {(hpDelta > 0 ? "+" : "")}{hpDelta} [{chain}]");
        }
        int applied = hpDelta;
        int overheal = 0;
        Hp += hpDelta;
        if (Hp > MaxHp)
        {
            if (HasBuff(BuffType.ShengJiZhanFang)) ModifyMaxHp(Hp - MaxHp);
            else if (hpDelta > 0) overheal = Hp - MaxHp;
            Hp = MaxHp;
        }
        applied -= overheal;

        if (hpDelta > 0)
        {
            if (Hp > GetBuffValue(BuffType.JiLuZhanDouZuiGaoShengMing))
                SetBuffValue(BuffType.JiLuZhanDouZuiGaoShengMing, Hp);
            ModifyBuffValue(BuffType.AddHpCount, hpDelta);
            ModifyBuffValue(BuffType.HuiHeJiaShengMing, hpDelta);
            ModifyBuffValue(BuffType.JiaShengMingCiShu, 1);
            if (HasBuff(BuffType.YeDuZhiYin))
            {
                ModifyBuffValue(BuffType.YeDuZhiYin, -1);
                ModifyBuffValue(BuffType.ExActionAgain, 1);
            }
        }

        AfterHpModifyEffect(applied);

        if (hpDelta > 0 && HasBuff(BuffType.NiShi))
        {
            int back = (int)Math.Floor(hpDelta * (double)Config.CardOtherParams(11000020).At(0) / 100.0);
            if (back > 0)
                CombatMath.ApplyDamage(this, Opponent, DamageInfo.Create(this, DamageType.ReflectDamage, back, skipWoundCheck: true));
        }
        if (hpDelta > 0 && HasBuff(BuffType.MengKuiRanBuDong) && !HasBuff(BuffType.MengKuiRanCiShu))
        {
            ModifyBuffValue(BuffType.MengKuiRanCiShu, 1);
            int back = (int)Math.Floor(hpDelta * (double)GetBuffValue(BuffType.MengKuiRanBuDong) / 100.0);
            if (back > 0)
                CombatMath.ApplyDamage(this, Opponent, DamageInfo.Create(this, DamageType.ReflectDamage, back, skipWoundCheck: true));
            RemoveBuff(BuffType.MengKuiRanCiShu);
        }

        return applied;
    }

    /// <summary>改上限血。忠实 ModifyMaxHp。</summary>
    public void ModifyMaxHp(int delta)
    {
        ModifyMaxHpCore(delta);
        StepLog.Snap(this);
    }

    private void ModifyMaxHpCore(int delta)
    {
        if (delta == 0) return;
        if (delta > 0 && HasFateStrategy(394)) delta++;
        if (delta > 0 && HasBuff(BuffType.ShunYing))
            delta += CeilDiv(delta * Config.CardOtherParams(11000019).At(0), 100);
        if (delta < 0) ModifyBuffValue(BuffType.JianShengMingShangXianCiShu, 1);
        if (delta < 0 && Opponent.HasBuff(BuffType.KunWuRongHuoHuan))
            delta -= Opponent.GetBuffValue(BuffType.KunWuRongHuoHuan);
        if (delta < 0 && Opponent.HasBuff(BuffType.JianShengMingShangXianDuoJian))
            delta -= Opponent.GetBuffValue(BuffType.JianShengMingShangXianDuoJian);
        if (delta < 0 && HasBuff(BuffType.MengBuDongJinGangZhen)) delta = -1;
        if (delta < 0 && Opponent.HasBuff(BuffType.JianShengMingShangXianJiaFang))
            Opponent.ModifyDef(Opponent.GetBuffValue(BuffType.JianShengMingShangXianJiaFang));

        MaxHp += delta;
        if (MaxHp < 0) MaxHp = 0;
        if (Hp > MaxHp) Hp = MaxHp;

        if (HasBuff(BuffType.YanQi) && delta > 0)
        {
            ModifyBuffValue(BuffType.YanQi, -1);
            ModifyHp(MaxHp * Config.FateOtherParams(326).At(0) / 100);
        }
        if (delta < 0) ModifyBuffValue(BuffType.LoseMaxHpCount, -delta);
    }

    /// <summary>改血/改上限后的联动效果。忠实 AfterHpModifyEffect。</summary>
    public void AfterHpModifyEffect(int hpDelta)
    {
        if (hpDelta < 0 && HasBuff(BuffType.LingGuiMiZongBu) && !HasBuff(BuffType.LingGuiMiZongBuShengXiao))
        {
            ModifyBuffValue(BuffType.LingGuiMiZongBuShengXiao, 1);
            ModifyAnima(GetBuffValue(BuffType.LingGuiMiZongBu));
            ModifyBuffValue(BuffType.ShenFa, GetBuffValue(BuffType.LingGuiMiZongBu));
        }
        if (hpDelta < 0 && HasBuff(BuffType.MingYeMiZongBu) && !HasBuff(BuffType.MingYeMiZongBuShengXiao))
        {
            ModifyBuffValue(BuffType.MingYeMiZongBuShengXiao, 1);
            ModifyAnima(GetBuffValue(BuffType.MingYeMiZongBu));
            ModifyBuffValue(BuffType.ShenFa, GetBuffValue(BuffType.MingYeMiZongBu));
        }
        if (HasTalent(64) && hpDelta != 0) ModifyDef(Config.TalentOtherParams(64).At(0));
        if (Subs.HasKeYinType(this, 147) && hpDelta != 0) ModifyDef(Subs.KeYinOtherparam(this, 147, 0));
        if (hpDelta < 0 && HasBuff(BuffType.BingFengXueLian))
        {
            ModifyBuffValue(BuffType.BingFengXueLian, -1);
            ModifyDef(-hpDelta);
        }
        if (hpDelta < 0 && HasBuff(BuffType.MengDuanYa))
        {
            int divisor = Config.CardOtherParams(7040086).At(1);
            if (divisor != 0)
            {
                int add = -(int)Math.Floor((float)hpDelta / divisor);
                if (add > 0) ModifyBuffValue(BuffType.XiaHuiHeJiaFang, add);
            }
        }
        if (hpDelta < 0 && GetBuffValue(BuffType.XueGuangZhiZai) > 0)
        {
            ModifyBuffValue(BuffType.WaiShang, 1);
            ModifyBuffValue(BuffType.XueGuangZhiZai, -1);
        }
        if (hpDelta < 0 && GetBuffValue(BuffType.XuanMinJuLiZhen) > 0)
        {
            ModifyBuffValue(BuffType.JiaGong, 1);
            ModifyBuffValue(BuffType.XuanMinJuLiZhen, -1);
        }
        if (HasBuff(BuffType.YanQi) && hpDelta > 0)
        {
            ModifyBuffValue(BuffType.YanQi, -1);
            ModifyHp(MaxHp * Config.FateOtherParams(326).At(0) / 100);
        }
        if (hpDelta < 0)
        {
            ModifyBuffValue(BuffType.LoseHpTimesCount, 1);
            ModifyBuffValue(BuffType.LoseHpCount, -hpDelta);
        }
    }

    /// <summary>落血入口（ApplyDamage 末尾调）。返回实际造成的伤害。忠实 OnHit。</summary>
    public int OnHit(DamageInfo info)
    {
        int result = info.Damage;
        bool blocked = HasBuff(BuffType.HuTi) && !info.Source.HasBuff(BuffType.WuShiHuTi);

        if (info.Damage > 0 || (info.Damage == 0 && !info.HitDef))
        {
            result = -ModifyHp(-info.Damage, info.FengRui);
        }

        if (info.Type != DamageType.ReflectDamage && info.Damage > 0 && !blocked)
        {
            if (!(Hp > 0 || CanRevive() || DeathRules.CheckSiZhan(this)) && !IsDead)
            {
                OnDead();
            }
        }
        return result;
    }

    internal static int CeilDiv(int a, int b) => b == 0 ? 0 : (a + b - 1) / b;
}
