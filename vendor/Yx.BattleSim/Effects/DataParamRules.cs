using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 数据驱动卡（card_effects.local.json 里的 <c>{"nextparam"}</c> 表达式与 <c>randnext</c> / <c>nextparam</c> 谓词）的取参数语义。
///
/// 为什么不写进 JSON：那份文件由 extract_effects.py 从反编译自动抽取、会整份重生成，
/// 抽取器也不知道取到的值被拿去做什么（卡 id / buff 类型 / 百分比 / 区间）—— 值域来自卡面描述与笔记的逐点核对，
/// 所以按 baseId 在这里给规则，与手写效果处声明请求是同一套 <see cref="ParamRequest"/>。
/// <paramref name="ordinal"/> 是本次出牌里第几次取参数（从 0 起；同一张牌取两次且语义不同时用，如 4000091 先攻后血）。
/// 没登记的 baseId → <see cref="ParamKind.Unknown"/>（估值 -1）。
/// </summary>
public static class DataParamRules
{
    public static ParamRequest For(int baseId, int ordinal, Combatant src, BattleCard card)
    {
        var o = src.Config.CardOtherParams(card.Id);
        switch (baseId)
        {
            // ── A 类 ──
            case 9:
            case 395:
            case 1000052:
            case 10000089:
                return ParamRequest.Formula(card.Id);

            // ── B-百分比（0..99）──
            // 42 守株待兔：命中只让本牌永久变成影枭兔（本轮不生效），对本场战斗没有影响 → 给不命中的值，省一次抽样。
            case 42: return ParamRequest.Exact(baseId, 99);
            case 368:        // 马：GetNextParam，不吃卦象
            case 4000012:
            case 4000020:
            case 4000035:
            case 4000057:
            case 4000061:
            case 4000096:    // 两次都是 10% 判定
                return ParamRequest.Percent(baseId);

            // ── B-整数区间 ──
            case 6:          // 施加 o0~o1 层内伤
            case 3000001:    // o0~o1 伤害
            case 4000007:    // 星位满足时生命 +o0~o1（配置里暂缺这张卡）
            case 4000017:    // 生命 +o0~o1
            case 4000019:    // 对方减 o0~o1 生命上限
                return ParamRequest.Range(baseId, o.At(0), o.At(1));
            case 4000053:    // 投石问路：0~o0 层虚弱，然后 0~o1 层破绽（现已手写，保留以防回退到数据路径）
                return ParamRequest.Range(baseId, 0, o.At(ordinal == 0 ? 0 : 1));
            case 4000068:    // attack~randomAttack 攻（只取一次，所有段同值）
            case 4000088:
                return ParamRequest.Range(baseId, card.Attack, src.Config.CardRandomAttack(card.Id));
            case 4000091:    // 犀牛望月：先 attack~randomAttack 攻，再生命及上限 +o0~o1
                return ordinal == 0
                    ? ParamRequest.Range(baseId, card.Attack, src.Config.CardRandomAttack(card.Id))
                    : ParamRequest.Range(baseId, o.At(0), o.At(1));

            // ── B-小枚举 ──
            case 6000014: return ParamRequest.Choice(baseId, 3);   // 防 / 生命 / 护体

            // ── B-从自身现有负面里挑 ──
            case 100:
            case 9000026:
            case 10000009:
            case 10000049:   // 配置里暂缺这张卡
                return ParamRequest.OwnDebuff(baseId);

            // ── B-从固定池挑负面施加 ──
            case 6000007: return ParamRequest.FromDebuffPool(baseId, ParamDebuffPool.Six);

            // ── B-卡池 ──
            case 6000011: return ParamRequest.FromCardPool(baseId, ParamCardPool.Sect);
            case 341: return ParamRequest.FromCardPool(baseId, ParamCardPool.HighTreasure);
            case 349: return ParamRequest.HandPick(baseId);
            // 7000075 梦•浑天印：只有化神档（7040075）取参数 ——「与前一格牌相同五行和等级的化神期常规牌」。
            case 7000075: return ParamRequest.FromCardPool(baseId, ParamCardPool.HuaShenSameWuXing);

            // ── C 类：380（当前发行的卡表里没有这张卡，描述 / 卡池未知）。
            //    -1 的后果已核对为中性：usecard 见 -1 直接跳过，追加的再行动挂在 != -1 上。
            default:
                return ParamRequest.Unknown(baseId);
        }
    }
}
