using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// **出牌付费（耗生命段）**：忠实移植 <c>CardActionBase.CheckCardCost</c>（原码 24151）里
/// 「灵气判定通过之后」的那一整段。
///
/// ⚠ **时序是关键**：战斗循环（原码 15590）的顺序是
///     <c>CheckAdjacentEffects → CheckCardCost（付费 + 下面这串结算）→ Execute（卡效果）</c>，
///   即这些结算**先于本张牌的效果**。早先 sim 把其中几条放在 <c>OnAfterExecuted</c>（出牌之后），
///   于是「本张牌刚施加的 buff 立刻作用到本张牌自己的耗生命」—— 典型是卡 10000076 梦•崩拳弹：
///   它施加「下次耗生命加防」，在引擎里只作用于**下一张**，sim 里却当场兑现，每场多 1 点防。
/// ⚠ **只在主循环出牌时调用**：临时触发的牌（<c>ExecuteEffect(isTempCard: true)</c>）不走
///   <c>CheckCardCost</c>，所以不付费、也不触发这一串。放在 <c>OnAfterExecuted</c> 里会让临时牌也结算。
/// ⚠ 分支**顺序敏感**（每条都改血 / 防），逐条照抄原码次序，不要合并。
/// 「连崩牌不扣层」用 <see cref="CardTypes.IsLianBeng"/>（原码 IsLianBeng）。
/// </summary>
internal static class CostFunctions
{
    /// <summary>
    /// **灵气判定 + 扣灵气**：<c>CheckAnima</c>（原码 24234）在减耗链（<see cref="CardEffects.CostOf"/>）之后的尾段——
    /// 灵气不够时的一串「替代支付」（天赋 153 以血/体魄代、共鸣 57 / 仙命 160 以[脉]代、
    /// 共鸣 43 / 仙命 412 木灵牌以血代、刻印 47 星力换灵气、[剑气]代、紫芒星宝以[星力]代），
    /// 然后判定不足 / 扣灵气 / 移除「下张牌灵气减耗」。返回 false = AnimaShortage。
    /// ⚠ 早先 sim 这一段整个没有，且从不移除 XiaZhangPaiLingQiJianHao（减耗永久生效）。
    /// </summary>
    public static bool CheckAnima(Combatant src, BattleCard card)
    {
        int num = CardEffects.CostOf(src, card);
        var cfg = src.Config;

        // 天赋 153：不够的部分按 o[0] 扣血、o[1] 扣体魄（共鸣 50 免体魄），两样都付得起才替代。
        if (src.HasTalent(153) && num + src.Anima < 0)
        {
            int short1 = num + src.Anima;
            int hpCost = short1 * cfg.TalentOtherParams(153).At(0);
            int tiPoCost = short1 * cfg.TalentOtherParams(153).At(1);
            if (src.HasTalentResonance(50)) tiPoCost = 0;
            if (Math.Abs(hpCost) < src.Hp && Math.Abs(tiPoCost) <= src.TiPo)
            {
                num = -src.Anima;
                src.ModifyHp(hpCost, canRevive: false, isCost: true);
                if (tiPoCost < 0) src.ModifyTiPo(tiPoCost);
            }
        }
        // 共鸣 57 / 仙命 160：不够的部分用[脉]抵。
        if ((src.HasTalentResonance(57) || src.HasFateStrategy(160)) && num + src.Anima < 0)
        {
            int short2 = num + src.Anima;
            if (Math.Abs(short2) <= src.GetBuffValue(BuffType.Min))
            {
                src.ModifyBuffValue(BuffType.Min, short2);
                num = -src.Anima;
            }
        }
        // 共鸣 43（生效）/ 仙命 412：木灵牌的灵气费改为按倍数扣血。
        if (src.IsTalentResonanceEffective(43) && cfg.CardName(card.Id).Contains("木灵") && num < 0)
        {
            src.ModifyHp(num * cfg.ResonanceOtherParams(43).At(0), canRevive: false, isCost: true);
            num = 0;
        }
        if (src.HasFateStrategy(412) && cfg.CardName(card.Id).Contains("木灵") && num < 0)
        {
            src.ModifyHp(num * cfg.FateOtherParams(412).At(0), canRevive: false, isCost: true);
            num = 0;
        }
        // 刻印 47：灵气不够且有星力 → 耗 1 星力得 2 灵气（不改 num）。
        if (src.Anima + num < 0 && src.Subs.HasKeYinType(src, 47) && src.GetBuffValue(BuffType.XingLi) > 0)
        {
            src.ModifyBuffValue(BuffType.XingLi, -1);
            src.ModifyAnima(2);
        }
        // [剑气]：不够的部分用剑气抵。
        if (src.GetBuffValue(BuffType.JianQi) > 0 && num + src.Anima < 0)
        {
            int short3 = num + src.Anima;
            if (Math.Abs(short3) <= src.GetBuffValue(BuffType.JianQi))
            {
                src.ModifyBuffValue(BuffType.JianQi, short3);
                num = -src.Anima;
            }
        }
        // 紫芒星宝：灵气费先用[星力]抵（不论够不够）。
        if (num < 0 && src.HasBuff(BuffType.ZiMangXingBao))
        {
            int xingLi = src.GetBuffValue(BuffType.XingLi);
            if (xingLi > 0)
            {
                int use = Math.Min(-num, xingLi);
                if (use > 0)
                {
                    src.ModifyBuffValue(BuffType.XingLi, -use);
                    num += use;
                    if (num > 0) num = 0;
                }
            }
        }
        if (num + src.Anima < 0) return false;
        if (num < 0) src.ModifyAnima(num);
        src.RemoveBuff(BuffType.XiaZhangPaiLingQiJianHao);
        return true;
    }

    /// <summary>
    /// 在灵气费用已判定通过、并已扣掉灵气之后调用。<paramref name="card"/> 是**原牌**
    /// （出牌前拦截链还没换牌 —— 原码扣的就是原牌的代价）。
    /// </summary>
    public static void PayHpCost(Combatant src, Combatant dst, BattleCard card)
    {
        int cardHpCost = src.Config.CardHpCost(card.Id);   // 原码里分支内部用的 `cardConfig.hpCost`
        int num2 = cardHpCost;                              // 实际要扣的量（可被下面两条改写）
        int baseId = CardTypes.BaseId(card.Id);

        // 梅开二度 + 刻印 95：固定耗 8（原码同时改了卡面显示，故分支内用的 cardConfig.hpCost 不变）。
        if (baseId == 4000041 && src.Subs.HasKeYinType(src, 95)) num2 = 8;
        if (num2 <= 0) return;

        // 10000005：先按体魄抵掉一半（不低于 0）。
        if (baseId == 10000005)
        {
            num2 -= src.TiPo / 2;
            if (num2 < 0) num2 = 0;
        }

        // 天赋 174：改为扣**上限**；共鸣 54 生效时对方也扣同量上限。否则正常扣血（不可触发复活）。
        if (src.HasTalent(174))
        {
            src.ModifyMaxHp(-num2);
            if (src.IsTalentResonanceEffective(54)) dst.ModifyMaxHp(-num2);
        }
        else
        {
            src.ModifyHp(-num2, canRevive: false, isCost: true);
        }

        // 下面各条都用**卡面**的 hpCost（原码 `cardConfig.hpCost`），不是改写后的 num2。
        int hc = cardHpCost;
        bool bengQuan = CardTypes.IsBengQuan(src, card.Id);

        // 刻印血影：本次耗生命转成等量身法（消耗一层）。
        if (src.HasBuff(BuffType.KeYinXueYing) && hc > 0)
        {
            src.ModifyBuffValue(BuffType.KeYinXueYing, -1);
            src.ModifyBuffValue(BuffType.ShenFa, hc);
        }
        // ① 崩拳•弹：返还所耗生命（消耗一层；10000035 例外不扣层）。
        if (bengQuan && src.HasBuff(BuffType.BengQuanTan) && hc > 0)
        {
            src.ModifyHp(hc);
            if (!CardTypes.IsLianBeng(card.Id)) src.ModifyBuffValue(BuffType.BengQuanTan, -1);
        }
        // ② 崩拳•返玄：返还所耗生命（**不消耗**）。
        if (bengQuan && src.HasBuff(BuffType.BengQuanFanXuan) && hc > 0)
        {
            src.ModifyHp(hc);
        }
        // ③ 下次耗生命的牌返还（消耗一层）。
        if (src.HasBuff(BuffType.XiaCiHaoShengMingPaiFanHuan) && hc > 0)
        {
            src.ModifyBuffValue(BuffType.XiaCiHaoShengMingPaiFanHuan, -1);
            src.ModifyHp(hc);
        }
        // ④ 耗生命加防（不消耗）。
        if (src.HasBuff(BuffType.HaoShengMingJiaFang) && hc > 0)
        {
            src.ModifyDef(hc);
        }
        // ⑤ 下次耗生命加防（消耗一层）。——放回正确时序后恢复启用（见类注释里的 10000076）。
        if (src.HasBuff(BuffType.XiaCiHaoShengMingJiaFang) && hc > 0)
        {
            src.ModifyBuffValue(BuffType.XiaCiHaoShengMingJiaFang, -1);
            src.ModifyDef(hc);
        }
        // ⑥ 崩拳•碎骨自身：「此牌耗生命时向对方造成等量伤害」—— 原码门只看 baseId（外层已保证 num2 > 0）。
        if (baseId == 10000093)
        {
            CombatMath.ApplyDamage(src, dst, DamageInfo.Create(src, DamageType.Damage, hc, skipWoundCheck: true));
        }
        // ⑦ 崩拳•碎骨给下一张崩拳的那层（消耗一层）。
        if (src.HasBuff(BuffType.XiaCiBengQuanHaoShengMingShiZaoChengDengLiangShangHai) && hc > 0 && bengQuan)
        {
            CombatMath.ApplyDamage(src, dst, DamageInfo.Create(src, DamageType.Damage, hc, skipWoundCheck: true));
            if (!CardTypes.IsLianBeng(card.Id))
                src.ModifyBuffValue(BuffType.XiaCiBengQuanHaoShengMingShiZaoChengDengLiangShangHai, -1);
        }
        // 仙命 347 热血化气：本回合首次耗生命 +1 灵气（标记在回合 tick 里清）。
        if (src.HasFateStrategy(347) && hc > 0 && !src.HasBuff(BuffType.ReXueHuaQiYiChuFa))
        {
            src.ModifyBuffValue(BuffType.ReXueHuaQiYiChuFa, 1);
            src.ModifyAnima(1);
        }
    }
}
