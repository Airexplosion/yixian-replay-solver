using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yx.BattleSim.Config;
using Yx.BattleSim.Combat;
using Yx.BattleSim.Effects;
using Yx.BattleSim.Model;
using Yx.BattleSim.Resolve;
using Yx.BattleSim.Solve;
namespace Yx.BattleSim.Predict;

public sealed class PredictionRequest
{
    public int Round { get; set; } = 1;
    public bool Fast { get; set; }
    public bool FoeMirror { get; set; }
    public bool FoeKnown { get; set; } = true;
    public int Samples { get; set; } = 3;
    public int Seed { get; set; } = 1;
    public PuzzleSide Me { get; set; } = new();
    public PuzzleSide Foe { get; set; } = new();
}
public sealed record PredictionStep(int Turn, string Actor, int Card, string Name, int Damage, int MyHp, int FoeHp, int MyDef, int FoeDef);
public sealed record PredictionRun(bool MeFirst, int Sample, bool Random, string? Winner, int MyHp, int FoeHp, int Gap,
    LifePrediction? Life, string? Loser, int MyLife, int FoeLife, PredictionStep[] Steps)
{
    public int[] IndependentDamage { get; init; } = [];
    public bool FoeKnown { get; init; } = true;
}
public sealed record PredictionReport(bool Ok, int Job, string State, PredictionRun[] Runs, string? Error = null);

public static class PredictionApi
{
    private static int _next;
    private sealed record Job(Task<PredictionRun[]> Task, DateTime Created);
    private static readonly ConcurrentDictionary<int, Job> Jobs = new();
    public static string Start(string json)
    {
        var req = JsonSerializer.Deserialize(json, PredictionJson.Default.PredictionRequest) ?? throw new ArgumentException("请求为空");
        if (req.Round < 1 || req.Me.Cards.Count is < 1 or > 10 || (req.FoeKnown && req.Foe.Cards.Count is < 1 or > 10))
            throw new ArgumentException("需要双方可见牌桌（1–10格）与有效轮次");
        foreach (var old in Jobs)
            if (old.Value.Task.IsCompleted && DateTime.UtcNow - old.Value.Created > TimeSpan.FromMinutes(1)) Jobs.TryRemove(old.Key, out _);
        if (Jobs.Count >= 8) throw new InvalidOperationException("计算任务过多");
        var cfg = JsonBattleConfig.TryLoadDefault() ?? throw new InvalidOperationException("缺战斗数据");
        var knownCards = req.Me.Cards.Concat(req.Me.HandCards);
        if (req.FoeKnown) knownCards = knownCards.Concat(req.Foe.Cards).Concat(req.Foe.HandCards);
        foreach (int id in knownCards)
            if (id != 0 && cfg.Card(id) == null) throw new ArgumentException("未支持的牌：" + id);
        int job = Interlocked.Increment(ref _next);
        Jobs[job] = new Job(Task.Run(() => Compute(req, cfg)), DateTime.UtcNow);
        return Serialize(new(true, job, "running", []));
    }
    public static string Poll(string json)
    {
        using var doc = JsonDocument.Parse(json);
        int job = doc.RootElement.GetProperty("job").GetInt32();
        if (!Jobs.TryGetValue(job, out var entry)) return Serialize(new(false, job, "error", [], "任务已失效"));
        var task = entry.Task;
        if (!task.IsCompleted) return Serialize(new(true, job, "running", []));
        Jobs.TryRemove(job, out _);
        try { return Serialize(new(true, job, "done", task.GetAwaiter().GetResult())); }
        catch (Exception e) { return Serialize(new(false, job, "error", [], e.Message)); }
    }
    public static string Cancel(string json)
    {
        using var doc = JsonDocument.Parse(json);
        int job = doc.RootElement.GetProperty("job").GetInt32();
        Jobs.TryRemove(job, out _);
        return Serialize(new(true, job, "cancelled", []));
    }
    public static PredictionRun[] Compute(PredictionRequest req, IBattleConfig cfg)
    {
        var runs = new List<PredictionRun>();
        foreach (bool first in new[] { true, false })
            for (int sample = 0; sample < Math.Clamp(req.Samples, 1, 10); sample++)
            {
                int seed = unchecked(req.Seed * 7919 + sample * 104729 + (first ? 1 : 0));
                ParamEstimator.BeginRun(seed);
                var input = new BattleInput {
                    Left = req.Me.ToSideWith("L", req.Me.Cards, cfg) with { EstimateParams = true, UnlockGrids = req.Me.Cards.Count },
                    Right = req.FoeKnown ? req.Foe.ToSideWith("R", req.Foe.Cards, cfg) with { EstimateParams = true, UnlockGrids = req.Foe.Cards.Count }
                        : new SideInput { Uid = "R", StartHp = 1, Board = [] },
                    Round = req.Round, FastMode = req.Fast, FirstPlayerUid = first ? "L" : "R"
                };
                var run = req.FoeKnown ? Trace(input, cfg, first, sample)
                    : new PredictionRun(first, sample + 1, false, null, 0, 0, 0, null, null, input.Left.Life, 0, []) { FoeKnown = false };
                ParamEstimator.BeginRun(seed);
                var independent = IndependentOffense.Calculate(input.Left, req.Round, first, cfg);
                run = run with { IndependentDamage = independent, Random = run.Random || ParamEstimator.RandomDraws > 0 };
                if (req.FoeMirror && run.Loser == "R")
                    run = run with { Life = new LifePrediction(run.Life!.BaseDamage, 0, 0, input.Right.Life, ["心魔不扣命元"]), FoeLife = input.Right.Life };
                runs.Add(run);
                if (!run.Random) break;
            }
        return runs.ToArray();
    }
    public static PredictionRun Trace(BattleInput input, IBattleConfig cfg, bool first, int sample)
    {
        var rows = new List<PredictionStep>();
        Combatant? left = null, right = null;
        int turn = 0, damage = 0, turnDamage = 0; bool active = false; string owner = "L";
        var previous = StepLog.PredictionSink;
        var prevDamage = Combatant.HpDeltaSink;
        void Observe(string kind, int value, Combatant c)
        {
            left = c.Uid == "L" ? c : c.Opponent;
            right = left.Opponent;
            if (kind == "turn-start") { turn = value; damage = 0; turnDamage = 0; active = true; owner = c.Uid; }
            if (kind == "play-start") { damage = 0; active = true; owner = c.Uid; }
            if (kind == "play-end" || kind == "turn")
            {
                bool card = kind == "play-end";
                string actor = card ? c.Uid : (first == (turn % 2 == 1) ? "L" : "R");
                // Always show turn-end, including anima shortages and buff ticks.
                rows.Add(new(turn, actor, card ? value : -1, card ? (value == 0 ? "普通攻击" : cfg.CardName(value)) : "回合合计",
                    card ? damage : turnDamage, left.Hp, right.Hp, left.Def, right.Def));
                damage = 0; active = true;
            }
        }
        StepLog.PredictionSink = Observe;
        Combatant.HpDeltaSink = (target, delta, cost) => { if (active && target.Uid != owner && delta < 0 && !cost) { damage -= delta; turnDamage -= delta; } };
        try
        {
            var result = BattleResolver.Resolve(input);
            string? loser = result.WinnerUid == "L" ? "R" : result.WinnerUid == "R" ? "L" : null;
            LifePrediction? life = loser == null ? null : LifeSettlement.Calculate(loser == "L" ? left! : right!, result.LifeDamage);
            return new(first, sample + 1, ParamEstimator.RandomDraws > 0, result.WinnerUid, result.LeftHp, result.RightHp, result.EndHpGap,
                life, loser, loser == "L" ? life!.Remaining : left!.TempLife, loser == "R" ? life!.Remaining : right!.TempLife, rows.ToArray());
        }
        finally { StepLog.PredictionSink = previous; Combatant.HpDeltaSink = prevDamage; }
    }
    static string Serialize(PredictionReport report) => JsonSerializer.Serialize(report, PredictionJson.Default.PredictionReport);
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PredictionRequest))]
[JsonSerializable(typeof(PredictionReport))]
internal partial class PredictionJson : JsonSerializerContext { }
