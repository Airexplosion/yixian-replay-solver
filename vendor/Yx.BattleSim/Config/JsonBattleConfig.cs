using Yx.BattleSim.Model;
using System.Text.Json;

namespace Yx.BattleSim.Config;

/// <summary>
/// 从抽出的 battle_config.local.json（tools/refs/card-pipeline/extract_config.py 产出）加载真实配置数值，
/// 回填 <see cref="IBattleConfig"/>，让整条已移植管线用游戏真数（而非 stub 的 0）。
/// 另暴露卡牌元数据（攻/防/段数/灵气/体魄/稀有度/名）供 P5 卡效果层与牌型谓词用。
///
/// 数据是厂商版权：json 只存本机（sim/ 已 gitignore），随构建拷到输出目录。
/// 文件缺失时 <see cref="TryLoadDefault"/> 返回 null，调用方回退到空配置。
/// </summary>
public sealed class JsonBattleConfig : IBattleConfig
{
    /// <summary>
    /// 一张卡的静态元数据。Desc 不是展示用：后招/唯一等标记只在描述里（见 <see cref="IBattleConfig.CardDesc"/>）。
    /// <para>
    /// <see cref="ActionAgain"/> 是**无条件**再行动（108 张牌）：卡效果还能在运行期把它打开（条件式再行动，
    /// 如 Card_295 相生成立才再行动），那种不在这里。两者是「或」的关系。
    /// <see cref="NoUpgrade"/> 是 <c>CanUpgrade()</c> 的唯一输入（= !NoUpgrade）。
    /// </para>
    /// </summary>
    public sealed record CardMeta(
        string Name, string Desc, int Attack, int Def, int AttackCount, int Anima, int Physique, int Rarity,
        int[] OtherParams, int JianYi, int GuaXiang, bool ActionAgain, int CardType, int HpCost, bool NoUpgrade,
        int Subcategory,
        /// <summary>境界（Level 枚举 1..5）：卡体里 100 处 `cardConfig.level &gt;= Level.YuanYing` 门槛用。</summary>
        int Level,
        /// <summary>随机攻击上限（仅 29 张卡有）。</summary>
        int RandomAttack,
        int RandomDef,
        /// <summary>[蓄灵]：**灵气不足以出这张牌时**每空转一回合额外补的灵气（CardConfig.chargeQi，仅 7 张有）。</summary>
        int ChargeQi,
        /// <summary>职业（Career 枚举：3 = 琴师、5 = 阵法师 …；0 = 无）。琴师牌一族「后一格为琴师牌」按它判。</summary>
        int Career = 0,
        /// <summary>隐藏牌（CardConfig.hidden）。</summary>
        bool Hidden = false,
        /// <summary>宗门（Sect 枚举，0 = 无）。</summary>
        int Sect = 0);

    private readonly Dictionary<int, CardMeta> _card;
    private readonly Dictionary<int, int[]> _keyin;
    /// <summary>刻印牌描述（keyin_meta.ds）：判「[被动生效]」用（KeYinCardItem.IsPassive）。</summary>
    private Dictionary<int, string> _keyinDesc = new();
    /// <summary>刻印牌 maxHp（keyin_meta.hp）。</summary>
    private Dictionary<int, int> _keyinHp = new();
    /// <summary>刻印牌的刻印点数（keyin_meta.ky）。</summary>
    private Dictionary<int, int> _keyinPts = new();
    private readonly Dictionary<int, int[]> _fate;
    private readonly Dictionary<int, int[]> _talent;
    private readonly Dictionary<int, int[]> _resonance;
    private readonly Dictionary<int, int> _level;
    private readonly Dictionary<int, BuffCategory> _buffCat;
    private readonly Dictionary<int, int[]> _resGate;

    public string Version { get; }

    private JsonBattleConfig(string version, Dictionary<int, CardMeta> card, Dictionary<int, int[]> keyin,
        Dictionary<int, int[]> fate, Dictionary<int, int[]> talent, Dictionary<int, int[]> resonance,
        Dictionary<int, int> level, Dictionary<int, BuffCategory> buffCat, Dictionary<int, int[]> resGate)
    {
        Version = version; _card = card; _keyin = keyin; _fate = fate; _talent = talent;
        _resonance = resonance; _level = level; _buffCat = buffCat; _resGate = resGate;
    }

    private static readonly int[] Empty = [];

    public IReadOnlyList<int> CardOtherParams(int cardId) => _card.TryGetValue(cardId, out var m) ? m.OtherParams : Empty;
    public IReadOnlyList<int> KeYinOtherParams(int keYinCardId) => _keyin.TryGetValue(keYinCardId, out var v) ? v : Empty;
    public string KeYinDesc(int keYinCardId) => _keyinDesc.TryGetValue(keYinCardId, out var v) ? v : "";
    public int KeYinMaxHp(int keYinCardId) => _keyinHp.TryGetValue(keYinCardId, out var v) ? v : 0;
    public int KeYinPoints(int keYinCardId) => _keyinPts.TryGetValue(keYinCardId, out var v) ? v : 0;
    public IReadOnlyList<int> FateOtherParams(int fateId) => _fate.TryGetValue(fateId, out var v) ? v : Empty;
    public IReadOnlyList<int> TalentOtherParams(int talentId) => _talent.TryGetValue(talentId, out var v) ? v : Empty;
    public IReadOnlyList<int> ResonanceOtherParams(int resonanceTalentId) => _resonance.TryGetValue(resonanceTalentId, out var v) ? v : Empty;
    public string CardName(int cardId) => _card.TryGetValue(cardId, out var m) ? m.Name : "";
    public string CardDesc(int cardId) => _card.TryGetValue(cardId, out var m) ? m.Desc : "";

    public BuffCategory BuffCategoryOf(BuffType buff)
        => _buffCat.TryGetValue((int)buff, out var v) ? v : BuffCategory.Positive;

    public (int EffectRound, int EffectLevel) ResonanceGate(int resonanceTalentId)
        => _resGate.TryGetValue(resonanceTalentId, out var v) ? (v[0], v[1]) : (0, 0);

    /// <summary>卡牌元数据（找不到返回 null）。</summary>
    public CardMeta? Card(int cardId) => _card.TryGetValue(cardId, out var m) ? m : null;

    /// <summary>全卡表（覆盖率统计 / 全量冒烟用）。</summary>
    public IReadOnlyDictionary<int, CardMeta> AllCards => _card;

    /// <summary>等级基础血（GetStartMaxHp 用）。</summary>
    public int LevelBaseMaxHp(int level) => _level.TryGetValue(level, out var v) ? v : 0;

    /// <summary>
    /// 本张牌**无条件**再行动（CardConfig.actionAgain，108 张牌）。
    /// 对应原码行动权判定里的 <c>cardConfig.actionAgain</c>；条件式再行动由卡效果运行期另写。
    /// </summary>
    public bool CardActionAgain(int cardId) => _card.TryGetValue(cardId, out var m) && m.ActionAgain;

    /// <summary>CardConfig.CanUpgrade()：可被「画龙点睛」之类的升级效果提升 —— 唯一输入是 noUpgrade。</summary>
    public bool CanUpgrade(int cardId) => _card.TryGetValue(cardId, out var m) && !m.NoUpgrade;

    /// <summary>以血代灵气的代价（hpCost）；0 = 无。</summary>
    public int CardHpCost(int cardId) => _card.TryGetValue(cardId, out var m) ? m.HpCost : 0;

    /// <summary>CardType.Sustain（持续牌）在 CardType 枚举里的值。</summary>
    private const int CardTypeSustain = 3;

    public bool CardIsSustain(int cardId) => _card.TryGetValue(cardId, out var m) && m.CardType == CardTypeSustain;

    /// <summary>CardType.Consume（消耗牌）在 CardType 枚举里的值。</summary>
    private const int CardTypeConsume = 1;

    public bool CardIsConsume(int cardId) => _card.TryGetValue(cardId, out var m) && m.CardType == CardTypeConsume;

    /// <summary>CardConfig.chargeQi（[蓄灵]）：灵气不足以出这张牌时，每空转一回合额外补这么多灵气。</summary>
    public int CardChargeQi(int cardId) => _card.TryGetValue(cardId, out var m) ? m.ChargeQi : 0;
    public int CardCareer(int cardId) => _card.TryGetValue(cardId, out var m) ? m.Career : 0;
    public int CardSect(int cardId) => _card.TryGetValue(cardId, out var m) ? m.Sect : 0;

    /// <summary>CardConfig.level（境界，Level 枚举 1..5；0 = InvalidLevel）。</summary>
    public int CardLevel(int cardId) => _card.TryGetValue(cardId, out var m) ? m.Level : 0;

    /// <summary>卡表里所有的卡 id。</summary>
    public IEnumerable<int> CardIds() => _card.Keys;

    /// <summary>按 id 从卡表造一张 BattleCard（运行期换牌用；找不到返回只有 id 的空牌）。</summary>
    public BattleCard BuildCard(int cardId)
    {
        if (!_card.TryGetValue(cardId, out var m)) return new BattleCard { Id = cardId };
        return new BattleCard
        {
            Id = cardId, Attack = m.Attack, Def = m.Def, AttackCount = m.AttackCount,
            Anima = m.Anima, Physique = m.Physique, OtherParams = m.OtherParams,
            RandomAttack = m.RandomAttack, RandomDef = m.RandomDef,
            ActionAgain = m.ActionAgain, Name = m.Name,
        };
    }

    /// <summary>CardConfig.rarity（1..3；0 = 无稀有度，如普攻）。降级类机制的判据。</summary>
    public int CardRarity(int cardId) => _card.TryGetValue(cardId, out var m) ? m.Rarity : 0;

    public int CardJianYi(int cardId) => _card.TryGetValue(cardId, out var m) ? m.JianYi : 0;
    public int CardGuaXiang(int cardId) => _card.TryGetValue(cardId, out var m) ? m.GuaXiang : 0;
    public int CardRandomAttack(int cardId) => _card.TryGetValue(cardId, out var m) ? m.RandomAttack : 0;
    public int CardRandomDef(int cardId) => _card.TryGetValue(cardId, out var m) ? m.RandomDef : 0;

    // ---- 加载 ----

    private static JsonBattleConfig? _default;
    private static bool _defaultTried;

    /// <summary>默认实例：从数据目录（<see cref="SimData"/>）的 battle_config(.local).json 加载；缺失/失败返回 null（只试一次）。</summary>
    public static JsonBattleConfig? TryLoadDefault()
    {
        if (_defaultTried) return _default;
        _defaultTried = true;
        try
        {
            string? path = SimData.Find("battle_config");
            if (path is not null) _default = Load(path);
        }
        catch { _default = null; }
        return _default;
    }

    /// <summary>按当前数据目录重新加载默认实例（<see cref="SimData.Initialize"/> 用）。成功返回 true。</summary>
    public static bool ReloadDefault()
    {
        _defaultTried = false;
        _default = null;
        return TryLoadDefault() is not null;
    }

    public static JsonBattleConfig Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        string version = root.TryGetProperty("version", out var ver) ? ver.GetString() ?? "" : "";

        var card = new Dictionary<int, CardMeta>();
        foreach (var kv in root.GetProperty("card").EnumerateObject())
        {
            var o = kv.Value;
            card[int.Parse(kv.Name)] = new CardMeta(
                o.TryGetProperty("n", out var n) ? n.GetString() ?? "" : "",
                o.TryGetProperty("ds", out var ds) ? ds.GetString() ?? "" : "",
                GetInt(o, "a"), GetInt(o, "d"), GetInt(o, "c"), GetInt(o, "an"), GetInt(o, "p"), GetInt(o, "r"),
                GetIntArray(o, "o"),
                GetInt(o, "jy"), GetInt(o, "gx"), GetInt(o, "ag") != 0, GetInt(o, "ct"),
                GetInt(o, "hc"), GetInt(o, "nu") != 0, GetInt(o, "sc"),
                GetInt(o, "lv"), GetInt(o, "ra"), GetInt(o, "rd"), GetInt(o, "cq"), GetInt(o, "cr"),
                GetInt(o, "hd") != 0, GetInt(o, "se"));
        }

        var cfg = new JsonBattleConfig(version, card,
            LoadArrays(root, "keyin"), LoadArrays(root, "fate"),
            LoadArrays(root, "talent"), LoadArrays(root, "resonance"),
            LoadInts(root, "level"), LoadBuffCats(root), LoadArrays(root, "resonance_gate"));
        if (root.TryGetProperty("keyin_meta", out var km))
            foreach (var kv in km.EnumerateObject())
            {
                if (!int.TryParse(kv.Name, out int kid)) continue;
                if (kv.Value.TryGetProperty("ds", out var ds)) cfg._keyinDesc[kid] = ds.GetString() ?? "";
                if (kv.Value.TryGetProperty("hp", out var hp) && hp.TryGetInt32(out int hv) && hv != 0) cfg._keyinHp[kid] = hv;
                if (kv.Value.TryGetProperty("ky", out var ky) && ky.TryGetInt32(out int kyv) && kyv != 0) cfg._keyinPts[kid] = kyv;
            }
        return cfg;
    }

    private static Dictionary<int, BuffCategory> LoadBuffCats(JsonElement root)
    {
        var d = new Dictionary<int, BuffCategory>();
        if (!root.TryGetProperty("buffcat", out var obj)) return d;
        foreach (var kv in obj.EnumerateObject()) d[int.Parse(kv.Name)] = (BuffCategory)kv.Value.GetInt32();
        return d;
    }

    private static int GetInt(JsonElement o, string prop) => o.TryGetProperty(prop, out var v) ? v.GetInt32() : 0;
    private static int[] GetIntArray(JsonElement o, string prop)
    {
        if (!o.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return Empty;
        var list = new int[arr.GetArrayLength()];
        int i = 0;
        foreach (var e in arr.EnumerateArray()) list[i++] = e.GetInt32();
        return list;
    }
    private static Dictionary<int, int[]> LoadArrays(JsonElement root, string name)
    {
        var d = new Dictionary<int, int[]>();
        if (!root.TryGetProperty(name, out var obj)) return d;
        foreach (var kv in obj.EnumerateObject())
        {
            var arr = kv.Value;
            var list = new int[arr.GetArrayLength()];
            int i = 0;
            foreach (var e in arr.EnumerateArray()) list[i++] = e.GetInt32();
            d[int.Parse(kv.Name)] = list;
        }
        return d;
    }
    private static Dictionary<int, int> LoadInts(JsonElement root, string name)
    {
        var d = new Dictionary<int, int>();
        if (!root.TryGetProperty(name, out var obj)) return d;
        foreach (var kv in obj.EnumerateObject()) d[int.Parse(kv.Name)] = kv.Value.GetInt32();
        return d;
    }
}
