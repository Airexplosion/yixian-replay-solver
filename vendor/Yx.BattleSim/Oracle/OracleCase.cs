using System.Text.Json;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Oracle;

/// <summary>
/// 一条对拍用例：一场战斗的输入 + 游戏真引擎跑出的真结果（golden）。
/// 金标由游戏内采集（arenaprobe/agent 驱动 BattleExecuter.Execute 或观测真实对局）产出为 JSON，
/// sim 用同一输入复算、与 <see cref="Real"/> 逐场 diff。见 oracle-validation 设计。
/// </summary>
public sealed class OracleCase
{
    public string Name { get; set; } = "";
    public OracleSide Left { get; set; } = new();
    public OracleSide Right { get; set; } = new();
    public int Round { get; set; } = 1;
    public bool Fast { get; set; }

    /// <summary>先手方：<c>"L"</c> / <c>"R"</c>；空 = 左先手（探针采集的对局都是左先手；真实对局由服务器定）。</summary>
    public string? First { get; set; }

    public OracleReal Real { get; set; } = new();

    public BattleInput ToInput() => new()
    {
        Left = Left.ToSide("L"),
        Right = Right.ToSide("R"),
        Round = Round,
        FastMode = Fast,
        FirstPlayerUid = First is "R" ? "R" : null,
    };
}

public class OracleSide
{
    public int UnlockGrids { get; set; } = 8;
    public int Hp { get; set; }
    public int Def { get; set; }
    /// <summary>
    /// 命元（lastRoundData.life）。探针 MakePlayer 固定写 10，但 B6 起的采集 JSON 没带这个字段 ——
    /// 缺省必须是 10 而不是 0：命元是「命元 ≤0 即死」、命元伤害、共鸣 121 等的输入（99000105 裂虚界元枪
    /// 在 life=0 下一出手就把对手清零，yisim 对拍 / B5 真机都是 37 而 sim 给 91）。
    /// </summary>
    public int Life { get; set; } = 10;
    public List<int> Fate { get; set; } = new();
    public List<int> Keyin { get; set; } = new();
    public List<OracleCard> Board { get; set; } = new();

    /// <summary>
    /// 天赋列表。**必须能表达**：星位/后招/相生之火(137)/刻印系天赋等一大批机制全靠天赋门，
    /// 早先这里硬编 <c>[]</c>，导致这些机制在 oracle 里<b>永远无法被触发</b>（不是移植问题，是采集格式缺字段）。
    /// </summary>
    public List<int> Talents { get; set; } = new();

    /// <summary>选中的共鸣（单个，对应游戏 TalentResonancePanel 的单选）。0 = 无。</summary>
    public int Resonance { get; set; }

    /// <summary>境界等级（共鸣 effectLevel 门槛 + 部分卡按 level 判）。0 = 未采集。</summary>
    public int Level { get; set; }

    /// <summary>玩家选牌参数（battleParamsQueue）：GetNextRandomValue / NextParam 的真实输入。</summary>
    public List<int> Params { get; set; } = new();

    // ---- 对局数据（见 SideInput 同名字段）；采集 JSON 不带时取探针的实际值 / 中性 ----
    /// <summary>角色 id。探针 MakePlayer 固定写 1000001。</summary>
    public int CharacterId { get; set; } = 1000001;
    public int Exp { get; set; }
    public List<int> HandCards { get; set; } = new();
    public Dictionary<int, int> PermanentBuffs { get; set; } = new();
    public Dictionary<int, int> TalentTempDatas { get; set; } = new();
    public Dictionary<int, int> ResonanceFlags { get; set; } = new();
    /// <summary>天赋自带数据：189 = 灵悟牌。</summary>
    public Dictionary<int, List<int>> TalentDatas { get; set; } = new();
    public Dictionary<int, List<int>> PrivateTalentDatas { get; set; } = new();
    public int MaxKeYin { get; set; }
    public int Sect { get; set; }
    public int Career { get; set; }

    public SideInput ToSide(string uid) => new()
    {
        Uid = uid,
        UnlockGrids = UnlockGrids,
        StartHp = Hp,
        StartDef = Def,
        Life = Life,
        FateStrategies = Fate,
        Talents = Talents,
        KeYinCards = Keyin,
        Resonance = Resonance,
        Level = Level,
        // 采集没带 params 时回落到探针的默认序列（见 OracleDefaults）——**不能按空处理**。
        BattleParams = Params.Count > 0 ? Params : OracleDefaults.Params(),
        Board = Board.ConvertAll(c => c.ToCard()),
        CharacterId = CharacterId,
        LastRoundExp = Exp,
        LastRoundHandCards = HandCards,
        PermanentBuffs = PermanentBuffs,
        TalentTempDatas = TalentTempDatas,
        ResonancePermanentFlags = ResonanceFlags,
        TalentDatas = ToRo(TalentDatas),
        PrivateTalentDatas = ToRo(PrivateTalentDatas),
        MaxKeYin = MaxKeYin,
        Sect = Sect,
        Career = Career,
    };

    private static Dictionary<int, IReadOnlyList<int>> ToRo(Dictionary<int, List<int>> m)
    {
        var d = new Dictionary<int, IReadOnlyList<int>>();
        foreach (var kv in m) d[kv.Key] = kv.Value;
        return d;
    }
}

public sealed class OracleCard
{
    public int Id { get; set; }
    public int A { get; set; }        // attack
    public int D { get; set; }        // def
    public int C { get; set; } = 1;   // attackCount
    public bool Ig { get; set; }      // ignoreDefense

    /// <summary>灵气（cardConfig.anima）。灵气牌走「加灵气」分支，缺它会让这类卡静默变成 0。</summary>
    public int An { get; set; }

    /// <summary>体魄（cardConfig.physique）。</summary>
    public int P { get; set; }

    /// <summary>牌名（可选）：相邻/相生/后招判定按名做，非配置来源的盘面靠它表达。</summary>
    public string? Nm { get; set; }

    public BattleCard ToCard() => new()
    {
        Id = Id, Attack = A, Def = D, AttackCount = C, IgnoreDefense = Ig,
        Anima = An, Physique = P, Name = Nm,
    };
}

/// <summary>游戏真引擎的结束态。</summary>
public sealed class OracleReal
{
    public int LeftHp { get; set; }
    public int RightHp { get; set; }
    public string? Winner { get; set; }   // "L" / "R" / null
    public int Life { get; set; }
}

/// <summary>
/// 采集侧**没有写进 JSON** 但游戏侧确实喂了的默认值。
///
/// `battleParams` 是这里唯一的成员：arenaprobe 的每个 BuildResult* 都用同一个
/// xorshift(seed=20260919) 生成 4096 个 0..99 的参数（见探针 main`BuildResult`/`BuildResult2`）。
/// 它们是 `GetNextRandomValue()` / `GetNextParam()` 的真实输入 ——
/// sim 若把空 params 当成「全是 0」，读这两个接口的卡（paramInput 那 23 张、
/// 后招的天赋108、共鸣71…）会**全部算错**，而且错得很隐蔽（0 是个合法值）。
/// 所以这里按同样的算法重放一遍，保证 sim 与游戏吃到同一串数。
/// </summary>
public static class OracleDefaults
{
    private const uint Seed = 20260919u;
    private const int Count = 4096;

    private static List<int>? _params;

    /// <summary>探针喂给游戏的 battleParams 序列（与 BuildResult* 的生成逐位一致）。</summary>
    public static List<int> Params() => _params ??= Generate();

    private static List<int> Generate()
    {
        var list = new List<int>(Count);
        uint state = Seed;
        for (int i = 0; i < Count; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            list.Add((int)(state % 100u));
        }
        return list;
    }
}

public static class OracleCaseIo
{
    public static List<OracleCase> Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var arr = doc.RootElement.TryGetProperty("cases", out var c) ? c : doc.RootElement;
        var list = new List<OracleCase>();
        foreach (var e in arr.EnumerateArray())
            list.Add(JsonSerializer.Deserialize<OracleCase>(e.GetRawText(), opts)!);
        return list;
    }
}
