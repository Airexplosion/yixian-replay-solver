using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 刻印牌的**逐回合打出**：忠实移植主循环 15400–15420 与 <c>KeYinCardFunctions.ExecuteAsync</c>（原码 194089，d__0）。
///
/// 每回合（回合开始 tick + 复活 / 死亡判定之后、出牌之前）按顺序取出下一张刻印牌（<c>ShiftKeYinCard</c>：
/// 序号只增不回绕，取完就不再有；id 为 0 或 10167 的格返回空），**非被动**（描述不含「[被动生效]」）才执行。
/// 对手带刻印 10166 时，对手随即以同一张刻印牌的 id 再执行一次（src / dst 对调，<c>CreatTempKeYinCard</c>）。
/// ⚠ 早先 sim 整段缺失 —— 所有主动刻印牌的效果都没打出来（b4 有 85/120 场带刻印）。
/// case 22 的「修为差」取 <c>characterUI.exp</c>（= <see cref="Combatant.Exp"/>，当前修为）。
/// </summary>
internal static class KeYinTurnFunctions
{
    /// <summary>主循环里每回合一次（原码 15400）。</summary>
    public static void OnTurn(Combatant first, Combatant second)
    {
        var list = first.BattleKeYinCards;
        if (list.Count == 0) return;
        if (first.CurrentUsingKeYinCardIndex >= list.Count) return;
        int id = list[first.CurrentUsingKeYinCardIndex];
        first.CurrentUsingKeYinCardIndex++;
        if (id == 0 || id == 10167) return;
        if (first.Config.KeYinDesc(id).Contains("[被动生效]")) return;

        Execute(id, first, second);
        // 对手的刻印 166：以同一张刻印牌的 id 复制执行（要求对手牌组里恰有 10166 这一格）。
        if (KeYinCardFunctions.HasKeYinType(second, 166) && ContainsExact(second.BattleKeYinCards, 10166))
            Execute(id, second, first);
    }

    private static bool ContainsExact(IReadOnlyList<int> l, int id)
    {
        for (int i = 0; i < l.Count; i++) if (l[i] == id) return true;
        return false;
    }

    /// <summary>原码 KeYinCardFunctions.ExecuteAsync：按 id % 10000 分派。</summary>
    public static void Execute(int keYinId, Combatant src, Combatant dst)
    {
        var op = src.Config.KeYinOtherParams(keYinId);
        int o0 = op.At(0), o1 = op.At(1);
        switch (keYinId % 10000)
        {
            case 2: src.ModifyBuffValue(BuffType.XiaCiJiaFangDuoJia, o0); break;
            case 3: src.ModifyHp(o0); break;
            case 4: src.ModifyBuffValue(BuffType.XiaCiGongJiDuoGong, o0); break;
            case 5: src.ModifyBuffValue(BuffType.XiaCiGongJiSuiFang, o0); break;
            case 6: src.ModifyBuffValue(BuffType.XiaCiPuTongGongJiDuoGong, o0); break;
            case 8: src.ModifyBuffValue(BuffType.XiaCiLingJianGongJiDuoGong, o0); break;
            case 9: src.ModifyBuffValue(BuffType.XiaCiJianPiJianDangJiaJianYi, o0); break;
            case 10: src.ModifyBuffValue(BuffType.XiaCiDouZhuanXingYiDuoXingWei, o0); break;
            case 11: src.ModifyBuffValue(BuffType.XiaCiJiaGuaXiangZaoChengShangHai, o0); break;
            case 13: src.ModifyBuffValue(BuffType.XiaCiFengRuiChuFaJianShengMing, o0); break;
            case 14: src.ModifyBuffValue(BuffType.KeYinReSha, o0); break;
            case 15: src.ModifyBuffValue(BuffType.KeYinZiYang, o0); break;
            case 16: src.ModifyBuffValue(BuffType.XiaCiHuoDeFuMianZhuangTaiJiaFang, o0); break;
            case 17: src.ModifyBuffValue(BuffType.XiaCiDuanQuanJiaTiPo, o0); break;
            case 18: src.ModifyBuffValue(BuffType.KeYinTiaoXi, o0); break;
            case 19:
                src.ModifyHp(o0);
                src.ModifyDef(o0);
                src.ModifyBuffValue(BuffType.NeiShang, -1);
                src.ModifyBuffValue(BuffType.WaiShang, -1);
                src.ModifyBuffValue(BuffType.PoZhan, -1);
                break;
            case 20: src.ModifyMaxHp(o0); break;
            case 21:
                for (int k = 0; k < o0; k++)
                {
                    int p = src.NextParam(ParamRequest.OwnDebuff(ParamSite.KeYin21));
                    if (p != -1) src.ModifyBuffValue((BuffType)p, -1);
                }
                break;
            case 22:
            {
                // 原码 `break` 出 switch 后：修为差（封顶 o[1]）+ o[0] 的真伤。
                int diff = src.Exp - dst.Exp;   // characterUI.exp（当前修为）
                if (diff < 0) diff = 0; else if (diff > o1) diff = o1;
                CombatMath.ApplyDamage(src, dst, DamageInfo.Create(src, DamageType.Damage, o0 + diff, skipWoundCheck: true));
                break;
            }
            case 24: src.ModifyBuffValue(BuffType.XiaCiShouDaoGongJiQianJiaFang, o0); break;
            case 26: src.ModifyBuffValue(BuffType.XiaCiGongJiHouJianShengMing, o0); break;
            case 27: src.ModifyBuffValue(BuffType.XiaCiGongJiHouFanHuanJianYi, o0); break;
            case 28: src.ModifyBuffValue(BuffType.XiaCiFeiXingCiJiaXingLi, o0); break;
            case 30: src.ModifyBuffValue(BuffType.XiaCiJiaGuaXiangDuoJia, o0); break;
            case 33: src.ModifyBuffValue(BuffType.XiaCiMuLingShuYingJiaGong, o0); break;
            case 34: src.ModifyBuffValue(BuffType.XiaCiHuiHeJieShuJianShaoFuMianBuff, o0); break;
            case 35: src.ModifyBuffValue(BuffType.XiaCiHaoShengMingPaiFanHuan, o0); break;
            case 36: src.ModifyBuffValue(BuffType.XiaCiHuoDePoZhanShiShiJiaPoZhan, o0); break;
            case 37:
                foreach (var b in src.GetDebuffList()) src.ModifyBuffValue(b, -o0);
                break;
            case 38: src.ModifyBuffValue(BuffType.XiaCiJiaLingQiDuoJia, o0); break;
            case 39: src.ModifyBuffValue(BuffType.XiaCiGongJiShiJiaNeiShang, o0); break;
            case 40: src.ModifyBuffValue(BuffType.XiaCiJiaGongDuoJia, o0); break;
            case 41:
                dst.ModifyBuffValue(BuffType.JiaGong, -o0);
                dst.ModifyBuffValue(BuffType.HuTi, -o0);
                break;
            case 44: src.ModifyBuffValue(BuffType.XiaZhangPaiSuanZuoKuangJian, 1); break;
            case 45: src.ModifyBuffValue(BuffType.XiaCiJiaJianYiDuoJia, o0); break;
            case 50: src.ModifyBuffValue(BuffType.KeYinJingLei, o0); break;
            case 51: src.ModifyBuffValue(BuffType.XiaCiTuLingPaiJiaFang, o0); break;
            case 52: src.ModifyBuffValue(BuffType.XiaCiJinLingPaiJiaFengRui, o0); break;
            case 53: src.ModifyBuffValue(BuffType.KeYinHuoYuan, o0); break;
            case 54: src.ModifyBuffValue(BuffType.XiaCiShuiLingXiongYongEWaiGongJi, 1); break;
            case 55: src.ModifyBuffValue(BuffType.HunTianKe, 1); break;
            case 58: src.ModifyBuffValue(BuffType.QiRuoXuanHe, o0); break;
            case 59:
            {
                src.ModifyTiPo(o0);
                int d = o1 == 0 ? 0 : src.GetBuffValue(BuffType.TiPo) / o1;
                src.ModifyDef(d);
                break;
            }
            case 60: src.ModifyBuffValue(BuffType.BiXie, o0); break;
            case 66: src.ModifyBuffValue(BuffType.XiaCiRouXinJiaShengMingShangXianBingZaiDong, o0); break;
            case 68:
                src.ModifyDef(o0);
                src.ModifyBuffValue(BuffType.ShuiYueJianZhen, o1);
                break;
            case 69: src.ModifyBuffValue(BuffType.XiaCiJianZhenPaiMeiDianJianYiJiaFang, o0); break;
            case 70: src.ModifyBuffValue(BuffType.XiaCiKuMuFengChunMeiDianLingQiJiaShengMing, o0); break;
            case 72:
                src.ModifyBuffValue(BuffType.XiaCiLeiPaiShiQuShengMing, o0);
                src.ModifyBuffValue(BuffType.XiaCiLeiPaiJiaGuaXiang, o1);
                break;
            case 75: src.ModifyBuffValue(BuffType.XiaCiShuiLingPaiJiaLingQi, o0); break;
            case 80:
            {
                int d = src.GetDebuffCount() / 2;
                if (d > o0) d = o0;
                src.ModifyTiPo(d);
                break;
            }
            case 81: src.ModifyBuffValue(BuffType.BengQuanCunJin, 1); break;
            case 84: src.ModifyHp(src.GetDebuffCount() * o0); break;
            case 85: src.ModifyBuffValue(BuffType.HuaLongDianJing, o0); break;
            case 86: src.ModifyBuffValue(BuffType.XiaCiJiaLingQiDuoJia, o0); break;
            case 87: dst.ModifyBuffValue(BuffType.XiaCiPaiJiangJi, 1); break;
            case 88:
                src.ModifyBuffValue(BuffType.BenHuiHeWuFaZaiCiXingDong, o0);
                dst.ModifyBuffValue(BuffType.BenHuiHeWuFaZaiCiXingDong, o0);
                break;
            case 89: src.ModifyBuffValue(BuffType.HuTi, o0); break;
            case 94: src.ModifyBuffValue(BuffType.XiaCiHanGongPaiZhuiJiaGong, o0); break;
            case 98: src.ModifyBuffValue(BuffType.KeYinYinZheng, 1); break;
            case 99:
            {
                // 对手已有内伤则翻倍，否则挂 o[0] 层。
                int ns = dst.GetBuffValue(BuffType.NeiShang);
                dst.ModifyBuffValue(BuffType.NeiShang, ns > 0 ? ns : o0);
                break;
            }
            case 103: src.ModifyBuffValue(BuffType.XiaCiLingZhenPaiZaiCiXingDong, 1); break;
            case 105:
                src.ModifyAnima(o0);
                src.ModifyBuffValue(BuffType.NeiShang, o1);
                break;
            case 106: src.ModifyBuffValue(BuffType.KeYinXueYing, 1); break;
            case 110: src.ModifyBuffValue(BuffType.KeYinDiSha, o0); break;
            case 112:
                src.ModifyBuffValue(BuffType.NeiShang, o0);
                src.ModifyBuffValue(BuffType.HuiFu, o0);
                break;
            case 113:
                dst.ModifyHp(-o0);
                src.ModifyHp(o0);
                break;
            case 114: src.ModifyBuffValue(BuffType.KeYinQuanFeng, o0); break;
            case 115:
            {
                int n = o0;
                if (src.GetDebuffCount() > 0) n += o1;
                src.ModifyBuffValue(BuffType.QiShi, n);
                break;
            }
            case 116: src.ModifyBuffValue(BuffType.KeYinHuoBian, o0); break;
            case 117: src.ModifyBuffValue(BuffType.KeYinZhuCheng, o0); break;
            case 118: src.ModifyBuffValue(BuffType.JiGuanKeWen, o0); break;
            case 119:
                src.ModifyBuffValue(BuffType.JiGuanKeWen, o0);
                src.ModifyDef(src.GetBuffValue(BuffType.JiGuanKeWen) * o1);
                break;
            case 120:
                src.ModifyBuffValue(BuffType.JiGuanKeWen, o0);
                src.ModifyBuffValue(BuffType.JiGuanLingWen, o1);
                break;
            case 122:
                src.ModifyBuffValue(BuffType.JiGuanKeWen, o0);
                src.ModifyBuffValue(BuffType.JiGuanJianLie, 1);
                break;
            case 123:
                src.ModifyBuffValue(BuffType.JiGuanKeWen, o0);
                src.ModifyBuffValue(BuffType.JiGuanJianZhan, 1);
                break;
            case 125:
                src.ModifyBuffValue(BuffType.JiGuanKeWen, o0);
                src.ModifyBuffValue(BuffType.JiGuanJianZhen, 1);
                break;
            case 126:
                src.ModifyBuffValue(BuffType.JiGuanKeWen, o0);
                src.ModifyBuffValue(BuffType.JiGuanXingYi, o1);
                break;
            case 130:
                src.ModifyBuffValue(BuffType.JiGuanKeWen, o0);
                src.ModifyBuffValue(BuffType.JiGuanLingYin, 1);
                break;
            case 131: src.ModifyBuffValue(BuffType.JiGuanKeWen, o0 + WuXingFunctions.GetWuXingCountInDeck(src)); break;
            case 134:
                src.ModifyTiPo(o0);
                src.ModifyBuffValue(BuffType.JiGuanKeWen, o1);
                break;
            case 142: src.ModifyBuffValue(BuffType.KeYinWuKuangYan, o0); break;
            case 143: src.ModifyBuffValue(BuffType.KeYinHuLingLong, o0); break;
            case 144: src.ModifyBuffValue(BuffType.KeYinTaLingYun, o0); break;
            case 145: src.ModifyBuffValue(BuffType.KeYinYuLingFang, o0); break;
            case 148: src.ModifyBuffValue(BuffType.KeYinZhuoKunBie, o0); break;
            case 149: src.ModifyBuffValue(BuffType.KeYinBuLingDeng, 1); break;
            case 150: src.ModifyBuffValue(BuffType.KeYinBuXingGua, o0); break;
            case 152: src.ModifyBuffValue(BuffType.BeiGu, 1); break;
            case 154: src.ModifyBuffValue(BuffType.KeYinHuanYuanLing, o0); break;
            case 156: src.ModifyBuffValue(BuffType.KeYinZhenShanBeng, o0); break;
            case 157: src.ModifyBuffValue(BuffType.JiRanZhouYin, 1); break;
            case 158:
                src.ModifyBuffValue(BuffType.HuTi, o0);
                dst.ModifyBuffValue(BuffType.HuTi, o0);
                src.RemoveAllDebuff(o1);
                dst.RemoveAllDebuff(o1);
                break;
            case 162:
            {
                int n = 0;
                for (int i = 0; i < o0; i++)
                {
                    int p = src.NextParam(ParamRequest.OwnDebuff(ParamSite.KeYin162));
                    if (p == -1) break;
                    n++;
                    src.ModifyBuffValue((BuffType)p, -1);
                }
                if (n > 0) src.ModifyBuffValue(BuffType.JiaGong, n);
                break;
            }
            case 164: src.ModifyBuffValue(BuffType.KeYinZhanLingYi, 1); break;
            case 168: src.ModifyBuffValue(BuffType.KeYinWangBaiLu, 1); break;
            case 169: src.ModifyBuffValue(BuffType.KeYinLiZhiHuo, o0); break;
            case 170: src.ModifyBuffValue(BuffType.KeYinQuanZhiYong, 1); break;
            case 171: src.ModifyBuffValue(BuffType.KeYInPangBoShi, o0); break;
        }
    }
}
