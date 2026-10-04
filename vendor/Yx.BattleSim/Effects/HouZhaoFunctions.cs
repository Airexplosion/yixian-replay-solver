using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 后招（忠实移植自 <c>CardActionBase.CheckHouZhao</c>）。
///
/// 「是不是后招」<b>不看任何结构化字段，只看卡描述里有没有 <c>[后招]：</c></b>（见
/// <see cref="CardTypes.IsHouZhao"/>）；而「这张后招<b>能不能触发</b>」由 <see cref="CheckHouZhao"/>
/// 按「首次使用 / 显法之人 / 刻印73 / 共鸣69 / 直接触发符 / 天赋108 抽签」六条路放行。
/// 全卡池 30+ 张卡体 + OnBeforeExecuted/OnAfterExecuted 里的 5 处都用它做门。
///
/// ⚠ 副作用不是纯判断：它**每次**都会给 src 挂一层 <c>ShiYongHouZhao</c>，并在首次使用时发天赋/仙命奖励。
/// 所以不能用「先判后调」优化掉调用。
/// </summary>
public static class HouZhaoFunctions
{
    /// <summary>
    /// CheckHouZhao：本张牌是否作为「后招」触发。忠实照抄原分支顺序（顺序敏感——副作用与返回值交错）。
    /// </summary>
    /// <param name="src">出牌方。</param>
    /// <param name="cardId">当前牌整 id（天赋判据用不到它，但首次使用奖励与 grid 配对）。</param>
    /// <param name="gridNumber">当前牌所在格（判 hadUsed 与刻印73 的「第 7 格」条件）。</param>
    public static bool CheckHouZhao(Combatant src, int cardId, int gridNumber)
    {
        // 1) 出过就记一层「使用后招」——黄雀在候/灵雀在候/使用后招系列按它判。
        src.ModifyBuffValue(BuffType.ShiYongHouZhao, 1);

        // 2) 这张牌**本次战斗第一次**被出：四档天赋 71（i*10000+71）各给一次 防/血上限/血；仙命332 给对手挂虚化。
        if (!src.HadUsed(gridNumber))
        {
            for (int i = 0; i < 4; i++)
            {
                int talentId = i * 10000 + 71;
                if (!src.HasTalent(talentId)) continue;
                int num = src.Config.TalentOtherParams(talentId).At(0);
                src.ModifyDef(num);
                src.ModifyMaxHp(num);
                src.ModifyHp(num);
            }
            if (src.HasFateStrategy(332))
                src.Opponent.ModifyBuffValue(BuffType.XuRuo, 1);
        }

        // 3) 六条放行路。
        if (src.HadUsed(gridNumber) || src.HasBuff(BuffType.XianFaZhiRen)) return true;

        if (src.Subs.HasKeYinType(src, 73) && gridNumber == 7) return true;

        if (src.IsTalentResonanceEffective(69) && src.CheckTalentResonanceTempFlag(69))
        {
            src.RemoveTalentResonanceTempFlag(69);
            return true;
        }

        if (src.HasBuff(BuffType.XiaCiHouZhaoZhiJieChuFa))
        {
            src.ModifyBuffValue(BuffType.XiaCiHouZhaoZhiJieChuFa, -1);
            return true;
        }

        // 天赋108：抽一次玩家参数，< 1 即触发（参数是玩家预选的确定性输入，见 GetNextRandomValue）。
        if (src.HasTalent(108) && src.GetNextRandomValue(ParamRequest.Percent(ParamSite.Talent108)) < 1) return true;

        return false;
    }
}
