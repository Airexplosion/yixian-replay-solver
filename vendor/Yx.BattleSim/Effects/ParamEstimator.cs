using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 没有真实参数队列时（摆牌求解：开战前拿不到服务器下发的 battleParams），按**消费点声明的请求**估一个参数值。
///
/// 背景：battleParams 由服务器在开战时生成、随 BattleResult 下发（对拍时由探针给游戏和 sim 喂同一串，所以一直对得上）。
/// 早先估值器按 <c>CurrentCardId</c> 猜，只会灵猫乱剑、其余返回 -1 —— 而 -1 在 `roll &lt; X` 判定里是**必中**
/// （368 马每次 +2026 攻、天赋 108 后招每次都触发），被当攻 / 防值用时是 -1 攻 / -1 防；
/// 不在出牌过程里的消费点（回合钩子、刻印、天赋）还会拿到上一张牌的规则。
/// 现在每个消费点把「语义 + 值域」随 <see cref="ParamRequest"/> 传进来（全清单见
/// docs/superpowers/notes/2026-09-23-battle-params-inventory.md），这里只按请求种类估：
///   · A 类（<see cref="ParamKind.Formula"/>）照盘面公式精确算（<see cref="ParamFormulas"/>）；
///   · 吃到卦象（<see cref="ParamRequest.Lucky"/>）→ 最优值：区间取上限、百分比取 0；
///   · 其余 B 类用本线程随机源在值域里抽样（<see cref="Draw"/> / <see cref="Pick"/>），求解器按
///     <see cref="RandomDraws"/> 决定是否多次抽样取平均；
///   · C 类返回 -1（那几个消费点对 -1 的处理已核对为中性）。
/// </summary>
public static class ParamEstimator
{
    [ThreadStatic] private static Random? _rng;
    [ThreadStatic] private static int _draws;
    [ThreadStatic] private static int _unknown;

    /// <summary>开始一场评估：设本线程的随机种子，清零抽样计数。</summary>
    public static void BeginRun(int seed)
    {
        _rng = new Random(seed);
        _draws = 0;
        _unknown = 0;
    }

    /// <summary>本线程上一场评估里抽了几次真随机参数（0 = 结果确定，不必多次抽样）。</summary>
    public static int RandomDraws => _draws;

    /// <summary>本线程上一场评估里有几次取参数落在未建模（C 类 / 未登记）的消费点上，按 -1 处理。</summary>
    public static int UnknownRequests => _unknown;

    /// <summary>在 [lo, hi] 里均匀抽一个（真随机参数用）。区间只有一个值时不算一次抽样。</summary>
    public static int Draw(int lo, int hi)
    {
        if (hi <= lo) return lo;
        _draws++;
        _rng ??= new Random(1);
        return _rng.Next(lo, hi + 1);
    }

    /// <summary>从候选里均匀抽一个；没有候选返回 -1。</summary>
    public static int Pick(IReadOnlyList<int> candidates)
    {
        if (candidates.Count == 0) return -1;
        return candidates[Draw(0, candidates.Count - 1)];
    }

    /// <summary>按请求估值。-1 只出现在「没有了」（无负面 / 无候选 / A 类找不到牌）和 C 类上。</summary>
    public static int Estimate(Combatant c, in ParamRequest r)
    {
        switch (r.Kind)
        {
            case ParamKind.Exact: return r.Lo;
            case ParamKind.Range: return r.Lucky ? System.Math.Max(r.Lo, r.Hi) : Draw(r.Lo, r.Hi);
            case ParamKind.Percent: return r.Lucky ? 0 : Draw(0, 99);
            case ParamKind.Choice: return Draw(0, r.Hi);
            case ParamKind.OwnDebuff: return PickOwnDebuff(c);
            case ParamKind.DebuffPool: return Pick(ParamPools.Debuffs((ParamDebuffPool)r.Lo));
            case ParamKind.CardPool:
                return (ParamCardPool)r.Lo == ParamCardPool.HuaShenSameWuXing
                    ? ParamPools.PickHuaShenSameWuXing(c)
                    : Pick(ParamPools.Cards(c, (ParamCardPool)r.Lo));
            case ParamKind.HandPick: return PickHandConsumeOrSustain(c);
            case ParamKind.Formula: return ParamFormulas.Evaluate(c, r.CardId);
            default:
                _unknown++;
                return -1;
        }
    }

    /// <summary>
    /// 在**当前**身上的负面类型里均匀挑一种（yisim 的同类实现都是「按类型均匀、减到 0 就移出候选」；
    /// 每次调用都重新看身上，所以循环里「减到 0 后移出」自然成立）。没有负面 → -1。
    /// </summary>
    private static int PickOwnDebuff(Combatant c)
    {
        var list = c.GetDebuffList();
        if (list.Count == 0) return -1;
        return (int)list[Draw(0, list.Count - 1)];
    }

    /// <summary>349 梦•回响阵纹：手牌中的消耗 / 持续牌里均匀挑一张；没有 → -1。</summary>
    private static int PickHandConsumeOrSustain(Combatant c)
    {
        var hand = c.LastRoundHandCards;
        var cand = new List<int>();
        foreach (int id in hand)
            if (c.Config.CardIsConsume(id) || c.Config.CardIsSustain(id)) cand.Add(id);
        return Pick(cand);
    }
}
