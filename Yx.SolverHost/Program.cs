using System.Text.Json;
using System.Text.Json.Nodes;
using Yx.BattleSim;
using Yx.BattleSim.Config;
using Yx.BattleSim.Solve;

var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
int dataIndex = Array.IndexOf(args, "--data");
string dataDirectory = dataIndex >= 0 && dataIndex + 1 < args.Length ? args[dataIndex + 1] : AppContext.BaseDirectory;
try
{
    var missing = SimData.Initialize(dataDirectory);
    if (missing.Count > 0) throw new InvalidOperationException("缺少求解数据：" + string.Join(",", missing));
    var config = JsonBattleConfig.TryLoadDefault()!;
    int cores = Environment.ProcessorCount;
    object capabilities = new { ok = true, ready = true, version = "2026-10-15-baijia", gameRecordVersion = "001.0008.0002",
        cores, defaultThreads = cores, modes = new[] { "exhaustive", "existence", "rank" },
        configVersion = config.Version, baijia = true };
    Console.WriteLine(JsonSerializer.Serialize(capabilities, json));
    PuzzleSolver? solver = null;
    CancellationTokenSource? cancellation = null;
    Thread? thread = null;
    int threads = 0;
    string? line;
    while ((line = Console.ReadLine()) is not null)
    {
        try
        {
            using var command = JsonDocument.Parse(line);
            string? op = command.RootElement.GetProperty("op").GetString();
            object response;
            switch (op)
            {
                case "capabilities": response = capabilities; break;
                case "start":
                    if (thread?.IsAlive == true) throw new ArgumentException("已有求解任务运行中");
                    var request = command.RootElement.GetProperty("request").Deserialize<PuzzleRequest>(json)
                        ?? throw new ArgumentException("缺少盘面");
                    threads = request.Threads <= 0 ? cores : Math.Clamp(request.Threads, 1, cores);
                    request.Threads = threads;
                    if (request.Slots is < 1 or > 8 || request.Me.Cards.Count > 64 || request.Foes.Count > 8)
                        throw new ArgumentException("牌位须为 1–8，牌池最多 64 张，对手样本最多 8 个");
                    if (request.TopN is < 1 or > 20 || request.Samples is < 1 or > 100)
                        throw new ArgumentException("topN 须为 1–20，samples 须为 1–100");
                    if (request.First is not ("me" or "foe" or "both" or "auto")) throw new ArgumentException("先手选项无效");
                    if (request.DeathFightMode is not ("record" or "auto" or "tie" or "off")) throw new ArgumentException("死战模式无效");
                    if (request.SearchMode == "rank" && request.TimeLimitMs <= 0) throw new ArgumentException("限时模式须设置正数 timeLimitMs");
                    IEnumerable<PuzzleSide> sides = new[] { request.Me, request.Foe }.Concat(request.Foes);
                    foreach (var side in sides)
                    {
                        if (side != request.Me && side.Cards.Count > 8) throw new ArgumentException("对手牌桌最多 8 格");
                        foreach (int id in side.Cards.Concat(side.Initial).Concat(side.HandCards))
                            if (id != 0 && config.Card(id) is null) throw new ArgumentException("未收录的卡牌：" + id);
                    }
                    cancellation?.Dispose();
                    cancellation = new CancellationTokenSource();
                    solver = new PuzzleSolver(request, new SimEvaluator(request, config));
                    var runningSolver = solver;
                    var token = cancellation.Token;
                    thread = new Thread(() => runningSolver.Run(token)) { IsBackground = true, Name = "yx-api-solver" };
                    thread.Start();
                    response = new { ok = true, threads, searchMode = request.SearchMode, cores }; break;
                case "poll":
                    if (solver is null) throw new ArgumentException("没有求解任务");
                    var progress = solver.Snapshot();
                    var report = JsonSerializer.SerializeToNode(SolveReport.From(1, progress), json)!.AsObject();
                    report["threads"] = threads;
                    report["progressPercent"] = progress.Exhaustive ? 100.0
                        : progress.Candidates > 0 ? Math.Min(100.0, 100.0 * progress.Enumerated / progress.Candidates) : 0.0;
                    response = report; break;
                case "cancel": cancellation?.Cancel(); response = new { ok = true }; break;
                case "shutdown": cancellation?.Cancel(); thread?.Join(5000); return;
                default: throw new ArgumentException("未知求解操作");
            }
            Console.WriteLine(JsonSerializer.Serialize(response, json));
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = error.Message }, json));
        }
    }
    cancellation?.Cancel();
    thread?.Join(5000);
}
catch (Exception error)
{
    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = error.Message }, json));
    Environment.ExitCode = 1;
}
