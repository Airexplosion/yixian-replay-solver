using Yx.BattleSim.Combat;
using Yx.BattleSim.Model;
using Yx.BattleSim.Config;

namespace Yx.BattleSim.Effects;

public static partial class CardEffects
{
    private static void RegisterBaijia1015()
    {
        ByBase[383] = static (src, dst, card) => {
            src.ModifyTiPo(card.Physique);
            src.ModifyBuffValue(BuffType.ShenFa, src.Config.CardOtherParams(card.Id).At(0));
            int damage = src.GetBuffValue(BuffType.ShenFa) / 2;
            if (src.Level >= 5) damage *= 2;
            if (damage > 0) CombatMath.ApplyDamage(src, dst, DamageInfo.Create(src, DamageType.Damage, damage));
        };
        ByBase[405] = static (src, dst, card) => {
            if (card.Anima > 0) src.ModifyAnima(card.Anima);
            int growth = src.Config.CardOtherParams(card.Id).At(0);
            if (growth > 0) src.ModifyMaxHp(growth);
            int heal = src.GetBuffValue(BuffType.XingLi) + src.Anima;
            if (heal > 0) src.ModifyHp(heal);
            src.CurrentCardActionAgain = HouZhaoFunctions.CheckHouZhao(src, card.Id, src.CurrentCardGrid)
                || GridMarkFunctions.CheckXingWei(src, src.CurrentCardGrid);
        };
        ByBase[8000014] = static (src, dst, card) => {
            src.ModifyDef(card.Def);
            src.ModifyBuffValue(BuffType.XingYi_Duan, src.Config.CardOtherParams(card.Id).At(0));
            src.CurrentCardActionAgain = src.HasBuff(BuffType.YongGuoChiXuPai);
        };
    }
}
