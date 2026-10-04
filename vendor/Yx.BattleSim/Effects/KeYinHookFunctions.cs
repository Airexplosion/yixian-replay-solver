using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 刻印的**出牌前 / 出牌后钩子**：忠实移植 <c>KeYinCardFunctions.OnBeforeCardExecutedKeYinCardFunction</c>（原码 194831 状态机）
/// 与 <c>OnAfterCardExecutedKeYinCardFunction</c>（195177）。
///
/// 调用点（与原码一致）：
///   · 出牌前：<see cref="CardHookFunctions.OnBeforeExecuted"/> 里「星元」之后、清实际伤害之前（原码 22521）；
///   · 出牌后：<see cref="CardHookFunctions.OnAfterExecuted"/> 里相邻后效之后、仙命 416 蹴鞠飞袭之前（原码 IL_1f55）。
/// 两处原码都不区分临时牌，照抄。
///
/// ⚠ 早先 sim 整段缺失：刻印牌（惊雷 / 热煞 / 拳风 / 滋养 / 火源 / 机关刻纹系 …）挂上的「下一次…时」buff
///   **只写不读**，效果全部静默丢失。
/// 顺序按 goto 链（= IL 偏移序），不是文件序：
///   出牌前：默认段 → IL_018a（刻印 92 … 刻印 78）→ IL_096e（刻印 108、机关星弈）→ IL_0b4c（机关灵印）→ switch 之后（132 / 133 / 卦 / 星弈 / 155 / 胖波师）。
///   出牌后：默认段（25 … 火变）→ IL_089c（137、离之火）。
/// </summary>
internal static class KeYinHookFunctions
{
    /// <summary>Level 枚举里的 ZhuJi（筑基）＝ 2。</summary>
    private const int LevelZhuJi = 2;

    private static JsonBattleConfig.CardMeta? Meta(Combatant c, int cardId) => (c.Config as JsonBattleConfig)?.Card(cardId);

    /// <summary>KeYinCardFunctions.IsXiangSheng(wuxing, cardName, 相生之火)（原码 195497）：已用五行 → 本张五行牌是否相生。</summary>
    private static bool IsXiangSheng(BuffType wuxing, string cardName, bool zhiHuo)
    {
        if (!(cardName.Contains("金灵") || cardName.Contains("木灵") || cardName.Contains("土灵")
              || cardName.Contains("水灵") || cardName.Contains("火灵")))
            return false;
        if ((wuxing == BuffType.JiHuoTuLing && cardName.Contains("金灵"))
            || (wuxing == BuffType.JiHuoShuiLing && cardName.Contains("木灵"))
            || (wuxing == BuffType.JiHuoHuoLing && cardName.Contains("土灵"))
            || (wuxing == BuffType.JiHuoMuLing && cardName.Contains("火灵"))
            || (wuxing == BuffType.JiHuoJinLing && cardName.Contains("水灵")))
            return true;
        if (zhiHuo && ((wuxing == BuffType.JiHuoHuoLing && !cardName.Contains("火灵"))
                       || (wuxing != BuffType.JiHuoHuoLing && cardName.Contains("火灵"))))
            return true;
        return false;
    }

    private static void TrueDamage(Combatant src, Combatant dst, int n)
        => CombatMath.ApplyDamage(src, dst, DamageInfo.Create(src, DamageType.Damage, n, skipWoundCheck: true));

    /// <summary>OnBeforeCardExecutedKeYinCardFunction。</summary>
    public static void OnBeforeCard(Combatant src, Combatant dst, int cardId, int grid)
    {
        var cfg = src.Config;
        string name = cfg.CardName(cardId);
        int baseId = CardTypes.BaseId(cardId);
        var meta = Meta(src, cardId);
        int hpCost = cfg.CardHpCost(cardId);

        // 默认段：下次剑阵牌按剑意加防。
        if (src.HasBuff(BuffType.XiaCiJianZhenPaiMeiDianJianYiJiaFang) && CardTypes.IsJianZhen(src, cardId) && src.HasBuff(BuffType.JianYi))
        {
            int d = src.GetBuffValue(BuffType.XiaCiJianZhenPaiMeiDianJianYiJiaFang) * src.GetBuffValue(BuffType.JianYi);
            src.RemoveBuff(BuffType.XiaCiJianZhenPaiMeiDianJianYiJiaFang);
            src.ModifyDef(d);
        }

        // ── IL_018a ──
        if (KeYinCardFunctions.HasKeYinType(src, 92) && CardTypes.IsKuangJian(src, cardId))
            src.ModifyAnima(KeYinCardFunctions.GetTotalKeYinOtherparam(src, 92, 0));
        if (name.Contains("雷"))
        {
            if (src.HasBuff(BuffType.XiaCiLeiPaiShiQuShengMing))
            {
                int v = src.GetBuffValue(BuffType.XiaCiLeiPaiShiQuShengMing);
                src.RemoveBuff(BuffType.XiaCiLeiPaiShiQuShengMing);
                src.ModifyHp(-v);
            }
            if (src.HasBuff(BuffType.XiaCiLeiPaiJiaGuaXiang))
            {
                int v = src.GetBuffValue(BuffType.XiaCiLeiPaiJiaGuaXiang);
                src.RemoveBuff(BuffType.XiaCiLeiPaiJiaGuaXiang);
                src.ModifyBuffValue(BuffType.GuaXiang, v);
            }
        }
        if (name.Contains("雷") && src.HasBuff(BuffType.KeYinJingLei) && ((meta?.Attack ?? 0) > 0 || name == "五雷轰顶"))
        {
            int v = src.GetBuffValue(BuffType.KeYinJingLei);
            src.RemoveBuff(BuffType.KeYinJingLei);
            src.ModifyBuffValue(BuffType.SuiFang, 1);
            dst.ModifyBuffValue(BuffType.WaiShang, v);
        }
        if ((baseId == 4000042 || grid == 7) && src.HasBuff(BuffType.XiaCiKuMuFengChunMeiDianLingQiJiaShengMing) && src.Anima > 0)
        {
            int v = src.GetBuffValue(BuffType.XiaCiKuMuFengChunMeiDianLingQiJiaShengMing) * src.Anima;
            src.RemoveBuff(BuffType.XiaCiKuMuFengChunMeiDianLingQiJiaShengMing);
            src.ModifyMaxHp(v);
            src.ModifyHp(v);
        }
        if ((name.Contains("土灵") || name.Contains("火灵")) && src.GetBuffValue(BuffType.KeYinReSha) > 0)
        {
            int v = src.GetBuffValue(BuffType.KeYinReSha);
            src.RemoveBuff(BuffType.KeYinReSha);
            TrueDamage(src, dst, v);
        }
        if (hpCost > 0 && src.GetBuffValue(BuffType.KeYinQuanFeng) > 0)
        {
            int v = src.GetBuffValue(BuffType.KeYinQuanFeng);
            src.RemoveBuff(BuffType.KeYinQuanFeng);
            TrueDamage(src, dst, v);
        }
        if ((name.Contains("木灵") || name.Contains("水灵")) && src.GetBuffValue(BuffType.KeYinZiYang) > 0)
        {
            int v = src.GetBuffValue(BuffType.KeYinZiYang);
            src.RemoveBuff(BuffType.KeYinZiYang);
            src.ModifyMaxHp(v);
            src.ModifyHp(v);
        }
        if (name.Contains("土灵") && src.GetBuffValue(BuffType.XiaCiTuLingPaiJiaFang) > 0)
        {
            int v = src.GetBuffValue(BuffType.XiaCiTuLingPaiJiaFang);
            src.RemoveBuff(BuffType.XiaCiTuLingPaiJiaFang);
            src.ModifyDef(v);
        }
        if (name.Contains("金灵") && src.GetBuffValue(BuffType.XiaCiJinLingPaiJiaFengRui) > 0)
        {
            int v = src.GetBuffValue(BuffType.XiaCiJinLingPaiJiaFengRui);
            src.RemoveBuff(BuffType.XiaCiJinLingPaiJiaFengRui);
            src.ModifyBuffValue(BuffType.FengRui, v);
        }
        if (name.Contains("火灵") && src.GetBuffValue(BuffType.KeYinHuoYuan) > 0)
        {
            int v = src.GetBuffValue(BuffType.KeYinHuoYuan);
            src.RemoveBuff(BuffType.KeYinHuoYuan);
            dst.ModifyHp(-v);                       // ModifyHpWithFx（飘字/特效另算）
            dst.ModifyMaxHp(-v);
        }
        if (name.Contains("水灵") && src.GetBuffValue(BuffType.XiaCiShuiLingPaiJiaLingQi) > 0)
        {
            int v = src.GetBuffValue(BuffType.XiaCiShuiLingPaiJiaLingQi);
            src.RemoveBuff(BuffType.XiaCiShuiLingPaiJiaLingQi);
            src.ModifyAnima(v);
        }
        if (name.Contains("火灵") && KeYinCardFunctions.HasKeYinType(src, 78))
        {
            int v = System.Math.Min(src.Def, KeYinCardFunctions.GetTotalKeYinOtherparam(src, 78, 0));
            if (v > 0)
            {
                src.ModifyDef(-v);
                dst.ModifyHp(-v);
                dst.ModifyMaxHp(-v);
            }
        }

        // ── IL_096e ──
        if (hpCost > 0 && KeYinCardFunctions.HasKeYinType(src, 108))
            TrueDamage(src, dst, hpCost);
        if (src.HasBuff(BuffType.JiGuanXingYi) && grid >= 0 && !GridMarkFunctions.IsXingWei(src, grid)
            && src.GetBuffValue(BuffType.JiGuanKeWen) >= 2)
        {
            src.ModifyBuffValue(BuffType.JiGuanXingYi, -1);
            src.ModifyBuffValue(BuffType.JiGuanKeWen, -2);
            GridMarkFunctions.AddXingWei(src, grid);
        }

        // ── IL_0b4c：机关灵印（未激活的五行牌耗 2 机关刻纹先激活）。条件不满足时原码 `break`，照样落到 switch 之后。──
        if (src.HasBuff(BuffType.JiGuanLingYin) && WuXingFunctions.GetWuXingName(name) != ""
            && !WuXingFunctions.IsCardNameActived(src, name) && src.GetBuffValue(BuffType.JiGuanKeWen) >= 2)
        {
            src.ModifyBuffValue(BuffType.JiGuanKeWen, -2);
            WuXingFunctions.ActiveWuXingInName(src, name);
        }

        // ── switch 之后（总会执行）──
        if (KeYinCardFunctions.HasKeYinType(src, 132))
        {
            foreach (int keyin in src.BattleKeYinCards)
            {
                if (keyin % 10000 != 132) continue;
                var op = cfg.KeYinOtherParams(keyin);
                if (name.Contains("水灵") && src.GetBuffValue(BuffType.JiGuanKeWen) >= 2)
                {
                    src.ModifyBuffValue(BuffType.JiGuanKeWen, -2);
                    src.ModifyBuffValue(BuffType.ShuiShi, op.At(1));
                }
                if (name.Contains("土灵") && src.GetBuffValue(BuffType.JiGuanKeWen) >= 2)
                {
                    src.ModifyBuffValue(BuffType.JiGuanKeWen, -2);
                    src.ModifyDef(op.At(2));
                }
            }
        }
        if (KeYinCardFunctions.HasKeYinType(src, 133))
        {
            foreach (int keyin in src.BattleKeYinCards)
            {
                if (keyin % 10000 != 133) continue;
                if (name.Contains("金灵") && src.GetBuffValue(BuffType.JiGuanKeWen) >= 2)
                {
                    src.ModifyBuffValue(BuffType.JiGuanKeWen, -2);
                    src.ModifyBuffValue(BuffType.FengRui, 3);
                }
                if (name.Contains("木灵") && src.GetBuffValue(BuffType.JiGuanKeWen) >= 2)
                {
                    src.ModifyBuffValue(BuffType.JiGuanKeWen, -2);
                    src.ModifyBuffValue(BuffType.JiaGong, 1);
                }
            }
        }
        if (name.Contains("卦") && src.HasBuff(BuffType.KeYinBuLingDeng))
        {
            src.ModifyBuffValue(BuffType.KeYinBuLingDeng, -1);
            src.ModifyBuffValue(BuffType.GuaXiang, 2);
            dst.ModifyHp(-2);
            dst.ModifyAnima(-1);
        }
        if (name.Contains("星弈") && src.HasBuff(BuffType.KeYinBuXingGua))
        {
            src.ModifyBuffValue(BuffType.KeYinBuXingGua, -1);
            src.ModifyBuffValue(BuffType.GuaXiang, 1);
        }
        if (KeYinCardFunctions.HasKeYinType(src, 155) && src.HasBuff(BuffType.UsedWuXing)
            && IsXiangSheng((BuffType)src.GetBuffValue(BuffType.UsedWuXing), name, src.HasTalent(137)))
        {
            src.ModifyAnima(KeYinCardFunctions.GetTotalKeYinOtherparam(src, 155, 0));
            src.ModifyHp(KeYinCardFunctions.GetTotalKeYinOtherparam(src, 155, 1));
        }
        if (src.HasBuff(BuffType.KeYInPangBoShi) && (baseId == 10000043 || baseId == 10000052))
        {
            int v = src.GetBuffValue(BuffType.KeYInPangBoShi);
            src.RemoveBuff(BuffType.KeYInPangBoShi);
            src.ModifyBuffValue(BuffType.QiShi, v);
        }
    }

    /// <summary>OnAfterCardExecutedKeYinCardFunction。</summary>
    public static void OnAfterCard(Combatant src, Combatant dst, int cardId, int grid)
    {
        var cfg = src.Config;
        string name = cfg.CardName(cardId);
        int baseId = CardTypes.BaseId(cardId);
        int level = cfg.CardLevel(cardId);
        bool canAgain() => src.GetBuffValue(BuffType.ExActionAgain) == 0
                           && src.GetBuffValue(BuffType.ZaiCiXingDong) < src.ActionAgainPerRound;

        if ((baseId == 1000001 || baseId == 1000027) && KeYinCardFunctions.HasKeYinType(src, 25))
            src.ModifyBuffValue(BuffType.SuoYouPaiShiZuoYunJian, 1);
        if (baseId == 1000039 && KeYinCardFunctions.HasKeYinType(src, 67) && !src.HasBuff(BuffType.KeYinShanFeng))
        {
            src.ActionAgainPerRound++;
            src.ModifyBuffValue(BuffType.KeYinShanFeng, 1);
        }
        if (baseId == 1000066 && KeYinCardFunctions.HasKeYinType(src, 91))
        {
            src.ModifyMaxHp(KeYinCardFunctions.GetTotalKeYinOtherparam(src, 91, 0));
            src.ModifyBuffValue(BuffType.JiaGong, KeYinCardFunctions.GetTotalKeYinOtherparam(src, 91, 1));
        }
        if (src.HasBuff(BuffType.XiaCiJianPiJianDangJiaJianYi) && (baseId == 1000010 || baseId == 1000012))
        {
            src.ModifyBuffValue(BuffType.JianYi, src.GetBuffValue(BuffType.XiaCiJianPiJianDangJiaJianYi));
            src.RemoveBuff(BuffType.XiaCiJianPiJianDangJiaJianYi);
        }
        if (src.GetBuffValue(BuffType.XiaCiGongJiHouFanHuanJianYi) > 0 && src.GetBuffValue(BuffType.DanKaGongJiJiShu) > 0
            && src.GetBuffValue(BuffType.XiaoHaoJianYi) > 0)
        {
            int v = src.GetBuffValue(BuffType.XiaoHaoJianYi);
            if (v > src.GetBuffValue(BuffType.XiaCiGongJiHouFanHuanJianYi)) v = src.GetBuffValue(BuffType.XiaCiGongJiHouFanHuanJianYi);
            src.RemoveBuff(BuffType.XiaCiGongJiHouFanHuanJianYi);
            src.ModifyBuffValue(BuffType.HuiFuJianYi, v);
        }
        if (baseId == 1000026 && src.HasBuff(BuffType.XiaCiRouXinJiaShengMingShangXianBingZaiDong))
        {
            src.ModifyMaxHp(src.GetBuffValue(BuffType.XiaCiRouXinJiaShengMingShangXianBingZaiDong));
            src.RemoveBuff(BuffType.XiaCiRouXinJiaShengMingShangXianBingZaiDong);
            src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        }
        if ((baseId == 4000024 || baseId == 4000008) && src.GetBuffValue(BuffType.XiaCiFeiXingCiJiaXingLi) > 0)
        {
            int v = src.GetBuffValue(BuffType.XiaCiFeiXingCiJiaXingLi);
            src.RemoveBuff(BuffType.XiaCiFeiXingCiJiaXingLi);
            src.ModifyBuffValue(BuffType.XingLi, v);
        }
        if (KeYinCardFunctions.HasKeYinType(src, 48) && src.HasBuff(BuffType.ShiYongHouZhao))
        {
            int v = KeYinCardFunctions.GetTotalKeYinOtherparam(src, 48, 0);
            src.ModifyMaxHp(v);
            src.ModifyHp(v);
        }
        if (baseId == 4000045 && src.HasBuff(BuffType.XiaCiTianYuanXinFaZaiCiXingDong) && canAgain())
        {
            src.RemoveBuff(BuffType.XiaCiTianYuanXinFaZaiCiXingDong);
            src.ModifyBuffValue(BuffType.XingLi, -1);
            src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        }
        if (level == LevelZhuJi && name.Contains("木灵") && src.GetBuffValue(BuffType.XiaCiMuLingShuYingJiaGong) > 0)
        {
            int v = src.GetBuffValue(BuffType.XiaCiMuLingShuYingJiaGong);
            src.RemoveBuff(BuffType.XiaCiMuLingShuYingJiaGong);
            src.ModifyBuffValue(BuffType.JiaGong, v);
        }
        if (KeYinCardFunctions.HasKeYinType(src, 102) && src.Def >= KeYinCardFunctions.GetTotalKeYinOtherparam(src, 102, 0) && canAgain())
        {
            src.ModifyDef(-KeYinCardFunctions.GetTotalKeYinOtherparam(src, 102, 1));
            src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        }
        if (name.Contains("灵阵") && src.HasBuff(BuffType.XiaCiLingZhenPaiZaiCiXingDong) && canAgain())
        {
            src.RemoveBuff(BuffType.XiaCiLingZhenPaiZaiCiXingDong);
            src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        }
        if (baseId == 10000004 && src.GetBuffValue(BuffType.XiaCiDuanQuanJiaTiPo) > 0)
        {
            int v = src.GetBuffValue(BuffType.XiaCiDuanQuanJiaTiPo);
            src.RemoveBuff(BuffType.XiaCiDuanQuanJiaTiPo);
            src.ModifyTiPo(v);
        }
        if (KeYinCardFunctions.HasKeYinType(src, 90)
            && ((!src.HasBuff(BuffType.YunHaiYouLongShengXiao) && grid == 0 && src.HasBuff(BuffType.WoundedCount)) || baseId == 1000042))
            src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        // 火变：木灵 / 火灵牌本次攻击过 → 追加两段「火变层数」攻。
        if (src.HasBuff(BuffType.KeYinHuoBian) && (name.Contains("木灵") || name.Contains("火灵"))
            && src.GetBuffValue(BuffType.DanKaGongJiJiShu) > 0)
        {
            int v = src.GetBuffValue(BuffType.KeYinHuoBian);
            src.RemoveBuff(BuffType.KeYinHuoBian);
            CombatMath.Attack(src, dst, v, 2);
        }

        // ── IL_089c ──
        if (KeYinCardFunctions.HasKeYinType(src, 137))
        {
            foreach (int keyin in src.BattleKeYinCards)
            {
                if (keyin % 10000 == 137 && src.GetBuffValue(BuffType.BENLUNGONGJICISHU) == 0
                    && src.GetBuffValue(BuffType.JiGuanKeWen) >= 2)
                {
                    src.ModifyBuffValue(BuffType.JiGuanKeWen, -2);
                    src.ModifyBuffValue(BuffType.ShenFa, cfg.KeYinOtherParams(keyin).At(1));
                }
            }
        }
        if (src.HasBuff(BuffType.KeYinLiZhiHuo) && baseId == 4000034)
        {
            int v = src.GetBuffValue(BuffType.KeYinLiZhiHuo) * src.GetBuffValue(BuffType.GuaXiang);
            src.RemoveBuff(BuffType.KeYinLiZhiHuo);
            dst.ModifyHp(-v);
            dst.ModifyMaxHp(-v);
        }
    }
}
