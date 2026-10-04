using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.Versioning;
using Yx.BattleSim;
using Yx.BattleSim.Config;
using Yx.BattleSim.Solve;

namespace Yx.WebSim;

[SupportedOSPlatform("browser")]
public partial class BrowserApi
{
    private static JsonBattleConfig? config;

    [JSExport]
    public static string Initialize(string cards, string effects)
    {
        try
        {
            Directory.CreateDirectory("/yxdata");
            File.WriteAllText("/yxdata/battle_config.json", cards);
            File.WriteAllText("/yxdata/card_effects.json", effects);
            var missing = SimData.Initialize("/yxdata");
            if (missing.Count > 0) throw new InvalidOperationException(string.Join(",", missing));
            config = JsonBattleConfig.TryLoadDefault()!;
            return "{\"ok\":true}";
        }
        catch (Exception e) { return SolveApi.Error(e.Message); }
    }

    [JSExport]
    public static string Solve(string json)
    {
        try
        {
            if (config is null) throw new InvalidOperationException("sim 未就绪");
            var req = JsonSerializer.Deserialize(json, BrowserJsonContext.Default.PuzzleRequest)
                ?? throw new ArgumentException("缺少盘面");
            req.Threads = 1;
            req.TopN = Math.Clamp(req.TopN, 1, 10);
            req.TimeLimitMs = Math.Clamp(req.TimeLimitMs, 100, 30000);
            req.ExhaustiveLimit = Math.Min(req.ExhaustiveLimit, 100000);
            if (req.Slots is < 1 or > 8 || req.Me.Cards.Count > 24 || req.Foe.Cards.Count > 8)
                throw new ArgumentException("牌位须为 1–8，牌池最多 24 张");
            foreach (int id in req.Me.Cards.Concat(req.Foe.Cards))
                if (id != 0 && config.Card(id) is null) throw new ArgumentException("未收录的卡牌：" + id);
            var solver = new PuzzleSolver(req, new SimEvaluator(req, config));
            solver.Run(CancellationToken.None);
            return JsonSerializer.Serialize(SolveReport.From(0, solver.Snapshot()), BrowserJsonContext.Default.SolveReport);
        }
        catch (Exception e) { return SolveApi.Error(e.Message); }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PuzzleRequest))]
[JsonSerializable(typeof(SolveReport))]
internal partial class BrowserJsonContext : JsonSerializerContext { }
