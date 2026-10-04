using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 复活（批次 C）：忠实移植 <c>BattleExecuter.CharacterResurrectionCheckAsync</c> +
/// <c>BattleCharacter.CanRevive</c>。
///
/// 六种复活来源（<see cref="Combatant.CanRevive"/> 判能不能、本函数执行）：
///   盘古符（先扣临时生命再消耗，且不受「禁止复活」否决）／炎魂鬼符／浴火凤凰／天女白玉轮／
///   刻印·续天命／七星解命。
/// 每个分支都重新判 <c>Hp &lt;= 0</c>，所以实际只会有一个生效（先满足的先把血拉起来）。
/// </summary>
public static class RevivalFunctions
{
    /// <summary>复活检查：对自己与对手各判一次（对应 ResurrectionCheckAsync 里先 firstCharacter 后 defaultOpponentTarget）。</summary>
    public static void Check(Combatant c)
    {
        Revive(c);
        Revive(c.Opponent);
    }

    /// <summary>按原码顺序执行各复活来源；无可复活则什么都不做。</summary>
    public static void Revive(Combatant c)
    {
        if (!c.CanRevive()) return;

        // 盘古符：扣临时生命并消耗（发生在「禁止复活」判断之前）。
        if (c.Hp <= 0 && c.HasBuff(BuffType.PanGuFu))
        {
            c.ModifyTempLife(-c.GetBuffValue(BuffType.PanGuFu));
            c.RemoveBuff(BuffType.PanGuFu);
        }

        if (!c.HasBuff(BuffType.JinZhiFuHuo))
        {
            // 炎魂鬼符：上限与血都设为「天赋139 的 otherParams[1]」（共鸣48 会再加）。
            if (c.Hp <= 0 && c.HasBuff(BuffType.YanHunGuiFu))
            {
                int hp = c.Config.TalentOtherParams(139).At(1);
                if (c.IsTalentResonanceEffective(48)) hp += c.Config.ResonanceOtherParams(48).At(0);
                c.SetMaxHp(hp);
                c.SetHp(hp);
                c.RemoveBuff(BuffType.YanHunGuiFu);
            }
            // 浴火凤凰：按 buff 值加上限，再把血补到该值，然后消耗。
            if (c.Hp <= 0 && c.HasBuff(BuffType.YuHuoFengHuang))
            {
                int v = c.GetBuffValue(BuffType.YuHuoFengHuang);
                c.ModifyMaxHp(v);
                c.ModifyHp(v - c.Hp, canRevive: true);
                c.RemoveBuff(BuffType.YuHuoFengHuang);
            }
            // 天女白玉轮：上限不为正则补 1，血设为卡 172 的 otherParams[0]，消耗 1 层。
            if (c.Hp <= 0 && c.HasBuff(BuffType.TianNvBaiYuLun))
            {
                if (c.MaxHp <= 0) c.ModifyMaxHp(1);
                c.ModifyHp(c.Config.CardOtherParams(172).At(0) - c.Hp, canRevive: true);
                c.ModifyBuffValue(BuffType.TianNvBaiYuLun, -1);
            }
            // 刻印·续天命：卦象层数 × buff 值 → 加上限并回血，消耗。
            if (c.Hp <= 0 && c.HasBuff(BuffType.KeYinXuTianMing))
            {
                int add = c.GetBuffValue(BuffType.GuaXiang) * c.GetBuffValue(BuffType.KeYinXuTianMing);
                if (add > 0)
                {
                    c.ModifyMaxHp(add);
                    c.ModifyHp(add, canRevive: true);
                }
                c.RemoveBuff(BuffType.KeYinXuTianMing);
            }
        }

        // 七星解命：清 buff 与卦象/星力，按（卦象+星力）× 层数 加上限并回血。
        if (c.Hp <= 0 && c.HasBuff(BuffType.QiXingJieMing))
        {
            int add = (c.GetBuffValue(BuffType.GuaXiang) + c.GetBuffValue(BuffType.XingLi))
                      * c.GetBuffValue(BuffType.QiXingJieMing);
            c.RemoveBuff(BuffType.QiXingJieMing);
            if (add > 0)
            {
                c.RemoveBuff(BuffType.GuaXiang);
                c.RemoveBuff(BuffType.XingLi);
                c.ModifyMaxHp(add);
                c.ModifyHp(add, canRevive: true);
            }
        }
    }
}
