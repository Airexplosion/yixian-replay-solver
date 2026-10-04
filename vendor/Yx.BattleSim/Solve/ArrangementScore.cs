using Yx.BattleSim.Config;
using Yx.BattleSim.Model;
using Yx.BattleSim.Resolve;

namespace Yx.BattleSim.Solve;

/// <summary>一种先后手下的战果（我方视角）。</summary>
public readonly record struct OrderResult(bool MeFirst, int Class, int MyHp, int FoeHp)
{
    /// <summary>第几个对手样本（<see cref="PuzzleRequest.Foes"/> 的下标；单对手为 0）。</summary>
    public int Foe { get; init; }

    /// <summary>这个结果在综合分里的权重（= 对手样本权重）。</summary>
    public int Weight { get; init; } = 1;

    /// <summary>
    /// 第几个回合把对方打掉「血量上限 × 1.1」的累计伤害（见 <see cref="SimEvaluator"/>）。
    /// 0 = 整场没打到。越小越好 —— 这是「伤害前置」的度量：越早把对方打死越好。
    /// </summary>
    public int KillTurn { get; init; }

    /// <summary>胜 = 2、负 = 0（游戏没有平局，见 <see cref="SimEvaluator.Judge"/>；Draw 只留给假评估器 / 以后的模式）。</summary>
    public const int Win = 2, Draw = 1, Loss = 0;

    public string ResultText => Class == Win ? "win" : Class == Draw ? "draw" : "loss";

    /// <summary>战斗结束时我方的 [体魄]（从上一场带下来的那个）。只有 <c>PuzzleRequest.ScoreTiPo</c> 开了才填。</summary>
    public int TiPo { get; init; }

    /// <summary>这一种先后手的模拟是否实际抽到了随机参数。</summary>
    public bool HadRandom { get; init; }

    /// <summary>随机模拟中的胜场；没有随机参数时为 0。</summary>
    public int RandomWins { get; init; }

    /// <summary>随机模拟总场次；没有随机参数时为 0。</summary>
    public int RandomRuns { get; init; }
}

/// <summary>
/// 一套摆法在各先后手下的综合分，可比较：先比「最差的那种先后手」（都赢 > 有一种平 > 有一种输），
/// 再比各先后手胜负之和，再比**前置伤害**（各样本加权后打到「对方血量上限 ×1.1」的回合数之和，越小越好），
/// 最后比血差之和（我方剩血 − 对方剩血）。多对手样本时，和都按样本权重加权。
/// </summary>
public readonly record struct ArrangementScore(int MinClass, int SumClass, int SumGap, int SumKillTurn, int SumTiPo = 0) : IComparable<ArrangementScore>
{
    /// <summary>整场没打到阈值时的记分（比任何真实回合数都差）。</summary>
    public const int MissTurn = 99;

    /// <summary>
    /// 随机条件下获胜的惩罚：确定性胜利为 0；随机胜利至少为 1，且随机胜率越低惩罚越大。
    /// 这是独立的排序维度，避免随机方案靠血差把确定性方案挤下去。
    /// </summary>
    public int RandomWinPenalty { get; init; }

    public static ArrangementScore Of(IReadOnlyList<OrderResult> results, bool withTiPo = false)
    {
        int min = int.MaxValue, sum = 0, gap = 0, tipo = 0, randomPenalty = 0;
        bool anyKill = false;
        foreach (var r in results)
        {
            min = Math.Min(min, r.Class);
            sum += r.Class * r.Weight;
            gap += (r.MyHp - r.FoeHp) * r.Weight;
            if (r.KillTurn > 0) anyKill = true;
            tipo += r.TiPo * r.Weight;
            if (r.Class == OrderResult.Win && r.HadRandom)
                randomPenalty += r.Weight * (Math.Max(1, r.RandomRuns) - Math.Min(r.RandomWins, r.RandomRuns) + 1);
        }
        // 一个都没打到（请求没开 EarlyKill，或这场谁也打不到阈值）：这一维取 0 = 对所有候选都一样，
        // 排序自然退化成老排序；不这么写的话报告里会显示一串没意义的 99。
        int kill = 0;
        if (anyKill)
            foreach (var r in results) kill += (r.KillTurn > 0 ? r.KillTurn : MissTurn) * r.Weight;
        return new ArrangementScore(min == int.MaxValue ? 0 : min, sum, gap, kill, withTiPo ? tipo : 0)
        {
            RandomWinPenalty = randomPenalty,
        };
    }

    public int CompareTo(ArrangementScore o)
    {
        if (MinClass != o.MinClass) return MinClass.CompareTo(o.MinClass);
        if (SumClass != o.SumClass) return SumClass.CompareTo(o.SumClass);
        if (RandomWinPenalty != o.RandomWinPenalty) return o.RandomWinPenalty.CompareTo(RandomWinPenalty);
        // [体魄]：赢得一样多时，多攒体魄的那套排前面（不让体魄抢赢负，只做同分时的取舍）。
        // 残局求解不开 ScoreTiPo → 这一维恒为 0，排序与老版本一致。
        if (SumTiPo != o.SumTiPo) return SumTiPo.CompareTo(o.SumTiPo);
        // 前置伤害优先于终局血差（用户 2026-09-24）：打得更快的摆法排在前面，回合数越小越好。
        if (SumKillTurn != o.SumKillTurn) return o.SumKillTurn.CompareTo(SumKillTurn);
        return SumGap.CompareTo(o.SumGap);
    }

    public static bool operator >(ArrangementScore a, ArrangementScore b) => a.CompareTo(b) > 0;
    public static bool operator <(ArrangementScore a, ArrangementScore b) => a.CompareTo(b) < 0;
}

/// <summary>给一套摆法（我方牌桌卡 id）在指定先后手下算战果。实现必须线程安全。</summary>
public interface IArrangementEvaluator
{
    OrderResult Evaluate(int[] myBoard, bool meFirst);

    /// <summary>对手样本数（默认 1）。</summary>
    int FoeCount => 1;

    /// <summary>对第 <paramref name="foe"/> 个对手样本算（默认忽略序号，用于单对手的实现 / 测试）。</summary>
    OrderResult Evaluate(int[] myBoard, bool meFirst, int foe) => Evaluate(myBoard, meFirst);

    /// <summary>所有请求场景的行为签名；null 表示不支持去重。仅对有望进榜的候选调用。</summary>
    string? EquivalenceKey(int[] myBoard) => null;
}

/// <summary>用战斗计算器算战果。</summary>
public sealed class SimEvaluator : IArrangementEvaluator
{
    private readonly PuzzleRequest _req;
    private readonly IBattleConfig _cfg;

    /// <summary>卡表（摆法空间用它判消耗 / 持续与能否上场）；不是 JSON 配置时为 null。</summary>
    public JsonBattleConfig? Config => _cfg as JsonBattleConfig;
    private readonly SideInput[] _foes;
    private readonly int[] _weights;

    public SimEvaluator(PuzzleRequest req, IBattleConfig cfg)
    {
        _req = req;
        _cfg = cfg;
        var foes = req.Foes.Count > 0 ? req.Foes : new List<PuzzleSide> { req.Foe };
        _foes = new SideInput[foes.Count];
        _weights = new int[foes.Count];
        for (int i = 0; i < foes.Count; i++)
        {
            _foes[i] = foes[i].ToSideWith("R", foes[i].Cards, cfg);
            _weights[i] = Math.Max(1, foes[i].Weight);
        }
    }

    public int FoeCount => _foes.Length;

    public string EquivalenceKey(int[] myBoard)
    {
        var trace = new BattleBehaviorTrace(myBoard);
        var previous = StepLog.BehaviorSink;
        StepLog.BehaviorSink = trace.Observe;
        try
        {
            bool[] orders = _req.First == "me" ? [true] : _req.First == "foe" ? [false] : [true, false];
            for (int f = 0; f < FoeCount; f++)
                foreach (bool first in orders)
                {
                    trace.Begin(f, first);
                    // 与评分共用抽样/种子/确定性提前结束规则；每个样本的事件都单独进入签名。
                    trace.Result(EvaluateCore(myBoard, first, f, trace));
                }
            return trace.ToString();
        }
        finally { StepLog.BehaviorSink = previous; }
    }

    public OrderResult Evaluate(int[] myBoard, bool meFirst) => Evaluate(myBoard, meFirst, 0);

    /// <summary>
    /// 对一个对手样本、一种先后手算。取到了真随机参数就按 <see cref="PuzzleRequest.Samples"/> 次抽样聚合
    /// （胜负按多数、剩血取平均）；没取到的一次就够（结果是确定的）。
    /// </summary>
    public OrderResult Evaluate(int[] myBoard, bool meFirst, int foe)
        => EvaluateCore(myBoard, meFirst, foe, null);

    private OrderResult EvaluateCore(int[] myBoard, bool meFirst, int foe, BattleBehaviorTrace? trace)
    {
        int samples = Math.Max(1, _req.Samples);
        int wins = 0, my = 0, op = 0, kill = 0, runs = 0, tipo = 0;
        bool hadRandom = false;
        for (int k = 0; k < samples; k++)
        {
            trace?.Sample(k);
            Effects.ParamEstimator.BeginRun(unchecked(_req.Seed * 7919 + k * 104729 + foe * 31 + (meFirst ? 1 : 0)));
            var r = Once(myBoard, meFirst, foe);
            trace?.Result(r);
            trace?.RandomDraws(Effects.ParamEstimator.RandomDraws);
            runs++;
            if (r.Class == OrderResult.Win) wins++;
            my += r.MyHp;
            op += r.FoeHp;
            if (_req.EarlyKill) kill += r.KillTurn > 0 ? r.KillTurn : ArrangementScore.MissTurn;
            if (_req.ScoreTiPo) tipo += r.TiPo;
            if (Effects.ParamEstimator.RandomDraws > 0) hadRandom = true;
            if (Effects.ParamEstimator.RandomDraws == 0) break;    // 没用到随机参数：结果确定，不必再抽
        }
        int cls = wins * 2 > runs ? OrderResult.Win : OrderResult.Loss;
        return new OrderResult(meFirst, cls, my / runs, op / runs)
        {
            Foe = foe, Weight = _weights[foe], KillTurn = kill / runs,
            TiPo = tipo / Math.Max(1, runs),
            HadRandom = hadRandom,
            RandomWins = hadRandom ? wins : 0,
            RandomRuns = hadRandom ? runs : 0,
        };
    }

    private OrderResult Once(int[] myBoard, bool meFirst, int foe)
    {
        var input = new BattleInput
        {
            // 开战前拿不到服务器的参数队列：两边都按「当前出的牌」估参数（ParamEstimator），不用探针的占位序列。
            Left = _req.Me.ToSideWith("L", myBoard, _cfg) with { LastRoundHandCards = Remaining(myBoard), EstimateParams = true },
            Right = _foes[foe] with { EstimateParams = true },
            Round = _req.Round,
            FirstPlayerUid = meFirst ? "L" : "R",
        };
        if (!_req.EarlyKill && !_req.ScoreTiPo)
        {
            var plain = BattleResolver.Resolve(input);
            return new OrderResult(meFirst, Judge(plain.LeftHp, plain.RightHp, meFirst), plain.LeftHp, plain.RightHp);
        }
        _killTurn = 0;
        _foeMaxHp = 0;
        _tiPo = 0;
        BattleResolver.TurnSink = TurnHook;
        try
        {
            var o = BattleResolver.Resolve(input);
            return new OrderResult(meFirst, Judge(o.LeftHp, o.RightHp, meFirst), o.LeftHp, o.RightHp)
            {
                KillTurn = _killTurn,
                TiPo = _tiPo,
            };
        }
        finally
        {
            BattleResolver.TurnSink = null;
        }
    }

    // 「前置伤害 / 第几回合打死」的打点状态。线程静态：每个并行 walker 一条线程，各算各的。
    [ThreadStatic] private static int _killTurn;
    [ThreadStatic] private static int _foeMaxHp;
    [ThreadStatic] private static int _tiPo;

    /// <summary>
    /// 每回合看一眼：对方从**开局血量上限**掉下来的累计伤害到了「上限 × 1.1」就记下这是第几回合。
    /// ×1.1 = 用户定的阈值（对手血量上限多 10%，也就是确定打死）。一次到位就不再算。
    ///
    /// <para>上限取第一回合读到的 <c>MaxHp</c>（战斗开始时 Hp = MaxHp，天赋 / 仙命的上限加成都在之前结算完），
    /// 中途被减上限也不改基准 —— 否则阈值会跟着抖。</para>
    /// </summary>
    private static void TurnHook(int turn, Combatant left, Combatant right)
    {
        _tiPo = left.TiPo;      // 每回合看一眼，留最后那一次的（= 带到下一场的那个值）
        if (_killTurn != 0) return;
        if (_foeMaxHp <= 0) _foeMaxHp = right.MaxHp;
        if (_foeMaxHp <= 0) return;
        int need = (_foeMaxHp * 11 + 9) / 10;
        if (_foeMaxHp - right.Hp >= need) _killTurn = turn;
    }

    /// <summary>
    /// 没上桌的牌留在手里：开战时引擎读的「本轮手牌」（lastRoundData.handCards，部分卡按手牌数 / 手牌内容结算）
    /// = 牌池 − 这套摆法用掉的牌（空格 0 不占牌池）。
    /// </summary>
    private List<int> Remaining(int[] board)
    {
        var rest = new List<int>(_req.Me.Cards);
        foreach (int id in board)
        {
            if (id == _req.PadId) continue;
            int k = rest.IndexOf(id);
            if (k >= 0) rest.RemoveAt(k);
        }
        return rest;
    }

    /// <summary>
    /// 游戏的胜负判定（BattleExecuter 收尾，HotUpdate.cs 15888 起）：剩血多者胜（没人阵亡也一样），
    /// 同血判给先手方 —— 没有平局。
    /// </summary>
    public static int Judge(int myHp, int foeHp, bool meFirst)
    {
        if (myHp != foeHp) return myHp > foeHp ? OrderResult.Win : OrderResult.Loss;
        return meFirst ? OrderResult.Win : OrderResult.Loss;
    }
}
