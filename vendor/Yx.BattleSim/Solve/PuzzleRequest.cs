using Yx.BattleSim.Config;
using Yx.BattleSim.Model;
using Yx.BattleSim.Oracle;

namespace Yx.BattleSim.Solve;

/// <summary>
/// 一次摆牌求解的输入（残局 / 以后的 AI 打牌共用）。我方给「可用牌池」（手牌 + 牌桌），求解器从中选
/// <see cref="Slots"/> 张排好；对方给固定牌桌。双方的对局数据（天赋 / 仙命 / 刻印 / 共鸣 / 境界 / 血量 / 命元…）
/// 字段与 oracle 用例相同 —— 这些输入都在 B7 真机对拍里验证过。
/// </summary>
public sealed class PuzzleRequest
{
    public int Round { get; set; } = 1;

    /// <summary>我方牌桌格数。</summary>
    public int Slots { get; set; } = 8;

    /// <summary>先手：<c>"me"</c> / <c>"foe"</c> / <c>"both"</c>（两种都算，默认）。</summary>
    public string First { get; set; } = "both";

    public PuzzleSide Me { get; set; } = new();

    /// <summary>对手（残局：已知的那一个）。<see cref="Foes"/> 非空时忽略它。</summary>
    public PuzzleSide Foe { get; set; } = new();

    /// <summary>
    /// 多个对手样本（PvP：本轮看不到对手的牌，用对手上一轮快照 + 历史几轮的盘面），各带 <see cref="PuzzleSide.Weight"/>。
    /// 评分：最差样本的胜负等级 → 加权胜场 → 加权血差。空 = 只用 <see cref="Foe"/>。
    /// </summary>
    public List<PuzzleSide> Foes { get; set; } = new();

    /// <summary>
    /// 真随机参数的抽样次数：一场战斗若取到了真随机参数（估值器抽样），同一摆法 × 对手 × 先后手跑这么多次，
    /// 胜负按多数、剩血取平均；没取到随机参数的只跑一次。
    /// </summary>
    public int Samples { get; set; } = 3;

    /// <summary>
    /// 消耗牌 + 持续牌合计上限；&lt;0 = 不限。mod 在游戏里直接调 <c>CardPanel.Get_MAX_CONSUMED_CONTINUOUS_CARD_COUNT()</c> 填进来
    /// （2 + 刻印类型 64 张数 + 永久 buff），求解器不自己推这个公式。
    /// </summary>
    public int MaxConsumeSustain { get; set; } = -1;

    /// <summary>牌池不够填满格数时补的牌（默认 0 = 普通攻击）。</summary>
    public int PadId { get; set; }

    /// <summary>
    /// 评分是否带上「前置伤害」这一维（第几回合打到对方血量上限 ×1.1，小者优，排在终局血差之前）。
    /// 默认 **关** —— 残局求解按老排序（能赢 + 血差最大）走，只有 AI 摆牌打开它（用户 2026-09-24）。
    /// 关掉时每回合打点整个不装，连那点回调开销也省了。
    /// </summary>
    public bool EarlyKill { get; set; }

    /// <summary>
    /// 把「战斗结束时我方的 [体魄]」算进摆法评分（用户 2026-09-24：
    /// 体魄不只是战斗属性，还是突破的另一条路：游戏里 <c>TiPo &gt;= needExp</c> 也能点亮突破按钮，
    /// 而体魄是从上一场带下来的）。**默认关**：残局求解不开这个，排序与老版本逐字一致。
    /// </summary>
    public bool ScoreTiPo { get; set; }

    /// <summary>返回前几名。</summary>
    public int TopN { get; set; } = 5;

    /// <summary>推荐结果合并当前全部先后手 / 对手 / 抽样中战斗行为相同的未用牌变体。</summary>
    public bool DeduplicateResults { get; set; }

    /// <summary>总时间上限（毫秒）。到点返回当前最好结果。</summary>
    public int TimeLimitMs { get; set; } = 8000;

    /// <summary>候选摆法 × 先后手 的场数不超过它时穷举（在局部搜索之后补齐）；0 = 不穷举。</summary>
    public int ExhaustiveLimit { get; set; } = 200_000;

    /// <summary>
    /// 收敛即停：局部搜索连续这么久（毫秒）第一名没变好就结束（大牌池没法穷举，靠它早收工）；&lt;=0 = 一直跑到时限。
    /// 实测 15 张牌选 8（2.6 亿种摆法）：2 秒内多数种子已到最优，8~30 秒不再变好。
    /// </summary>
    public int StallMs { get; set; } = 1500;

    /// <summary>并行线程数；&lt;=0 = 逻辑核数 − 2（至少 1），给游戏留余量。</summary>
    public int Threads { get; set; }

    /// <summary>局部搜索的随机种子（同输入同种子 → 同结果）。</summary>
    public int Seed { get; set; } = 1;
}

/// <summary>一方：对局数据（继承 oracle 的字段）+ 牌。我方 <see cref="Cards"/> 是牌池，对方是按格顺序的牌桌。</summary>
public sealed class PuzzleSide : OracleSide
{
    public List<int> Cards { get; set; } = new();

    /// <summary>作为对手样本时的权重（整数，默认 1）。</summary>
    public int Weight { get; set; } = 1;

    /// <summary>我方当前牌桌（可选）：作为局部搜索的起点之一。</summary>
    public List<int> Initial { get; set; } = new();

    /// <summary>按 id 从卡表造牌（数值与游戏同源），组成这一方的战斗输入。</summary>
    public SideInput ToSideWith(string uid, IReadOnlyList<int> board, IBattleConfig cfg)
    {
        var cards = new BattleCard[board.Count];
        for (int i = 0; i < board.Count; i++) cards[i] = cfg.BuildCard(board[i]);
        return ToSide(uid) with { Board = cards };
    }
}
