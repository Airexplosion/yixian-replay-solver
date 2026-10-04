using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yx.BattleSim.Config;

namespace Yx.BattleSim.Solve;

/// <summary>
/// 求解的字符串接口（原生库导出与 Bench 共用同一条路径）：JSON 进、JSON 出，异常一律转成 <c>{"ok":false,"error":…}</c>。
///   <c>Start(PuzzleRequest)</c> → <c>{"ok":true,"job":N}</c>（后台线程开始算，立即返回）；
///   <c>Poll({"job":N})</c> → 进度报告（当前最好的前几名随时可读）；
///   <c>Cancel({"job":N})</c> → 取消；报告状态为 done / cancelled / error 后再 Poll 一次即释放。
/// </summary>
public static class SolveApi
{
    private static readonly ConcurrentDictionary<int, Job> Jobs = new();
    private static int _nextId;

    private sealed class Job
    {
        public required PuzzleSolver Solver { get; init; }
        public required CancellationTokenSource Cts { get; init; }
    }

    public static string Start(string requestJson)
    {
        try
        {
            var req = JsonSerializer.Deserialize(requestJson, SolveJsonContext.Default.PuzzleRequest)
                      ?? throw new ArgumentException("请求为空");
            var cfg = JsonBattleConfig.TryLoadDefault() ?? throw new InvalidOperationException("缺 battle_config 数据");
            var solver = new PuzzleSolver(req, new SimEvaluator(req, cfg));
            var job = new Job { Solver = solver, Cts = new CancellationTokenSource() };
            int id = Interlocked.Increment(ref _nextId);
            Jobs[id] = job;
            var t = new Thread(() => solver.Run(job.Cts.Token)) { IsBackground = true, Name = "yxsim-solve-" + id };
            t.Start();
            return "{\"ok\":true,\"job\":" + id + "}";
        }
        catch (Exception e) { return Error(e.Message); }
    }

    public static string Poll(string requestJson)
    {
        try
        {
            int id = JobId(requestJson);
            if (!Jobs.TryGetValue(id, out var job)) return Error("没有这个任务：" + id);
            var p = job.Solver.Snapshot();
            if (p.State is "done" or "cancelled" or "error") Jobs.TryRemove(id, out _);
            return JsonSerializer.Serialize(SolveReport.From(id, p), SolveJsonContext.Default.SolveReport);
        }
        catch (Exception e) { return Error(e.Message); }
    }

    public static string Cancel(string requestJson)
    {
        try
        {
            int id = JobId(requestJson);
            if (Jobs.TryGetValue(id, out var job)) job.Cts.Cancel();
            return "{\"ok\":true}";
        }
        catch (Exception e) { return Error(e.Message); }
    }

    private static int JobId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("job").GetInt32();
    }

    /// <summary>把请求序列化成 JSON（生成题目 / 调试用）。</summary>
    public static string ToJson(PuzzleRequest req) => JsonSerializer.Serialize(req, SolveJsonContext.Default.PuzzleRequest);

    public static string Error(string message) =>
        "{\"ok\":false,\"error\":" + JsonSerializer.Serialize(message, SolveJsonContext.Default.String) + "}";
}

/// <summary>对外的进度报告（JSON 形状）。</summary>
public sealed class SolveReport
{
    public bool Ok { get; set; } = true;
    public int Job { get; set; }
    public string State { get; set; } = "";
    public long Evaluated { get; set; }
    public double Candidates { get; set; }
    public bool Exhaustive { get; set; }
    public long ElapsedMs { get; set; }
    public int Seed { get; set; }
    public int Samples { get; set; }
    public string? Error { get; set; }
    public List<SolveReportEntry> Best { get; set; } = new();

    public static SolveReport From(int job, SolveProgress p)
    {
        var r = new SolveReport
        {
            Job = job, State = p.State, Evaluated = p.Evaluated, Candidates = p.Candidates,
            Exhaustive = p.Exhaustive, ElapsedMs = p.ElapsedMs, Error = p.Error, Ok = p.State != "error",
            Seed = p.Seed, Samples = p.Samples,
        };
        foreach (var e in p.Best)
        {
            var re = new SolveReportEntry
            {
                Cards = new List<int>(e.Cards),
                MinClass = e.Score.MinClass, SumClass = e.Score.SumClass, SumGap = e.Score.SumGap,
                SumKillTurn = e.Score.SumKillTurn,
                RandomWinPenalty = e.Score.RandomWinPenalty,
            };
            foreach (var o in e.Orders)
                re.Orders.Add(new SolveReportOrder
                {
                    First = o.MeFirst ? "me" : "foe", Result = o.ResultText, MyHp = o.MyHp, FoeHp = o.FoeHp,
                    Foe = o.Foe, KillTurn = o.KillTurn, HadRandom = o.HadRandom,
                    RandomWins = o.RandomWins, RandomRuns = o.RandomRuns,
                });
            r.Best.Add(re);
        }
        return r;
    }
}

public sealed class SolveReportEntry
{
    public List<int> Cards { get; set; } = new();
    public int MinClass { get; set; }
    public int SumClass { get; set; }
    public int SumGap { get; set; }

    /// <summary>各样本加权后打到「对方血量上限 ×1.1」的回合数之和（越小越好；99 = 没打到）。</summary>
    public int SumKillTurn { get; set; }

    /// <summary>随机获胜的排序惩罚，数值越小越好。</summary>
    public int RandomWinPenalty { get; set; }
    public List<SolveReportOrder> Orders { get; set; } = new();
}

public sealed class SolveReportOrder
{
    public string First { get; set; } = "";
    public string Result { get; set; } = "";
    public int MyHp { get; set; }
    public int FoeHp { get; set; }

    /// <summary>第几回合打到阈值（0 = 没打到）。</summary>
    public int KillTurn { get; set; }

    /// <summary>对手样本序号（单对手为 0）。</summary>
    public int Foe { get; set; }

    /// <summary>这一种先后手是否实际抽到了随机参数。</summary>
    public bool HadRandom { get; set; }

    /// <summary>随机模拟中的胜场；没有随机参数时为 0。</summary>
    public int RandomWins { get; set; }

    /// <summary>随机模拟总场次；没有随机参数时为 0。</summary>
    public int RandomRuns { get; set; }
}

/// <summary>源生成的 JSON 上下文（NativeAOT 下不能靠反射序列化）。字段名 camelCase，读取时不分大小写。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PuzzleRequest))]
[JsonSerializable(typeof(SolveReport))]
[JsonSerializable(typeof(string))]
internal partial class SolveJsonContext : JsonSerializerContext
{
}
