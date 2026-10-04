namespace Yx.BattleSim.Model;

/// <summary>一方的战斗输入：起始血、防、仙命/天赋、以及按格顺序的摆牌。不可变。</summary>
public sealed record SideInput
{
    public required string Uid { get; init; }
    public required int StartHp { get; init; }
    public int StartDef { get; init; }
    public int Life { get; init; }
    public IReadOnlyList<int> FateStrategies { get; init; } = [];
    public IReadOnlyList<int> Talents { get; init; } = [];
    public IReadOnlyList<int> KeYinCards { get; init; } = [];

    /// <summary>选中的天赋共鸣 id（0 = 未选）。游戏里是单个选中值，不是列表。</summary>
    public int Resonance { get; init; }

    /// <summary>境界（Level 枚举值；0 = InvalidLevel）。共鸣的 effectLevel 门槛用。</summary>
    public int Level { get; init; }

    /// <summary>
    /// **已解锁的格数**（游戏 <c>lastRoundData.unlockGrids</c>）。**开局前几回合是逐格解锁的**，
    /// 所以牌组不一定是 8 格 —— 游戏的牌组长度是 <c>min(unlockGrids, usedCards.Count)</c>（反编译 275775），
    /// 环形回绕也按它来（`GetNextGrid`：`num = unlockGrids; if (num &lt;= 0) num = 8;`）。
    /// 缺省 8 = 全部解锁（oracle 探针喂的正是 8，见 arenaprobe 的 FillBoard）。
    /// </summary>
    public int UnlockGrids { get; init; } = 8;

    /// <summary>
    /// 玩家选牌参数（对应游戏 battleExecuter.battleParamsQueue / battleResult.battleParams）。
    /// 少数卡的效果值来自出牌时玩家选的参数（反编译里写作 `(BuffType)nextParam`），按出牌顺序消耗。
    /// 空表时按 0 兜底（这类分支不改变结构，只是取值待 AI 摆牌层接入后填）。
    /// </summary>
    public IReadOnlyList<int> BattleParams { get; init; } = [];

    public required IReadOnlyList<BattleCard> Board { get; init; }

    // ---- 游戏对局数据（BattlePlayerData / lastRoundData / privateData）：少数机制的输入，缺省即中性 ----

    /// <summary>角色 id（<c>publicData.characterId</c>）。</summary>
    public int CharacterId { get; init; }

    /// <summary>上一回合修为（<c>lastRoundData.exp</c>；开局 <c>characterUI.exp</c>）。</summary>
    public int LastRoundExp { get; init; }

    /// <summary>玩家宗门 / 职业（<c>publicData.sect / career</c>，0 = 无）。</summary>
    public int Sect { get; init; }
    public int Career { get; init; }

    /// <summary>上一回合手牌（<c>lastRoundData.handCards</c>）。已用牌就是 <see cref="Board"/>。</summary>
    public IReadOnlyList<int> LastRoundHandCards { get; init; } = [];

    /// <summary>
    /// 摆牌求解：开战前没有服务器的 battleParams，取参数时按当前出的牌估值（见 ParamEstimator），不读 <see cref="BattleParams"/>。
    /// </summary>
    public bool EstimateParams { get; init; }

    /// <summary>上一回合的永久 buff（<c>lastRoundData.permanentBuffTempDatas</c>：BuffType 数值 → 值）。</summary>
    public IReadOnlyDictionary<int, int> PermanentBuffs { get; init; } = new Dictionary<int, int>();

    /// <summary>上一回合的天赋临时数据（<c>lastRoundData.talentTempDatas</c>）。</summary>
    public IReadOnlyDictionary<int, int> TalentTempDatas { get; init; } = new Dictionary<int, int>();

    /// <summary>共鸣永久标记位（<c>publicData.resonanceTalentFlags</c>）。</summary>
    public IReadOnlyDictionary<int, int> ResonancePermanentFlags { get; init; } = new Dictionary<int, int>();

    /// <summary>天赋自带数据（<c>publicData.talentDatas</c>）：189 = 灵悟牌。</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>> TalentDatas { get; init; } = new Dictionary<int, IReadOnlyList<int>>();

    /// <summary>刻印点数上限（<c>privateData.keYinData.maxKeYin</c>）：论道 14 按剩余点数加血。</summary>
    public int MaxKeYin { get; init; }

    /// <summary>私有天赋自带数据（<c>privateData.talentDatas</c>）：199 = 五行玉屏。</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>> PrivateTalentDatas { get; init; } = new Dictionary<int, IReadOnlyList<int>>();
}

/// <summary>一场战斗的完整确定性输入。同一输入必得同一 <see cref="BattleOutcome"/>。</summary>
public sealed record BattleInput
{
    public required SideInput Left { get; init; }
    public required SideInput Right { get; init; }

    /// <summary>回合数（进入 CalLifeDamage 的生命伤害公式）。</summary>
    public int Round { get; init; } = 1;

    /// <summary>是否 FastMode（生命伤害 ×1.5）。</summary>
    public bool FastMode { get; init; }

    /// <summary>
    /// 先手方的 Uid（对应 battleResult.firstPlayerId）。留空 = Left 先手。
    /// 结算器据此决定谁先出牌（原码在 Execute 开头按它选 firstCharacter）。
    /// </summary>
    public string? FirstPlayerUid { get; init; }
}
