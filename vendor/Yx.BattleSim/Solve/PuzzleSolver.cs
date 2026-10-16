using System.Collections.Concurrent;
using System.Diagnostics;

namespace Yx.BattleSim.Solve;

/// <summary>一套候选摆法及其战果。</summary>
public sealed record SolveEntry(int[] Cards, ArrangementScore Score, OrderResult[] Orders)
{
    public string? AlternativeFor { get; init; }
    internal ArrangementScore[]? OrderScores { get; init; }
}

/// <summary>求解进度快照（随时可取）。</summary>
public sealed record SolveProgress(
    string State, long Evaluated, double Candidates, bool Exhaustive, long ElapsedMs,
    IReadOnlyList<SolveEntry> Best, string? Error = null, int Seed = 1, int Samples = 3,
    bool CanWin = false, bool HasRandom = false, long Enumerated = 0, string Phase = "search", string SearchMode = "rank");

/// <summary>
/// 摆牌求解（随时可取当前最好结果）：
///   1. 局部搜索（从我方当前牌桌与随机起点出发，交换两格 / 换一张牌，邻域整批并行评估，局部最优后扰动再爬）—— 通常几千场内给出好解；
///   2. 摆法 × 先后手的场数不超过 <see cref="PuzzleRequest.ExhaustiveLimit"/> 时，剩余时间里穷举补齐（已算过的走缓存），穷举完即全局最优；
///   3. 否则一直局部搜索到时限或连续多轮没有进步。
/// 同一摆法只算一次（缓存）；评估器必须线程安全。
/// </summary>
public sealed class PuzzleSolver
{
    private const int StallRestarts = 40;       // 连续这么多轮重启没进步就收工（非穷举模式）
    private const int PerturbSteps = 3;
    private const int MaxSideways = 12;          // 平台上连续平移的上限
    private const int ExhaustiveChunk = 4096;
    private const int ExistenceChunk = 256;
    private const int ExistenceMemoLimit = 16384;

    private readonly PuzzleRequest _req;
    private readonly IArrangementEvaluator _eval;
    private readonly ArrangementSpace _space;
    private readonly bool[] _orders;
    private readonly int _dop;
    private readonly bool _existence;
    private readonly bool _unbounded;
    private readonly double _candidates;
    private readonly ConcurrentDictionary<ulong, SolveEntry> _memo = new();
    private readonly object _topLock = new();
    private readonly List<SolveEntry> _top = new();
    private readonly Dictionary<SolveEntry, string> _topKeys = new();
    private readonly SolveEntry?[] _orderBest = new SolveEntry?[2];
    private readonly Stopwatch _sw = new();
    private long _evaluated;
    private long _lastImproveMs;          // 第一名最近一次变好的时刻
    private volatile string _state = "pending";
    private volatile bool _exhaustive;
    private string? _error;
    private int _canWin;
    private int _hasRandom;
    private long _enumerated;
    private volatile string _phase = "search";

    public ArrangementSpace Space => _space;
    internal int CachedArrangementCount => _memo.Count;

    /// <summary>能上场：游戏 MoveToGrid 拦「只能炼化」「变化」两类与 id 1（CardType.Refine = 2、Change = 4）。</summary>
    private static bool Placeable(Config.JsonBattleConfig cfg, int id)
    {
        if (id == 1) return false;
        int ct = cfg.Card(id)?.CardType ?? 0;
        return ct != 2 && ct != 4;
    }

    public PuzzleSolver(PuzzleRequest req, IArrangementEvaluator eval)
    {
        _req = req;
        _eval = eval;
        if (req.SearchMode != "rank" && req.SearchMode != "existence" && req.SearchMode != "exhaustive") throw new ArgumentException("未知搜索模式");
        _existence = req.SearchMode == "existence";
        _unbounded = _existence || req.SearchMode == "exhaustive";
        if (req.Slots > 10) throw new ArgumentException("格数最多 10");
        var cfg = eval is SimEvaluator se ? se.Config : null;
        _space = new ArrangementSpace(req.Me.Cards, req.Slots, req.PadId,
            cfg is null ? null : id => cfg.CardIsConsume(id) || cfg.CardIsSustain(id),
            req.MaxConsumeSustain,
            cfg is null ? null : id => Placeable(cfg, id));
        _orders = req.First switch
        {
            "me" => new[] { true },
            "foe" => new[] { false },
            _ => new[] { true, false },
        };
        _dop = req.Threads > 0 ? req.Threads : Math.Max(1, Environment.ProcessorCount - 2);
        _candidates = _space.CountDistinct();
    }

    private bool FoundEnough => _existence && Volatile.Read(ref _canWin) != 0;
    private bool OrderAlternatives => _req.ShowOrderAlternatives && _req.First == "both" && !_existence;

    /// <summary>局部搜索是否已收敛（第一名连续 StallMs 没变好）。</summary>
    private bool Converged() =>
        _req.StallMs > 0 && _sw.ElapsedMilliseconds - Interlocked.Read(ref _lastImproveMs) > _req.StallMs;

    public SolveProgress Snapshot()
    {
        List<SolveEntry> best;
        lock (_topLock)
        {
            best = new List<SolveEntry>(_top);
            if (OrderAlternatives)
                for (int i = 0; i < 2; i++)
                {
                    var extra = _orderBest[i];
                    if (extra is null || OrderScore(extra, i).MinClass != OrderResult.Win) continue;
                    if (best.Any(e => OrderScore(e, i).MinClass == OrderResult.Win)) continue;
                    best.Add(extra with { AlternativeFor = i == 0 ? "me" : "foe" });
                }
        }
        return new SolveProgress(_state, Interlocked.Read(ref _evaluated), _candidates, _exhaustive,
            _sw.ElapsedMilliseconds, best, _error, _req.Seed, Math.Max(1, _req.Samples),
            Volatile.Read(ref _canWin) != 0, Volatile.Read(ref _hasRandom) != 0,
            Interlocked.Read(ref _enumerated), _phase, _req.SearchMode);
    }

    /// <summary>阻塞跑完（到时限 / 取消 / 穷举完 / 收敛）。</summary>
    public void Run(CancellationToken ct)
    {
        _sw.Start();
        _state = "running";
        try
        {
            if (_unbounded)
            {
                // Try promising boards briefly, then cover every legal arrangement, regardless of time/candidate limits.
                LocalSearch(ct, TimeSpan.FromMilliseconds(1000));
                if (!FoundEnough) ExhaustExistence(ct);
                _state = ct.IsCancellationRequested ? "cancelled" : "done";
                return;
            }
            var deadline = TimeSpan.FromMilliseconds(Math.Max(1, _req.TimeLimitMs));
            double distinct = _space.CountDistinct();
            bool canExhaust = _req.ExhaustiveLimit > 0 && distinct * _orders.Length * _eval.FoeCount <= _req.ExhaustiveLimit;
            // 能穷举时局部搜索只占时限的一小段（先出个好解），其余留给穷举。
            var lsBudget = canExhaust ? TimeSpan.FromMilliseconds(Math.Min(_req.TimeLimitMs / 4.0, 1500)) : deadline;
            LocalSearch(ct, lsBudget);
            if (canExhaust && !ct.IsCancellationRequested && _sw.Elapsed < deadline)
                Exhaust(ct, deadline);
            _state = ct.IsCancellationRequested ? "cancelled" : "done";
        }
        catch (OperationCanceledException) { _state = "cancelled"; }
        catch (Exception e) { _error = e.Message; _state = "error"; }
        finally { _sw.Stop(); }
    }

    /// <summary>
    /// 多个搜索者并行各自爬山（每核一个，共享缓存与前几名）：邻域小批量并行利用不满核，整条爬山路线并行才吃得满。
    /// 0 号搜索者从我方当前牌桌出发，其余从随机摆法出发；局部最优后交替「扰动全局最好」与「随机重启」。
    /// </summary>
    private void LocalSearch(CancellationToken ct, TimeSpan budget)
    {
        byte[]? start = _space.FromCardIds(_req.Me.Initial.Count > 0 ? _req.Me.Initial : _req.Me.Cards);
        var opts = new ParallelOptions { MaxDegreeOfParallelism = _dop, CancellationToken = ct };
        Parallel.For(0, OrderAlternatives ? Math.Max(3, _dop) : _dop, opts, w => Walk(w, w == 0 ? start : null, ct, budget));
    }

    private void Walk(int walker, byte[]? start, CancellationToken ct, TimeSpan budget)
    {
        var rng = new Random(_req.Seed * 7919 + walker);
        int objective = OrderAlternatives ? walker % 3 - 1 : -1;
        int stall = 0;
        ArrangementScore? localBest = null;
        bool first = true;
        while (!ct.IsCancellationRequested && !FoundEnough && _sw.Elapsed < budget && (objective >= 0 || !Converged()))
        {
            byte[] cur;
            if (first && start is not null) cur = start;
            else if (localBest is not null && stall % 2 == 1) cur = _space.Perturb(BestArr(objective) ?? _space.Random(rng), PerturbSteps, rng);
            else cur = _space.Random(rng);
            first = false;

            var curE = EvaluateOne(cur, ct);
            int sideways = 0;
            var visited = new HashSet<ulong> { ArrangementSpace.Key(cur) };
            while (!ct.IsCancellationRequested && !FoundEnough && _sw.Elapsed < budget)
            {
                var nb = _space.Neighbors(cur);
                int bi = -1;
                SolveEntry? be = null;
                var flat = new List<int>();
                for (int i = 0; i < nb.Count; i++)
                {
                    if (ct.IsCancellationRequested || FoundEnough || _sw.Elapsed >= budget) break;
                    var e = EvaluateOne(nb[i], ct);
                    if (OrderScore(e, objective) > OrderScore(curE, objective) && (be is null || OrderScore(e, objective) > OrderScore(be, objective))) { bi = i; be = e; }
                    else if (OrderScore(e, objective).CompareTo(OrderScore(curE, objective)) == 0 && !visited.Contains(ArrangementSpace.Key(nb[i]))) flat.Add(i);
                }
                if (be is null)
                {
                    // 平台：很多摆法同分，纯爬山停在平台上。允许有限步随机「平移」（不回头），跨过平台再找上坡。
                    if (flat.Count == 0 || sideways >= MaxSideways) break;
                    bi = flat[rng.Next(flat.Count)];
                    be = EvaluateOne(nb[bi], ct);
                    sideways++;
                }
                else sideways = 0;
                cur = nb[bi];
                curE = be;
                visited.Add(ArrangementSpace.Key(cur));
            }
            if (localBest is null || OrderScore(curE, objective) > localBest.Value) { localBest = OrderScore(curE, objective); stall = 0; }
            else stall++;
            if (stall >= StallRestarts) break;
        }
    }

    private ArrangementScore OrderScore(SolveEntry entry, int objective)
        => objective < 0 ? entry.Score : entry.OrderScores is not null ? entry.OrderScores[objective]
            : ArrangementScore.Of(Array.FindAll(entry.Orders, o => o.MeFirst == (objective == 0)), _req.ScoreTiPo);

    private byte[]? BestArr(int objective = -1)
    {
        lock (_topLock)
        {
            var best = objective >= 0 ? _orderBest[objective] : _top.Count > 0 ? _top[0] : null;
            return best is null ? null : _space.FromCardIds(best.Cards);
        }
    }

    private void Exhaust(CancellationToken ct, TimeSpan deadline)
    {
        _phase = "exhaustive";
        var all = _space.EnumerateAll();
        for (int i = 0; i < all.Count; i += ExhaustiveChunk)
        {
            if (ct.IsCancellationRequested || _sw.Elapsed >= deadline) return;
            EvaluateMany(all.GetRange(i, Math.Min(ExhaustiveChunk, all.Count - i)), ct);
        }
        _exhaustive = true;
    }

    private void ExhaustExistence(CancellationToken ct)
    {
        _phase = "exhaustive";
        var chunk = new List<byte[]>(ExistenceChunk);
        foreach (var arr in _space.EnumerateLazy(ct))
        {
            if (FoundEnough) return;
            chunk.Add(arr);
            if (chunk.Count < ExistenceChunk) continue;
            EvaluateMany(chunk, ct);
            chunk.Clear();
            if (FoundEnough) return;
        }
        if (chunk.Count > 0) EvaluateMany(chunk, ct);
        ct.ThrowIfCancellationRequested();
        if (!FoundEnough) _exhaustive = true;
    }

    /// <summary>评估一套摆法（已算过的直接取缓存）。线程安全。</summary>
    private SolveEntry EvaluateOne(byte[] arr, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ulong key = ArrangementSpace.Key(arr);
        if (_memo.TryGetValue(key, out var hit)) return hit;
        int[] cards = _space.ToCardIds(arr);
        int foes = Math.Max(1, _eval.FoeCount);
        var results = new List<OrderResult>(_orders.Length * foes);
        for (int f = 0; f < foes; f++)
        {
            bool[] orders = _req.First == "auto" ? _eval.AllowedOrders(cards, f) : _orders;
            foreach (bool order in orders)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(_eval.Evaluate(cards, order, f));
            }
        }
        var ors = results.ToArray();
        // Existential review verdicts must include samples outside the ranked top list.
        foreach (var result in ors)
        {
            if (result.HadRandom) Interlocked.Exchange(ref _hasRandom, 1);
            if (result.Class == OrderResult.Win || result.RandomWins > 0)
                Interlocked.Exchange(ref _canWin, 1);
        }
        Interlocked.Add(ref _evaluated, ors.Length);
        var e = new SolveEntry(cards, ArrangementScore.Of(ors, _req.ScoreTiPo), ors);
        if (OrderAlternatives) e = e with { OrderScores = new[] { OrderScore(e, 0), OrderScore(e, 1) } };
        bool cache = !_unbounded || (_phase == "search" && _memo.Count < ExistenceMemoLimit);
        if (!cache) { Offer(e); return e; }
        if (_memo.TryAdd(key, e)) Offer(e);
        return _memo[key];
    }

    /// <summary>并行评估一批摆法（已算过的直接取缓存），返回与输入同序的结果。</summary>
    private void EvaluateMany(List<byte[]> arrs, CancellationToken ct)
    {
        var opts = new ParallelOptions { MaxDegreeOfParallelism = _dop, CancellationToken = ct };
        Parallel.For(0, arrs.Count, opts, (i, loop) =>
        {
            if (FoundEnough) { loop.Stop(); return; }
            EvaluateOne(arrs[i], ct);
            Interlocked.Increment(ref _enumerated);
            if (FoundEnough) loop.Stop();
        });
    }

    /// <summary>分数高者优先；同分按卡 id 字典序（结果与线程调度无关，同输入同输出）。</summary>
    private bool Better(SolveEntry a, SolveEntry b)
    {
        if (_existence)
        {
            bool aw = HasWin(a), bw = HasWin(b);
            if (aw != bw) return aw;
        }
        int c = a.Score.CompareTo(b.Score);
        if (c != 0) return c > 0;
        for (int i = 0; i < a.Cards.Length && i < b.Cards.Length; i++)
            if (a.Cards[i] != b.Cards[i]) return a.Cards[i] < b.Cards[i];
        return false;
    }

    private static bool HasWin(SolveEntry entry)
    {
        foreach (var o in entry.Orders)
            if (o.Class == OrderResult.Win || o.RandomWins > 0) return true;
        return false;
    }

    private void Offer(SolveEntry e)
    {
        if (OrderAlternatives)
            lock (_topLock)
                for (int i = 0; i < 2; i++)
                {
                    var old = _orderBest[i];
                    int comparison = old is null ? 1 : OrderScore(e, i).CompareTo(OrderScore(old, i));
                    if (comparison > 0 || (comparison == 0 && CompareCards(e.Cards, old!.Cards) < 0)) _orderBest[i] = e;
                }
        int n = Math.Max(1, _req.TopN);
        // 先做便宜的排名筛选，复算行为签名不持有排名锁。
        lock (_topLock)
            if (_top.Count >= n && !Better(e, _top[^1])) return;
        string? key = _req.DeduplicateResults ? _eval.EquivalenceKey(e.Cards) : null;
        lock (_topLock)
        {
            if (_top.Count >= n && !Better(e, _top[^1])) return;
            if (key is not null)
            {
                int duplicate = _top.FindIndex(x => _topKeys.TryGetValue(x, out var k) && k == key
                    && x.Orders.AsSpan().SequenceEqual(e.Orders));
                if (duplicate >= 0)
                {
                    if (!Better(e, _top[duplicate])) return;
                    _topKeys.Remove(_top[duplicate]);
                    _top.RemoveAt(duplicate);
                }
                _topKeys[e] = key;
            }
            int at = _top.FindIndex(x => Better(e, x));
            if (at < 0) _top.Add(e); else _top.Insert(at, e);
            if (at == 0 || _top.Count == 1) Interlocked.Exchange(ref _lastImproveMs, _sw.ElapsedMilliseconds);
            if (_top.Count > n)
            {
                _topKeys.Remove(_top[^1]);
                _top.RemoveAt(_top.Count - 1);
            }
        }
    }

    private static int CompareCards(int[] a, int[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Length.CompareTo(b.Length);
    }
}
