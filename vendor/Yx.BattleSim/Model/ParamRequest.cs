namespace Yx.BattleSim.Model;

/// <summary>
/// 一次取参数（<c>GetNextParam</c> / <c>GetNextRandomValue</c>）的**语义**：这个值被拿去做什么、值域是什么。
///
/// 只在**估值模式**（<see cref="Combatant.EstimateParams"/>，摆牌求解开战前没有服务器参数）下才被读；
/// 真参数模式照旧逐个出队，请求被忽略 —— 所以对拍回归与加这层之前逐位一致。
/// 每个消费点（卡效果 / 回合钩子 / 刻印 / 天赋）自己声明请求，估值器**不再按 <c>CurrentCardId</c> 猜**
/// （那样在回合开始 / 结束钩子、刻印等不在出牌过程里的消费点会拿到上一张牌的规则）。
/// 全清单与分类依据：docs/superpowers/notes/2026-09-23-battle-params-inventory.md。
/// </summary>
public enum ParamKind : byte
{
    /// <summary>C 类 / 未登记：估值返回 -1（与空队列相同）。</summary>
    Unknown,
    /// <summary>值已知（<see cref="ParamRequest.Lo"/>），例如对本场战斗没有影响的判定。</summary>
    Exact,
    /// <summary>整数区间 [Lo, Hi] 均匀；吃到卦象 → Hi（本库所有区间型消费点都是越大越好）。</summary>
    Range,
    /// <summary>百分比判定 0..99 均匀；吃到卦象 → 0（`&lt; X` 判定必中、164 的 `&gt;= 10` 自伤必免）。</summary>
    Percent,
    /// <summary>小枚举 {0..Hi} 均匀（switch 分派；不吃卦象）。</summary>
    Choice,
    /// <summary>取值方**当前**身上的负面类型里均匀挑一种；没有负面 → -1（服务器写 -1 表示「没有了」）。</summary>
    OwnDebuff,
    /// <summary>从固定负面池里挑（池 = <see cref="ParamDebuffPool"/>，放在 Lo）。</summary>
    DebuffPool,
    /// <summary>从卡池里挑一张来用（池 = <see cref="ParamCardPool"/>，放在 Lo）；池空 → -1。</summary>
    CardPool,
    /// <summary>手牌中的消耗 / 持续牌里挑一张（349 梦•回响阵纹）；没有 → -1。</summary>
    HandPick,
    /// <summary>A 类：由盘面精确算（按 <see cref="ParamRequest.CardId"/> 的 baseId 分派公式）。</summary>
    Formula,
}

/// <summary>固定负面池（值为 BuffType 数值的列表，见 <c>ParamPools.Debuffs</c>）。</summary>
public enum ParamDebuffPool : byte
{
    /// <summary>6 种：内伤 / 减攻 / 虚弱 / 破绽 / 困缚 / 外伤（193、6000007；yisim 同表）。</summary>
    Six,
    /// <summary>内伤 / 虚弱 / 外伤（4000085 元婴以下）。</summary>
    NeiXuWai,
    /// <summary>内伤 / 虚弱 / 外伤 / 减攻（4000085 元婴档）。</summary>
    NeiXuWaiJian,
    /// <summary>7 种：6 种 + 冥（73 疯魔；yisim DEBUFF_NAMES）。</summary>
    Seven,
    /// <summary>刻印 161：8 种负面里挑，困缚改写成冥（所以冥占两份）。</summary>
    KeYin161,
}

/// <summary>「随机使用 N 张牌」一族的卡池（筛选条件见 <c>ParamPools</c>）。</summary>
public enum ParamCardPool : byte
{
    /// <summary>门派牌（64 五彩鲛珠、6000011 神来之笔）。</summary>
    Sect,
    /// <summary>门派牌或副职牌（352 天马行空）。</summary>
    SectOrCareer,
    /// <summary>含攻击效果的门派牌（353 金戈铁马）。</summary>
    SectAttack,
    /// <summary>不含攻击效果的门派牌（354 秣马厉兵）。</summary>
    SectNoAttack,
    /// <summary>其他门派的牌（355 指鹿为马；取值方宗门为 0 时 = 全部门派）。</summary>
    OtherSect,
    /// <summary>粽子牌（358 野马分粽）。</summary>
    ZongZi,
    /// <summary>门派 / 副职里与「再次行动」或「身法」有关的牌（359 一马当先）。</summary>
    ActionAgainOrShenFa,
    /// <summary>名字含「马」的牌（360 蛛丝马迹）。</summary>
    MaName,
    /// <summary>任意门派的秘术牌（361 马到成功）。</summary>
    MiShu,
    /// <summary>专属牌（362 谈马掌）。</summary>
    Exclusive,
    /// <summary>化神期梦境牌（363 梦•天马行空）。</summary>
    HuaShenDream,
    /// <summary>机缘牌（364 心猿意马）。</summary>
    JiYuan,
    /// <summary>返虚期牌（365 一马平川）。</summary>
    FanXu,
    /// <summary>元婴期及以上的灵宠 / 法宝 / 秘术牌（341 梦•神来之笔）。</summary>
    HighTreasure,
    /// <summary>「狂剑」牌（186 狂剑•降神）。</summary>
    KuangJian,
    /// <summary>崩拳（10000065 无尽崩绝，1 级）。</summary>
    BengQuan,
    /// <summary>
    /// 与前一格牌相同五行、相同等级的化神期常规牌（7040075 梦•浑天印化神档）。
    /// 候选按前一格牌的五行筛，抽中后换成前一格牌的等级；前一格没有五行 → -1。
    /// </summary>
    HuaShenSameWuXing,
}

/// <summary>
/// 一次取参数的请求。<see cref="Site"/> 是消费点标识（卡牌消费点 = baseId；非卡牌消费点见 <see cref="ParamSite"/>），
/// 只用于诊断（ParamProbe / 估值统计）；估值只看 <see cref="Kind"/> 与值域。
/// <see cref="Lucky"/> 由 <see cref="Combatant.GetNextRandomValue"/> 在扣到卦象 / 星力 / 共鸣71 灵气时置上
/// （服务器此时写进队列的是「最优值」，见笔记 §0）。
/// </summary>
public readonly record struct ParamRequest(ParamKind Kind, int Lo, int Hi, int Site, int CardId, bool Lucky = false)
{
    /// <summary>没有声明语义的取值（只给测试 / 诊断的无参重载用）。</summary>
    public static readonly ParamRequest None = new(ParamKind.Unknown, 0, 0, 0, 0);

    public static ParamRequest Unknown(int site) => new(ParamKind.Unknown, 0, 0, site, 0);
    public static ParamRequest Exact(int site, int value) => new(ParamKind.Exact, value, value, site, 0);
    public static ParamRequest Range(int site, int lo, int hi) => new(ParamKind.Range, lo, hi, site, 0);
    public static ParamRequest Percent(int site) => new(ParamKind.Percent, 0, 99, site, 0);
    /// <summary>{0 .. n-1} 均匀。</summary>
    public static ParamRequest Choice(int site, int n) => new(ParamKind.Choice, 0, n - 1, site, 0);
    public static ParamRequest OwnDebuff(int site) => new(ParamKind.OwnDebuff, 0, 0, site, 0);
    public static ParamRequest FromDebuffPool(int site, ParamDebuffPool pool) => new(ParamKind.DebuffPool, (int)pool, 0, site, 0);
    public static ParamRequest FromCardPool(int site, ParamCardPool pool) => new(ParamKind.CardPool, (int)pool, 0, site, 0);
    public static ParamRequest HandPick(int site) => new(ParamKind.HandPick, 0, 0, site, 0);
    /// <summary>A 类公式：按 <paramref name="cardId"/> 的 baseId 分派（完整 id 用来读本牌的 otherParams / 等级）。</summary>
    public static ParamRequest Formula(int cardId) => new(ParamKind.Formula, 0, 0, Effects.CardTypes.BaseId(cardId), cardId);
}

/// <summary>非卡牌消费点的 <see cref="ParamRequest.Site"/>（负数，不与 baseId 冲突）。</summary>
public static class ParamSite
{
    /// <summary>356 走马观花挂的 buff：回合开始二选一。</summary>
    public const int ZouMaGuanHua = -356;
    /// <summary>192 琴天霹雳曲挂的 buff：回合结束 10%。</summary>
    public const int QinTianPiLiQu = -192;
    /// <summary>刻印 161：打出第 1 格牌时随机获得负面。</summary>
    public const int KeYin161 = -161001;
    /// <summary>崩拳•截脉挂的 buff：下一张崩拳转移负面。</summary>
    public const int BengQuanJieMai = -10000024;
    /// <summary>天赋 252：用名字含「雷」的牌后追加随机攻。</summary>
    public const int Talent252 = -252;
    /// <summary>天赋 108 瞒天过海：后招 1%。</summary>
    public const int Talent108 = -108;
    /// <summary>刻印 21 驱邪：随机减负面。</summary>
    public const int KeYin21 = -21001;
    /// <summary>刻印 162 破玄魔：负面转加攻。</summary>
    public const int KeYin162 = -162001;
    /// <summary>FallbackCardAction：随机攻（每段一次）。</summary>
    public const int FallbackAttack = -1;
    /// <summary>FallbackCardAction：随机防。</summary>
    public const int FallbackDef = -2;
}
