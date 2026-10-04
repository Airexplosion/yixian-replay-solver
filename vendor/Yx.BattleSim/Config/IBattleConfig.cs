using Yx.BattleSim.Model;

namespace Yx.BattleSim.Config;

/// <summary>
/// 战斗逻辑需要的**配置数值**（游戏里散落在 ConfigManager / CardFactory / KeYinCardFactory /
/// TalentResonancePanel 的 otherParams）。忠实移植的分支用它取 magic number，
/// 而不是把厂商数值硬编进逻辑。生产用一个从游戏抽出的本地数据文件回填（gitignore）；
/// 测试用 <see cref="StubBattleConfig"/> 只喂用到的少数 id。
/// 取不到的 id 返回空表；调用方按游戏语义自行兜底（多数分支在 HasBuff 为假时根本不会走到取值）。
/// </summary>
public interface IBattleConfig
{
    IReadOnlyList<int> CardOtherParams(int cardId);
    IReadOnlyList<int> TalentOtherParams(int talentId);
    IReadOnlyList<int> FateOtherParams(int fateId);
    IReadOnlyList<int> ResonanceOtherParams(int resonanceTalentId);
    IReadOnlyList<int> KeYinOtherParams(int keYinCardId);
    /// <summary>刻印牌描述（判「[被动生效]」）。默认空串 = 非被动。</summary>
    string KeYinDesc(int keYinCardId) => "";

    /// <summary>刻印牌的 maxHp（keyin_meta.hp）：开局「生命修正」加总用（OnBattleStartKeYin，原码 194697）。</summary>
    int KeYinMaxHp(int keYinCardId) => 0;

    /// <summary>刻印牌的刻印点数（keyin_meta.ky，KeYinCardConfig.keYin）：论道 14 算剩余刻印点数用。</summary>
    int KeYinPoints(int keYinCardId) => 0;

    /// <summary>卡名（牌型谓词按名判定用；缺失返回空串）。</summary>
    string CardName(int cardId);

    /// <summary>
    /// 卡描述（缺失返回空串）。**不是**展示用：游戏里若干机制用描述里的方括号标记判定，
    /// 例如 <c>IsHouZhao</c> ← <c>desc.Contains("[后招]：")</c>、<c>IsUnique</c> ← <c>"[唯一]"</c>。
    /// 这些标记不存在于任何结构化字段，desc 是唯一来源。
    /// </summary>
    string CardDesc(int cardId);

    /// <summary>
    /// 本张牌**无条件**再行动（CardConfig.actionAgain，全卡池 108 张）。
    /// 条件式再行动（如 Card_295 相生成立才再行动）由卡效果在运行期另写，不在此列——两者是「或」。
    /// </summary>
    bool CardActionAgain(int cardId);

    /// <summary>CardConfig.CanUpgrade()：这张牌能否被「画龙点睛」系效果升级（唯一输入是 noUpgrade）。</summary>
    bool CanUpgrade(int cardId);

    /// <summary>
    /// CardConfig.cardType == CardType.Sustain（持续牌）。出牌收尾要 +1 <c>YongGuoChiXuPai</c>
    /// （原码 24050；全卡池 442 张）。
    /// </summary>
    bool CardIsSustain(int cardId);

    /// <summary>
    /// CardConfig.cardType == CardType.Consume（消耗牌）。
    /// 与 <see cref="CardIsSustain"/> 一起用于**必须带卡型门槛**的规则 —— 典型是共鸣 126
    /// （消耗/持续牌加上限并回血）：漏了门槛就变成**每张牌都触发**，一局白送几百血。
    /// </summary>
    bool CardIsConsume(int cardId);

    /// <summary>
    /// CardConfig.chargeQi（[蓄灵]，仅 7 张）：灵气不足以出这张牌时，**每空转一回合额外补这么多灵气**。
    /// 对应原码 AnimaShortage 分支：<c>else { ModifyAnima(1); }</c> 之后
    /// <c>if (currentCard.cardConfig.chargeQi &gt; 0)  ModifyAnima(chargeQi);</c>（15806）。
    /// 漏了它，靠蓄灵才能出的仙器（如卡 181 幽冥化生壶）会被当成永远出不来。
    /// </summary>
    int CardChargeQi(int cardId);

    /// <summary>CardConfig.career（Career 枚举：3 = 琴师 …；0 = 无）。缺数据返回 0。</summary>
    int CardCareer(int cardId);

    /// <summary>卡牌宗门（CardConfig.sect，0 = 无）。</summary>
    int CardSect(int cardId) => 0;

    /// <summary>CardConfig.level（境界，Level 枚举 1..5；0 = InvalidLevel）。开局效果 case 1000086 按它判。</summary>
    int CardLevel(int cardId);

    /// <summary>卡表里所有的卡 id（只读遍历用；牌池估值要按门派/职业/境界筛）。</summary>
    IEnumerable<int> CardIds();

    /// <summary>
    /// CardConfig.hpCost（**以血代灵气的代价**，全池 172 张含变体）：出牌时先付这个血。
    /// 原码 <c>CheckCardCost</c>（24151）在**灵气判定通过之后**扣：
    /// <c>if (num == Succeed &amp;&amp; num2 &gt; 0) src.ModifyHp(-num2, 0f, canRevive: false, 0, isCost: true);</c>
    /// </summary>
    int CardHpCost(int cardId);

    /// <summary>
    /// 按 id 造一张 <see cref="BattleCard"/>（数值全部取自配置表）。
    /// **运行期换牌要用**：升级 / 降级 / 复制 / 变形都会把某一格的牌换成另一张，
    /// 新牌的攻/防/段数/灵气/体魄/otherParams 必须跟着变（实测卡 206 的 otherParams 随稀有度 4→7→10）。
    /// </summary>
    BattleCard BuildCard(int cardId);

    /// <summary>
    /// CardConfig.rarity（1..3）。**降级类机制的判据**：原码到处是
    /// <c>if (cardConfig.rarity &gt;= 1 &amp;&amp; cardConfig.id != 19) cardItem.LevelDown();</c>
    /// —— 可降级就降一档，否则走另一支（如卡 181 的「吸取生命及上限」、卡 11000018 的反伤）。
    /// </summary>
    int CardRarity(int cardId);

    /// <summary>
    /// CardConfig.jianYi / guaXiang —— **没有 `Card_&lt;baseId&gt;` 类的牌靠 FallbackCardAction 施加**（37228）：
    /// <c>if (cardConfig.jianYi &gt; 0) ModifyBuffValue(BuffType.JianYi, cardConfig.jianYi);</c>，
    /// 卦象同理。这类牌在抽取器里查不到 `Card_N` 类 → ops 为空 → 落到 <see cref="Effects.CardEffects.Default"/>，
    /// 早先 Default 只做「防御 + 攻击」，于是这批卡的灵气/剑意/卦象整块消失
    /// （实测 105 张基础卡落在 Default 且 an/jy/gx 非零，其中 12 张是「剑意」型）。
    /// </summary>
    int CardJianYi(int cardId);

    /// <inheritdoc cref="CardJianYi"/>
    int CardGuaXiang(int cardId);

    /// <summary>
    /// CardConfig.randomAttack / randomDef —— 随机区间上限（「{attack}～{randomAttack}攻」那一族）。
    /// FallbackCardAction（37228）里：<c>randomAttack &gt; attack</c> 时**每一段伤害各自取一次**
    /// `GetNextRandomValue()`；<c>randomDef &gt; 0</c> 时防御也改取 `GetNextRandomValue()`。
    /// 这不是装饰 —— 完整批次里这一族在「排除 real 可疑桶」的榜上是**头号类**（4000055 偏差 241、4000030 209）。
    /// </summary>
    int CardRandomAttack(int cardId);

    /// <inheritdoc cref="CardRandomAttack"/>
    int CardRandomDef(int cardId);

    /// <summary>
    /// 境界基础总血（LevelConfig.baseMaxHp）。**开局总血要用**：见 <c>BattleCharacter.GetStartMaxHp</c> ——
    /// 境界为 InvalidLevel 时总血就是 extraMaxHp，否则是 extraMaxHp + 该境界的基础血。
    /// </summary>
    int LevelBaseMaxHp(int level);

    /// <summary>
    /// buff 分类（对应 ConfigManager.GetBuffCategory ← BuffConfig 表）。
    /// debuff 判定全靠它；缺数据时返回 <see cref="BuffCategory.Positive"/>（即不算 debuff）。
    /// </summary>
    BuffCategory BuffCategoryOf(BuffType buff);

    /// <summary>
    /// 共鸣的生效门槛（TalentResonanceConfig.effectRound / effectLevel）。
    /// 缺数据返回 (0,0)：effectRound=0 表示不限回合；effectLevel=0 是 InvalidLevel，同样视为不限。
    /// </summary>
    (int EffectRound, int EffectLevel) ResonanceGate(int resonanceTalentId);
}

/// <summary>字典回填的配置，用于测试与本地数据装载。缺失 id 返回空表。</summary>
public sealed class StubBattleConfig : IBattleConfig
{
    private readonly Dictionary<int, int[]> _card = new();
    private readonly Dictionary<int, string> _cardName = new();
    private readonly Dictionary<int, string> _cardDesc = new();
    private readonly Dictionary<int, string> _keyinDesc = new();
    private readonly Dictionary<int, int> _keyinHp = new();
    private readonly HashSet<int> _actionAgain = new();
    private readonly HashSet<int> _noUpgrade = new();
    private readonly HashSet<int> _sustain = new();
    private readonly HashSet<int> _consume = new();
    private readonly Dictionary<int, int> _levelHp = new();
    private readonly Dictionary<int, int[]> _talent = new();
    private readonly Dictionary<int, int[]> _fate = new();
    private readonly Dictionary<int, int[]> _resonance = new();
    private readonly Dictionary<int, int[]> _keyin = new();
    private readonly Dictionary<int, BuffCategory> _buffCat = new();
    private readonly Dictionary<int, int[]> _resGate = new();

    public static readonly StubBattleConfig Empty = new();

    public StubBattleConfig Card(int id, params int[] otherParams) { _card[id] = otherParams; return this; }
    public StubBattleConfig CardNamed(int id, string name) { _cardName[id] = name; return this; }
    public StubBattleConfig Talent(int id, params int[] otherParams) { _talent[id] = otherParams; return this; }
    public StubBattleConfig Fate(int id, params int[] otherParams) { _fate[id] = otherParams; return this; }
    public StubBattleConfig Resonance(int id, params int[] otherParams) { _resonance[id] = otherParams; return this; }
    public StubBattleConfig KeYin(int id, params int[] otherParams) { _keyin[id] = otherParams; return this; }
    /// <summary>刻印牌的描述（判「[被动生效]」）与开局生命修正 maxHp。</summary>
    public StubBattleConfig KeYinMeta(int id, string desc, int maxHp = 0) { _keyinDesc[id] = desc; _keyinHp[id] = maxHp; return this; }
    public string KeYinDesc(int keYinCardId) => _keyinDesc.TryGetValue(keYinCardId, out var v) ? v : "";
    public int KeYinMaxHp(int keYinCardId) => _keyinHp.TryGetValue(keYinCardId, out var v) ? v : 0;

    public IReadOnlyList<int> CardOtherParams(int cardId) => Get(_card, cardId);
    public IReadOnlyList<int> TalentOtherParams(int talentId) => Get(_talent, talentId);
    public IReadOnlyList<int> FateOtherParams(int fateId) => Get(_fate, fateId);
    public IReadOnlyList<int> ResonanceOtherParams(int resonanceTalentId) => Get(_resonance, resonanceTalentId);
    public IReadOnlyList<int> KeYinOtherParams(int keYinCardId) => Get(_keyin, keYinCardId);
    public string CardName(int cardId) => _cardName.TryGetValue(cardId, out var v) ? v : "";

    /// <summary>测试用：登记某卡的描述文本（后招/唯一等标记的判定来源）。</summary>
    public StubBattleConfig CardDescribed(int id, string desc) { _cardDesc[id] = desc; return this; }

    public string CardDesc(int cardId) => _cardDesc.TryGetValue(cardId, out var v) ? v : "";

    /// <summary>测试用：登记某张牌的无条件再行动。</summary>
    public StubBattleConfig ActionAgainCard(int id) { _actionAgain.Add(id); return this; }

    public bool CardActionAgain(int cardId) => _actionAgain.Contains(cardId);

    /// <summary>测试用：登记某张牌不可升级（默认都可升级，与游戏一致）。</summary>
    public StubBattleConfig NoUpgradeCard(int id) { _noUpgrade.Add(id); return this; }

    public bool CanUpgrade(int cardId) => !_noUpgrade.Contains(cardId);

    /// <summary>测试用：登记某张牌是持续牌（CardType.Sustain）。</summary>
    public StubBattleConfig SustainCard(int id) { _sustain.Add(id); return this; }

    public bool CardIsSustain(int cardId) => _sustain.Contains(cardId);

    /// <summary>测试用：登记某张牌是消耗牌（CardType.Consume）。</summary>
    public StubBattleConfig ConsumeCard(int id) { _consume.Add(id); return this; }

    public bool CardIsConsume(int cardId) => _consume.Contains(cardId);

    /// <summary>测试用：登记某张牌的境界（CardConfig.level）。</summary>
    public StubBattleConfig LeveledCard(int id, int level) { _cardLevel[id] = level; return this; }
    private readonly Dictionary<int, int> _cardLevel = new();
    public int CardLevel(int cardId) => _cardLevel.TryGetValue(cardId, out var v) ? v : 0;

    /// <summary>测试用：登记过的卡 id。</summary>
    public IEnumerable<int> CardIds() => _card.Keys;

    /// <summary>测试用：登记某张牌的稀有度（CardConfig.rarity）。</summary>
    public BattleCard BuildCard(int cardId) => new() { Id = cardId, OtherParams = CardOtherParams(cardId) };

    public StubBattleConfig RarityCard(int id, int rarity) { _rarity[id] = rarity; return this; }
    private readonly Dictionary<int, int> _rarity = new();
    public int CardRarity(int cardId) => _rarity.TryGetValue(cardId, out var v) ? v : 0;

    /// <summary>测试用：登记某张牌的剑意/卦象（CardConfig.jianYi / guaXiang）。</summary>
    public StubBattleConfig JianYiGuaXiang(int id, int jianYi, int guaXiang)
    { _jianYi[id] = jianYi; _guaXiang[id] = guaXiang; return this; }
    private readonly Dictionary<int, int> _jianYi = new();
    private readonly Dictionary<int, int> _guaXiang = new();

    public int CardJianYi(int cardId) => _jianYi.TryGetValue(cardId, out var v) ? v : 0;
    public int CardGuaXiang(int cardId) => _guaXiang.TryGetValue(cardId, out var v) ? v : 0;
    public int CardRandomAttack(int cardId) => 0;
    public int CardRandomDef(int cardId) => 0;

    /// <summary>测试用：登记某张牌的以血代灵气代价（CardConfig.hpCost）。</summary>
    public StubBattleConfig HpCostCard(int id, int hpCost) { _hpCost[id] = hpCost; return this; }
    private readonly Dictionary<int, int> _hpCost = new();
    public int CardHpCost(int cardId) => _hpCost.TryGetValue(cardId, out var v) ? v : 0;

    public StubBattleConfig ChargeQiCard(int id, int chargeQi) { _chargeQi[id] = chargeQi; return this; }
    private readonly Dictionary<int, int> _chargeQi = new();
    public int CardChargeQi(int cardId) => _chargeQi.TryGetValue(cardId, out var v) ? v : 0;
    public int CardCareer(int cardId) => 0;

    /// <summary>测试用：登记某境界的基础总血。</summary>
    public StubBattleConfig LevelHp(int level, int baseMaxHp) { _levelHp[level] = baseMaxHp; return this; }

    public int LevelBaseMaxHp(int level) => _levelHp.TryGetValue(level, out var v) ? v : 0;

    /// <summary>测试用：登记某 buff 的分类（debuff 判定）。</summary>
    public StubBattleConfig Buff(BuffType buff, BuffCategory category) { _buffCat[(int)buff] = category; return this; }

    public BuffCategory BuffCategoryOf(BuffType buff)
        => _buffCat.TryGetValue((int)buff, out var v) ? v : BuffCategory.Positive;

    /// <summary>测试用：登记某共鸣的生效门槛。</summary>
    public StubBattleConfig ResonanceGate(int resonanceTalentId, int effectRound, int effectLevel)
    { _resGate[resonanceTalentId] = new[] { effectRound, effectLevel }; return this; }

    public (int EffectRound, int EffectLevel) ResonanceGate(int resonanceTalentId)
        => _resGate.TryGetValue(resonanceTalentId, out var v) ? (v[0], v[1]) : (0, 0);

    private static readonly int[] EmptyArr = [];
    private static IReadOnlyList<int> Get(Dictionary<int, int[]> d, int id) => d.TryGetValue(id, out var v) ? v : EmptyArr;
}

/// <summary>安全取 otherParams：越界/缺失返回 0（忠实分支多在 HasBuff 为假时不取值，缺配置以 0 兜底）。</summary>
public static class BattleConfigExtensions
{
    public static int At(this IReadOnlyList<int> list, int index)
        => index >= 0 && index < list.Count ? list[index] : 0;
}
