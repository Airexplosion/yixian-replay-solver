using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 炼化（刻印）（P4）：忠实移植自游戏 KeYinCardFunctions。
///
/// 基础两钩子 <see cref="HasKeYinType"/> / <see cref="GetTotalKeYinOtherparam"/> 是伤害管线里成群刻印分支
/// （ModifyDef 结阵93·124、AfterHpModifyEffect 147、锋锐 32、机关剑列/剑斩 135/136…）依赖的入口——做实即点亮它们。
/// <see cref="OnBattleStart"/> 遍历自己的炼化卡、按类型写开局 buff（多为机关刻文/连云）。
///
/// 依赖 Config.KeYinOtherParams(id) 取参数。牌桌相邻升级(61/121 邻格)、行位(96/111)属 P5 牌桌，跳过并注明。
/// </summary>
public static class KeYinCardFunctions
{
    /// <summary>是否携带某炼化类型（id%10000 匹配）。忠实 HasKeYinType。</summary>
    public static bool HasKeYinType(Combatant src, int keYinId)
    {
        int type = ((keYinId % 10000) + 10000) % 10000;
        var list = src.BattleKeYinCards;
        for (int i = 0; i < list.Count; i++)
            if (((list[i] % 10000) + 10000) % 10000 == type) return true;
        return false;
    }

    /// <summary>同类型炼化卡 otherParams[index] 求和。忠实 GetTotalKeYinOtherparam。</summary>
    public static int GetTotalKeYinOtherparam(Combatant src, int keYinTypeId, int index)
    {
        int type = ((keYinTypeId % 10000) + 10000) % 10000;
        int sum = 0;
        var list = src.BattleKeYinCards;
        for (int i = 0; i < list.Count; i++)
            if (((list[i] % 10000) + 10000) % 10000 == type)
                sum += src.Config.KeYinOtherParams(list[i]).At(index);
        return sum;
    }

    /// <summary>
    /// 开局刻印：忠实 OnBattleStartKeYin（原码 194608，await 已剥）。
    /// ① 逐格：id 非 0 / 10167 且是**被动**刻印（描述含「[被动生效]」）才执行其开局效果；
    /// ② 末尾「生命修正」：所有非 0 刻印牌的 maxHp 加总，给持有者**加生命上限和血量**（原码 194697 `SetMaxHp/SetHp(+num3)`）。
    /// ⚠ ② 早先整段缺失 —— b4 里带刻印的 85 场几乎全错，且误差恰是「刻印方血量少一个常数、对方分毫不差」。
    /// ③ 论道 14（上一轮永久 buff LunDaoId == 14）：剩余刻印点数（maxKeYin − 已用点数，夹在 [0, maxKeYin]）× 境界 × 2 并入生命修正。
    /// </summary>
    public static void OnBattleStart(Combatant src)
    {
        var list = src.BattleKeYinCards;
        for (int i = 0; i < list.Count; i++)
        {
            int id = list[i];
            if (id == 0 || id == 10167) continue;
            if (!src.Config.KeYinDesc(id).Contains("[被动生效]")) continue;
            OnBattleStartKeYinCardFunction(src, id, i);
        }
        int bonus = 0, points = 0;
        for (int i = 0; i < list.Count; i++)
            if (list[i] != 0)
            {
                bonus += src.Config.KeYinMaxHp(list[i]);
                points += src.Config.KeYinPoints(list[i]);
            }
        if (src.GetLastRoundPermanentBuffValue(BuffType.LunDaoId) == 14)
        {
            int left = Math.Clamp(src.MaxKeYin - points, 0, Math.Max(0, src.MaxKeYin));
            bonus += left * src.Level * 2;
        }
        if (bonus > 0)
        {
            src.SetMaxHp(src.MaxHp + bonus);
            src.SetHp(src.Hp + bonus);
        }
    }

    /// <summary>单张炼化卡的开局效果（switch on id%10000）。忠实移植；牌桌相邻/行位分支跳过。</summary>
    public static void OnBattleStartKeYinCardFunction(Combatant src, int keYinCardId, int gridIndex)
    {
        int type = ((keYinCardId % 10000) + 10000) % 10000;
        IReadOnlyList<int> op = src.Config.KeYinOtherParams(keYinCardId);
        switch (type)
        {
            case 7:
                src.ModifyBuffValue(BuffType.LianYun, 1);
                break;
            // 61：相邻格位炼化升级 —— 牌桌数据，跳过（P5）。
            case 65:
                src.ModifyBuffValue(BuffType.BaiNiaoLingJianJue, 1);
                break;
            // 96 / 111：行位（格位标记）—— 牌桌数据，跳过（P5）。
            case 97:
                src.ModifyBuffValue(BuffType.KeYinXuTianMing, op.At(0));
                break;
            case 121:
                // 机关刻文部分保留；相邻升级部分（牌名/level 判定）属 P5，跳过。
                src.ModifyBuffValue(BuffType.JiGuanKeWen, op.At(0));
                break;
            case 124:
            case 127:
            case 128:
            case 129:
            case 132:
            case 133:
            case 135:
            case 136:
            case 137:
                src.ModifyBuffValue(BuffType.JiGuanKeWen, op.At(0));
                break;
            case 151:
                src.ModifyBuffValue(BuffType.XingYueYuShan, 1);
                break;
        }
    }
}
