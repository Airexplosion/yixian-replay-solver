using Yx.BattleSim.Model;

namespace Yx.BattleSim.Resolve;

/// <summary>
/// 批量并发结算。每场战斗自带工作态、互不共享 → 直接跨真实线程并行（纯 .NET 线程，非 ILRuntime，
/// 无主线程约束）。这是「同时发多个并发一起算」的落点：摆牌搜索一次可把几十/上百套候选×对手采样
/// 一口气铺满所有核心。结果**保序**（第 i 个输入对应第 i 个输出），确定性不受并行度影响。
/// </summary>
public static class BattleSimPool
{
    /// <summary>
    /// 并发结算一批战斗，返回与输入同序的结果。
    /// </summary>
    /// <param name="inputs">待算的战斗输入。</param>
    /// <param name="maxDegreeOfParallelism">最大并行度；&lt;=0 表示用全部逻辑核。</param>
    public static BattleOutcome[] SimulateBatch(
        IReadOnlyList<BattleInput> inputs,
        int maxDegreeOfParallelism = 0)
    {
        int n = inputs.Count;
        var results = new BattleOutcome[n];
        if (n == 0) return results;

        int dop = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
        if (dop == 1 || n == 1)
        {
            for (int i = 0; i < n; i++) results[i] = BattleResolver.Resolve(inputs[i]);
            return results;
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = dop };
        Parallel.For(0, n, options, i =>
        {
            results[i] = BattleResolver.Resolve(inputs[i]);
        });
        return results;
    }

    /// <summary>
    /// 摆牌搜索常见形态：对每套候选摆法（我方）× 一组对手采样求结果，返回 [候选][对手] 的结果矩阵。
    /// 全矩阵一次并发铺满，用于对候选按对对手分布的 EV / 最坏值 argmax。
    /// </summary>
    public static BattleOutcome[][] EvaluateCandidates(
        IReadOnlyList<SideInput> candidates,
        IReadOnlyList<SideInput> opponents,
        int round = 1,
        bool fastMode = false,
        int maxDegreeOfParallelism = 0)
    {
        var flat = new List<BattleInput>(candidates.Count * opponents.Count);
        foreach (var c in candidates)
            foreach (var o in opponents)
                flat.Add(new BattleInput { Left = c, Right = o, Round = round, FastMode = fastMode });

        var outcomes = SimulateBatch(flat, maxDegreeOfParallelism);

        var matrix = new BattleOutcome[candidates.Count][];
        int k = 0;
        for (int ci = 0; ci < candidates.Count; ci++)
        {
            matrix[ci] = new BattleOutcome[opponents.Count];
            for (int oi = 0; oi < opponents.Count; oi++)
                matrix[ci][oi] = outcomes[k++];
        }
        return matrix;
    }
}
