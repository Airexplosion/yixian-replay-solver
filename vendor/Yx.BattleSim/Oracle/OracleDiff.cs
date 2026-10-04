using Yx.BattleSim.Model;
using Yx.BattleSim.Resolve;

namespace Yx.BattleSim.Oracle;

/// <summary>一条用例的 diff 结果。</summary>
public sealed record OracleResult(string Name, bool Ok, string Detail);

/// <summary>
/// 对拍：对每条金标用例，用 sim 复算并与真引擎结果逐场比对。
/// 主判据是**结束血差 / 双方结束 hp**（摆牌目标函数就靠它），其次胜负与生命伤害。
/// </summary>
public static class OracleDiff
{
    public static OracleResult Run(OracleCase c)
    {
        BattleOutcome sim;
        try { sim = BattleResolver.Resolve(c.ToInput()); }
        catch (Exception ex) { return new OracleResult(c.Name, false, "sim 抛异常: " + ex.Message); }

        var diffs = new List<string>();
        if (sim.LeftHp != c.Real.LeftHp) diffs.Add($"leftHp sim={sim.LeftHp} real={c.Real.LeftHp}");
        if (sim.RightHp != c.Real.RightHp) diffs.Add($"rightHp sim={sim.RightHp} real={c.Real.RightHp}");
        // 胜负：null(真) 表示未采集，跳过该项
        if (c.Real.Winner is not null && sim.WinnerUid != c.Real.Winner)
            diffs.Add($"winner sim={sim.WinnerUid ?? "null"} real={c.Real.Winner}");
        if (c.Real.Life != 0 && sim.LifeDamage != c.Real.Life)
            diffs.Add($"life sim={sim.LifeDamage} real={c.Real.Life}");

        return diffs.Count == 0
            ? new OracleResult(c.Name, true, "ok")
            : new OracleResult(c.Name, false, string.Join("; ", diffs));
    }

    public static (int total, int ok, List<OracleResult> fails) RunAll(IEnumerable<OracleCase> cases)
    {
        int total = 0, ok = 0;
        var fails = new List<OracleResult>();
        foreach (var c in cases)
        {
            total++;
            var r = Run(c);
            if (r.Ok) ok++; else fails.Add(r);
        }
        return (total, ok, fails);
    }
}
