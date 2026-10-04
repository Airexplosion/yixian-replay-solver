using System.Text.Json;
using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 数据驱动卡效果（P5）：从 card_effects.local.json（tools/refs/card-pipeline/extract_effects.py 产出）
/// 加载每个 baseId 的**语句序列**（局部变量 + 效果调用，保序）并解释执行。
///
/// v2（2026-09-22）：值从「字面量/参数/字段」三选一升级为**表达式树**，覆盖了原先被判 complex 的大部分卡
/// （auto 342 → 558 / 925）。真正带 gameplay 条件、循环、变量流不确定的卡仍标 complex、不在此应用
/// （宁可退回 <see cref="CardEffects.Default"/>，也不拿猜出来的数值当正确值）。
///
/// ⚠ 只保真「施加 buff/def/hp/anima + 攻击」这一侧；这些 buff 的**下游效果**取决于读取侧
///   （ApplyDamage/CalculateAttack/ModifyHp）是否已建模该 buff。数据是厂商材料，gitignore。
/// </summary>
public static class CardEffectData
{
    // ───────────────────────── 表达式树 ─────────────────────────

    internal abstract record Expr;
    internal sealed record Lit(int V) : Expr;
    internal sealed record Param(int I) : Expr;
    internal sealed record Field(string Name) : Expr;
    internal sealed record Var(string Name) : Expr;
    internal sealed record Temp(string Side, string Name) : Expr;
    internal sealed record BuffName(string Name) : Expr;          // BuffType.X
    internal sealed record NextParam(int K) : Expr;                // 玩家选牌参数（第 K 个）
    internal sealed record Neg(Expr E) : Expr;
    internal sealed record Arith(char Op, Expr L, Expr R) : Expr;  // + - * /
    /// <summary>比较（gt/ge/lt/le/eq/ne）：只用于 if 守卫，求值结果 1/0。</summary>
    internal sealed record Cmp(string Op, Expr L, Expr R) : Expr;
    /// <summary>逻辑非（`!x`）。**不是算术取负**：neg(1) = -1，判真看 != 0 仍是真，用错方向就全反了。</summary>
    internal sealed record Not(Expr E) : Expr;
    /// <summary>逻辑与（`&&`）：两侧都非 0 才为 1。</summary>
    internal sealed record And(Expr L, Expr R) : Expr;
    internal sealed record Or(Expr L, Expr R) : Expr;
    /// <summary>
    /// 谓词调用（`if (CheckHouZhao(src, item))` 这类门控）。名字对应 sim 里**已实现**的判定，
    /// 抽取器识别出来后才敢把门控块里的语句一起搬 —— 否则那一整段效果会被丢（实测卡体 if 条件里共 296 处）。
    /// <see cref="Side"/> 是判定对象（多数是 src）。
    /// </summary>
    internal sealed record Pred(string Name, string Side, Expr? Arg) : Expr;
    internal sealed record Cond(Expr C, Expr A, Expr B) : Expr;
    internal sealed record CastBuff(Expr E) : Expr;
    internal sealed record Call(string Name, Expr[] Args) : Expr;
    internal sealed record BuffVal(string Side, string Buff) : Expr;
    /// <summary>
    /// 「用另一张牌」时卡 id 的来源之一：**相邻格的牌**。
    /// 对应 <c>GetNextGridCardConfig(src, grid).id</c> / <c>…Previous…</c>（卡 173 瑶光溯时镜「复制后一格牌的效果」、
    /// 卡 60 魔化鲛珠「重复上一格牌的效果」）。
    /// </summary>
    internal sealed record GridCard(string Which) : Expr;
    /// <summary>
    /// 卡 id 来源之二：<b>某方牌组的队首</b>。对应 <c>GetCurrentCardInDeck()</c>（= <c>m_TempBattleCardItems[0]</c>，9708）。
    /// 卡 177 灵韵御心琴「复制对方下1张牌的效果」用它。
    /// </summary>
    internal sealed record DeckTop(string Side) : Expr;

    /// <summary>比较运算符（JSON 键名）——抽取器对 if 守卫产出的就是这些。</summary>
    private static readonly string[] CmpOps = ["gt", "ge", "lt", "le", "eq", "ne"];

    internal abstract record Stmt;
    /// <summary>
    /// 局部变量。<see cref="Cond"/> 非空表示它原本在 if 块里（守卫成立才赋值）——
    /// 抽出来时必须带上守卫，否则「一个 if 里的 let」会把后面用到它的整条语句链一起拖没。
    /// </summary>
    internal sealed record Let(string Name, Expr E, Expr? Cond = null) : Stmt;
    /// <summary>
    /// 一条效果。<see cref="Cond"/> 非空表示它被 if 守卫着（如 <c>num2 &gt; 0</c>）——
    /// 求值为 0 时**跳过**该 op。这是把「条件块内的语句」也搬进来的前提：条件本身也解析成了表达式，
    /// 所以不是猜，是真判。条件解析不出来的那种仍在抽取期丢弃（见 extract_effects.py）。
    /// </summary>
    internal sealed record Eff(string Kind, string? Target, Expr? Buff, Expr? Val, Expr? Atk, Expr? Count, Expr? Cond) : Stmt;
    /// <summary>
    /// <c>{"repeat": &lt;次数表达式&gt;, "body": [ &lt;stmt&gt; … ]}</c> —— **循环体重复 N 次**。
    /// 抽取器把「被压平的简单循环」（`for (i = 0; i &lt; N; i++) { body }` 只搬了一次 body）
    /// 收成这条（`extract_effects.py` 的 `fold_simple_loops`）。
    /// 实测卡 108 海鲜肉粽「重复 4 次：生命 + 5」：real `L=56` / sim `L=19`（R 侧精确，只差这 4 倍回血）。
    /// </summary>
    internal sealed record Repeat(Expr Count, Stmt[] Body) : Stmt;

    // ───────────────────────── 装载 ─────────────────────────

    private static readonly Dictionary<int, Stmt[]> ByBase = new();

    /// <summary>已加载（自动可应用）的卡数。</summary>
    public static int Count => ByBase.Count;

    /// <summary>装载时被跳过的卡数（complex / 解析失败），供覆盖率报告。</summary>
    public static int SkippedCount { get; private set; }

    /// <summary>usecard 执行后记录「临时牌自己的 actionAgain」的变量名（不与抽取出的变量名冲突）。</summary>
    private const string TempActionAgainVar = "__temp_actionAgain";

    /// <summary>
    /// [诊断] **整卡不注册**的 baseId（complex / 解析失败 / partial 却无 gameplay op）—— 这些卡会退回默认效果，
    /// 对 a=0 的卡等于什么都不做。
    /// 装载统计里只报原因计数，看不到是哪张卡；排查「某张卡的效果一次都没生效」时先看这里。
    /// </summary>
    public static List<int> SkippedIds { get; } = new();

    /// <summary>装进来的语句里出现「未知 buff 名」而被跳过的 op 数。</summary>
    public static int UnknownBuffOps { get; private set; }

    /// <summary>解析时抛异常而被跳过的卡数（正常应为 0）。</summary>
    public static int LoadErrors { get; private set; }

    static CardEffectData() => TryLoadDefault();

    /// <summary>
    /// 是否加载 partial 卡（条件块被丢、无条件语句保留）。
    /// 置 false 可随时做 A/B 对照——「通过数」常被微小偏差抹平，得靠连续指标比才算数。
    /// </summary>
    public static bool AcceptPartial { get; set; } = true;

    private static bool _tried;

    /// <summary>按当前 <see cref="AcceptPartial"/> 重新装载（对照实验用）。</summary>
    public static void Reload()
    {
        _tried = false;
        TryLoadDefault();
    }

    public static void TryLoadDefault()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            string? path = SimData.Find("card_effects");
            if (path is not null) Load(path);
        }
        catch { /* 无数据则不注册，走默认 */ }
    }

    /// <summary>按当前数据目录（<see cref="SimData"/>）重新加载。成功加载到至少一张卡返回 true。</summary>
    public static bool ReloadFromDataDirectory()
    {
        _tried = false;
        ByBase.Clear();
        TryLoadDefault();
        return ByBase.Count > 0;
    }

    public static void Load(string path)
    {
        ByBase.Clear();
        SkippedCount = 0; SkippedIds.Clear();
        UnknownBuffOps = 0;
        PartialCount = 0;
        PartialNoGameplay = 0;
        UnknownOps = 0;
        UnknownCalls = 0;
        SkipReasons.Clear();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var card in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(card.Name, out int baseId)) continue;
            var body = card.Value;
            string why = body.TryGetProperty("reason", out var rz) ? rz.GetString() ?? "" : "";
            if (body.TryGetProperty("complex", out var cx) && cx.GetBoolean())
            { SkippedCount++; SkipReasons["complex:" + why] = SkipReasons.GetValueOrDefault("complex:" + why) + 1; SkippedIds.Add(baseId); continue; }
            if (!body.TryGetProperty("ops", out var opsEl) || opsEl.GetArrayLength() == 0) continue;
            var list = new List<Stmt>();
            bool ok = true;
            try
            {
                foreach (var o in opsEl.EnumerateArray())
                {
                    var st = ParseStmt(o);
                    if (st is null) { ok = false; break; }
                    list.Add(st);
                }
            }
            catch { ok = false; LoadErrors++; }
            if (!ok || list.Count == 0)
            {
                SkippedCount++;
                SkipReasons["unparsable:" + why] = SkipReasons.GetValueOrDefault("unparsable:" + why) + 1;
                SkippedIds.Add(baseId);
                continue;
            }

            if (body.TryGetProperty("partial", out var pt) && pt.GetBoolean())
            {
                PartialCount++;
                if (!AcceptPartial) { SkippedCount++; SkippedIds.Add(baseId); continue; }
                // partial 卡的可信度只来自「搬出来的无条件语句」。若这些语句里**没有任何 gameplay op**
                // （只有 let 之类的中间量），那这张卡等于什么都没搬 —— 此时必须退回 Default，
                // 否则比现在更糟：Default 至少还会按 cardConfig 打一次攻击。
                if (!list.Exists(HasGameplayEffect)) { PartialNoGameplay++; SkippedCount++; SkippedIds.Add(baseId); continue; }
            }
            ByBase[baseId] = list.ToArray();
        }
    }

    /// <summary>该语句是否产生 gameplay 效果（而非只是算了个中间量）。</summary>
    /// <summary>
    /// 该语句是否产生 gameplay 效果（而非只是算了个中间量）。
    /// ⚠ ** 也要算**：循环体里全是真效果，只看顶层  会把「整卡就一条 repeat」的卡
    /// 误判成「什么都没搬」→ 退回 Default → 整卡空转（卡 108/110 那类）。
    /// </summary>
    private static bool HasGameplayEffect(Stmt s) => s switch
    {
        Eff => true,
        Repeat rp => Array.Exists(rp.Body, HasGameplayEffect),
        _ => false,
    };

    /// <summary>partial（条件块被丢、无条件部分保留）加载进来的卡数。</summary>
    public static int PartialCount { get; private set; }

    /// <summary>partial 里因「搬出来的语句毫无 gameplay 效果」而仍退回 Default 的卡数。</summary>
    public static int PartialNoGameplay { get; private set; }

    /// <summary>装进来的语句里出现「未识别的 op 名」而被静默忽略的次数（正常应为 0）。</summary>
    public static int UnknownOps { get; private set; }

    /// <summary>表达式求值时遇到「未识别的函数名」的次数（正常应为 0）。
    /// 和 <see cref="UnknownOps"/> 同一条原则：求值端**绝不**静默返回 0 ——
    /// 一个悄悄变成 0 的伤害比一个抛出来的错误难查一百倍。</summary>
    public static int UnknownCalls { get; private set; }

    /// <summary>[诊断] 打开后才统计五行谓词触发次数（默认关，避免拖慢吞吐基准）。</summary>
    public static bool TraceWuxing;

    /// <summary>[诊断] 未识别的 battleTempData 字段直方图（应为空；非空说明 TmpOf 缺映射）。</summary>
    public static readonly Dictionary<string, int> UnknownTemps = new();

    /// <summary>[诊断] 五行谓词触发直方图（键形如 `JiHuoHuoLing=TRUE`）。</summary>
    public static readonly Dictionary<string, int> WuxingHits = new();

    /// <summary>跳过原因直方图（诊断用）——「为什么这张卡没效果」第一个该看的东西。</summary>
    public static readonly Dictionary<string, int> SkipReasons = new();

    private static Stmt? ParseStmt(JsonElement o)
    {
        if (o.TryGetProperty("repeat", out var repCount))
        {
            var repN = ParseExpr(repCount);
            if (repN is null || !o.TryGetProperty("body", out var bodyEl)) return null;
            var body = new List<Stmt>();
            foreach (var bst in bodyEl.EnumerateArray())
            {
                var st = ParseStmt(bst);
                if (st is null) return null;
                body.Add(st);
            }
            return new Repeat(repN, body.ToArray());
        }
        if (o.TryGetProperty("let", out var letName))
        {
            var e = ParseExpr(PeekExpr(o, "e"));
            if (e is null) return null;
            Expr? lcond = o.TryGetProperty("cond", out var lc) ? ParseExpr(lc) : null;
            return new Let(letName.GetString()!, e, lcond);
        }
        if (!o.TryGetProperty("op", out var kindEl)) return null;
        string kind = kindEl.GetString()!;
        string? target = o.TryGetProperty("target", out var t) ? t.GetString() : null;
        Expr? buff = o.TryGetProperty("buff", out var b) ? ParseExpr(b) : null;
        Expr? val = o.TryGetProperty("val", out var v) ? ParseExpr(v) : null;
        Expr? atk = o.TryGetProperty("atk", out var a) ? ParseExpr(a) : null;
        Expr? cnt = o.TryGetProperty("count", out var c) ? ParseExpr(c) : null;
        Expr? cond = o.TryGetProperty("cond", out var cd) ? ParseExpr(cd) : null;
        return new Eff(kind, target, buff, val, atk, cnt, cond);
    }

    private static JsonElement PeekExpr(JsonElement o, string key) => o.GetProperty(key);

    internal static Expr? ParseExpr(JsonElement e)
    {
        try { return ParseExprCore(e); }
        catch { return null; }   // 单张卡格式异常不能中断整批装载
    }

    private static Expr? ParseExprCore(JsonElement e)
    {
        // 旧格式（v1 数据）：{"lit":N} / {"param":i} / {"field":"def"} —— 本就是新 schema 的叶子。
        if (e.ValueKind == JsonValueKind.String)
        {
            // 兼容 v1 的 "buff":"Name" 字符串形式
            return new BuffName(e.GetString()!);
        }
        if (e.ValueKind == JsonValueKind.Number) return new Lit(e.GetInt32());
        if (e.ValueKind != JsonValueKind.Object) return null;

        if (e.TryGetProperty("lit", out var lit)) return new Lit(lit.GetInt32());
        if (e.TryGetProperty("param", out var p)) return new Param(p.GetInt32());
        if (e.TryGetProperty("field", out var f)) return new Field(f.GetString()!);
        if (e.TryGetProperty("var", out var v)) return new Var(v.GetString()!);
        if (e.TryGetProperty("buff", out var b)) return new BuffName(b.GetString()!);
        if (e.TryGetProperty("nextparam", out var np)) return new NextParam(np.GetInt32());
        if (e.TryGetProperty("gridcard", out var gc))
            return new GridCard(gc.GetString() == "prev" ? "prev" : "next");
        if (e.TryGetProperty("decktop", out var dt))
            return new DeckTop(dt.GetString() ?? "dst");
        if (e.TryGetProperty("strlit", out _)) return new Lit(0);      // 表现用的字符串字面量，数值上忽略
        if (e.TryGetProperty("who", out _)) return new Lit(0);
        if (e.TryGetProperty("neg", out var n)) return Wrap(n, x => new Neg(x));
        if (e.TryGetProperty("not", out var nt)) return Wrap(nt, x => new Not(x));
        if (e.TryGetProperty("pred", out var pr))
        {
            string pname = pr.GetString()!;
            string pside = e.TryGetProperty("side", out var ps) ? ps.GetString() ?? "src" : "src";
            Expr? parg = e.TryGetProperty("a", out var pa) ? ParseExpr(pa) : null;
            return new Pred(pname, pside, parg);
        }
        if (e.TryGetProperty("and", out var an))
        {
            var al = ParseExpr(an[0]); var ar = ParseExpr(an[1]);
            return al is null || ar is null ? null : new And(al, ar);
        }
        if (e.TryGetProperty("or", out var orr))
        {
            var ol = ParseExpr(orr[0]); var or2 = ParseExpr(orr[1]);
            return ol is null || or2 is null ? null : new Or(ol, or2);
        }
        // 比较（if 守卫用）：{"gt":[L,R]} / ge / lt / le / eq / ne
        foreach (var opn in CmpOps)
        {
            if (!e.TryGetProperty(opn, out var carr) || carr.ValueKind != JsonValueKind.Array) continue;
            var l0 = ParseExpr(carr[0]); var r0 = ParseExpr(carr[1]);
            return l0 is null || r0 is null ? null : new Cmp(opn, l0, r0);
        }
        if (e.TryGetProperty("cast", out var cast))
        {
            var inner = cast[1];
            return Wrap(inner, x => new CastBuff(x));
        }
        if (e.TryGetProperty("buffval", out var bv))
        {
            string side = bv[0].GetString()!;
            string name = bv[1].GetString()!;
            return new BuffVal(side, name);
        }
        if (e.TryGetProperty("temp", out var tp))
        {
            return new Temp(tp[0].GetString()!, tp[1].GetString()!);
        }
        if (e.TryGetProperty("cond", out var cond))
        {
            var c0 = ParseExpr(cond[0]); var c1 = ParseExpr(cond[1]); var c2 = ParseExpr(cond[2]);
            return c0 is null || c1 is null || c2 is null ? null : new Cond(c0, c1, c2);
        }
        if (e.TryGetProperty("call", out var call))
        {
            var args = new List<Expr>();
            foreach (var a in e.GetProperty("a").EnumerateArray())
            {
                var ax = ParseExpr(a);
                if (ax is null) return null;
                args.Add(ax);
            }
            return new Call(call.GetString()!, args.ToArray());
        }
        foreach (var (key, op) in new[] { ("add", '+'), ("sub", '-'), ("mul", '*'), ("div", '/') })
        {
            if (e.TryGetProperty(key, out var bin))
            {
                var l = ParseExpr(bin[0]); var r = ParseExpr(bin[1]);
                return l is null || r is null ? null : new Arith(op, l, r);
            }
        }
        if (e.TryGetProperty("index", out _)) return null;   // 未支持的索引访问
        return null;
    }

    private static Expr? Wrap(JsonElement inner, Func<Expr, Expr> make)
    {
        var x = ParseExpr(inner);
        return x is null ? null : make(x);
    }

    // ───────────────────────── 执行 ─────────────────────────

    /// <summary>有该 baseId 的数据驱动效果则执行并返回 true。</summary>
    /// <summary>这张基础 id 有没有注册数据驱动效果（TryApply 会不会生效）。</summary>
    public static bool IsLoaded(int baseId) => ByBase.ContainsKey(baseId);

    public static bool TryApply(int baseId, Combatant src, Combatant dst, BattleCard card)
    {
        if (!ByBase.TryGetValue(baseId, out var stmts)) return false;
        ApplyStmts(stmts, new Ctx(src, dst, card, src.Config));
        return true;
    }

    /// <summary>已加载的 baseId 集合（覆盖率/冒烟测试用）。</summary>
    public static IReadOnlyCollection<int> LoadedBaseIds => ByBase.Keys;

    /// <summary>测试/诊断接缝：直接应用一段卡定义 JSON，**不碰全局注册表**（避免测试间互相干扰）。</summary>
    public static bool ApplyJson(string cardJson, Combatant src, Combatant dst, BattleCard card)
    {
        using var doc = JsonDocument.Parse(cardJson);
        if (!doc.RootElement.TryGetProperty("ops", out var opsEl)) return false;
        var list = new List<Stmt>();
        foreach (var o in opsEl.EnumerateArray())
        {
            var st = ParseStmt(o);
            if (st is null) return false;
            list.Add(st);
        }
        ApplyStmts(list, new Ctx(src, dst, card, src.Config));
        return true;
    }

    private static void ApplyStmts(IReadOnlyList<Stmt> stmts, Ctx ctx)
    {
        foreach (var st in stmts)
        {
            switch (st)
            {
                case Let l:
                    // 带守卫的 let：守卫不成立就**不赋值**（变量保持未定义/上一次的值），
                    // 与「这个变量本来就在 if 里」一致。
                    if (l.Cond is null || Eval(l.Cond, ctx) != 0) ctx.Vars[l.Name] = Eval(l.E, ctx);
                    else if (!ctx.Vars.ContainsKey(l.Name)) ctx.GuardedOff.Add(l.Name);
                    break;
                case Eff e: ApplyEff(e, ctx); break;
                case Repeat rp:
                    // ⚠ **每次都重新求值次数**：`{"param": i}` 那种在循环里不变，但保序更安全。
                    // 上界加护栏：数据脏（N 极大）时不至于把整场卡死。
                    int times = Eval(rp.Count, ctx);
                    if (times > 64) times = 64;
                    for (int i = 0; i < times; i++) ApplyStmts(rp.Body, ctx);
                    break;
            }
        }
    }

    /// <summary>本张数据驱动牌下一次取参数的语义（真参数模式下被忽略，只是个结构体）。</summary>
    private static ParamRequest NextRequest(Ctx c)
        => DataParamRules.For(CardTypes.BaseId(c.Card.Id), c.ParamOrdinal++, c.Src, c.Card);

    internal sealed class Ctx
    {
        public readonly Combatant Src, Dst;
        public readonly BattleCard Card;
        public readonly IBattleConfig Cfg;
        public readonly Dictionary<string, int> Vars = new();
        /// <summary>带守卫的 let 守卫不成立、从未赋过值的变量名（usecard 见到它就不出牌）。</summary>
        public readonly HashSet<string> GuardedOff = new();
        /// <summary>本次出牌里已经取过几次参数（估值规则按序号区分同一张牌的多次取值，见 <see cref="DataParamRules"/>）。</summary>
        public int ParamOrdinal;
        public int UnknownBuff;

        public Ctx(Combatant src, Combatant dst, BattleCard card, IBattleConfig cfg)
        { Src = src; Dst = dst; Card = card; Cfg = cfg; }

        public Combatant Side(string? s) => s == "dst" ? Dst : Src;
    }

    private static void ApplyEff(Eff e, Ctx c)
    {
        // if 守卫：抽出来的条件是真表达式，这里真判。为 0（假）就整条跳过 —— 与不搬的区别是
        // 「该触发的时候会触发」，与无条件搬的区别是「不该触发的时候不触发」。
        if (e.Cond is not null && Eval(e.Cond, c) == 0) return;

        switch (e.Kind)
        {
            case "buff":      c.Side(e.Target).ModifyBuffValue(ResolveBuff(e.Buff, c), Eval(e.Val, c)); break;
            case "setbuff":   c.Side(e.Target).SetBuffValue(ResolveBuff(e.Buff, c), Eval(e.Val, c)); break;
            case "removebuff": c.Side(e.Target).RemoveBuff(ResolveBuff(e.Buff, c)); break;
            case "def":       c.Side(e.Target).ModifyDef(Eval(e.Val, c)); break;
            case "hp":        c.Side(e.Target).ModifyHp(Eval(e.Val, c)); break;
            case "maxhp":     c.Side(e.Target).ModifyMaxHp(Eval(e.Val, c)); break;
            case "anima":     c.Side(e.Target).ModifyAnima(Eval(e.Val, c)); break;
            case "tipo":      c.Side(e.Target).ModifyTiPo(Eval(e.Val, c)); break;
            case "attack":    CombatMath.Attack(c.Src, c.Dst, Eval(e.Atk, c), Eval(e.Count, c)); break;
            // cardConfig.actionAgain = X;（卡效果改写行动权，如 Card_295 相生成立才再行动）
            case "actionagain": c.Src.CurrentCardActionAgain = Eval(e.Val, c) != 0; break;
            // 使用另一张牌（值 = 那张牌的 id，来自玩家参数队列；-1 = 队列空 → 不出手）。
            // 对应 cardActionBase.ExecuteEffect(src, dst, isTempCard: true)。
            // 记下临时牌自己的 actionAgain：抽出来的「使用另一张牌」卡几乎都跟一句
            // `if (cardItem.cardConfig.actionAgain) ExActionAgain+1` —— 那里读的是**临时牌**（还原之前）。
            case "usecard":
                // 抽取时常丢掉外层分支（如 7000075：`if (level == HuaShen) { id = GetNextParam(); if (id != -1) UseCard(id) }`，
                // 只剩带条件的 let）。那条 let 守卫没过 = 分支没走 → 不出牌；原码 GetNextParam 取空为 -1 同样不出。
                // ⚠ 早先这里把未赋值当 0 → 触发一张「普攻」（梦•浑天印每次多打 3）。
                // ⚠ 只认「有守卫的 let 没走到」：390 / 99000216 的 cardId 赋值压根没抽出来，仍按 0（普攻）走老路。
                if (e.Val is Var uv && c.GuardedOff.Contains(uv.Name)) break;
                int useId = Eval(e.Val, c);
                if (useId == -1) break;
                c.Vars[TempActionAgainVar] = CardEffects.TriggerCard(c.Src, c.Dst, useId) ? 1 : 0;
                break;
            // 这三个原先不在 CALL 白名单里、被静默跳过（整卡因此少算效果）。
            case "sethp":     c.Side(e.Target).SetHp(Eval(e.Val, c)); break;
            case "templife":  c.Side(e.Target).ModifyTempLife(Eval(e.Val, c)); break;
            case "removedebuff":
                // 参数可省：原码 RemoveAllDebuff(count = -1) 即全清
                c.Side(e.Target).RemoveAllDebuff(e.Val is null ? -1 : Eval(e.Val, c));
                break;
            // 星位环走：从本张牌所在格起沿环形格子往前走 N 格，逐格加星位。
            // 对应原码 `for (i<otherParams[0]) { grid = GetNextGrid(src,grid); src.AddXingWei(grid); }`
            // （卡体里 8 例，其中 Card_142 是 B4 里偏差最大的卡）。
            case "xingwei_forward":
                int ringGrid = c.Src.CurrentCardGrid;
                for (int i = 0, ringN = Eval(e.Val, c); i < ringN; i++)
                {
                    ringGrid = GridFunctions.GetNextGrid(c.Src, ringGrid);
                    GridMarkFunctions.AddXingWei(c.Src, ringGrid);
                }
                break;
            default:
                // ⚠ 未识别的 op **绝不静默 no-op** —— 那等于悄悄少算一整个效果，
                // 而且不会有任何迹象（UnknownBuffOps 那套的同类教训）。计数供诊断。
                UnknownOps++;
                break;
            // x.ApplyDamage(target, DamageInfo.Create(...))：**目标可以是自己**（自伤很常见）。
            // 对应原码 skipWoundCheck: true（不走外伤判定）。
            // 原码 DamageInfo.Create 的 skipWoundCheck 默认 false：不写就算击伤。抽取器按原码实参区分两种 op。
            case "damage":
                CombatMath.ApplyDamage(c.Src, c.Side(e.Target),
                    DamageInfo.Create(c.Src, DamageType.Damage, Eval(e.Val, c), skipWoundCheck: false));
                break;
            case "damage_skip":
                CombatMath.ApplyDamage(c.Src, c.Side(e.Target),
                    DamageInfo.Create(c.Src, DamageType.Damage, Eval(e.Val, c), skipWoundCheck: true));
                break;
        }
    }

    /// <summary>把 buff 表达式求成 BuffType。未知/求不出来 → 递增 UnknownBuff 并返回 false（调用方跳过该 op）。</summary>
    private static bool TryResolveBuff(Expr? e, Ctx c, out BuffType buff)
    {
        buff = default;
        if (e is null) return false;
        int v;
        switch (e)
        {
            case BuffName bn:
                if (!Enum.TryParse(bn.Name, out buff)) { c.UnknownBuff++; UnknownBuffOps++; return false; }
                return true;
            case CastBuff cb:
                v = Eval(cb.E, c);
                buff = (BuffType)v;
                return true;
            default:
                v = Eval(e, c);
                buff = (BuffType)v;
                return true;
        }
    }

    private static BuffType ResolveBuff(Expr? e, Ctx c)
        => TryResolveBuff(e, c, out var b) ? b : default;

    internal static int Eval(Expr? e, Ctx c)
    {
        switch (e)
        {
            case null: return 0;
            case Lit l: return l.V;
            case Param p: return c.Cfg.CardOtherParams(c.Card.Id).At(p.I);
            case Field f: return FieldOf(f.Name, c);
            case Var v: return c.Vars.TryGetValue(v.Name, out var x) ? x : 0;
            case Temp t: return TmpOf(t.Side, t.Name, c);
            case BuffName bn: return Enum.TryParse<BuffType>(bn.Name, out var bt) ? (int)bt : 0;
            case CastBuff cb: return Eval(cb.E, c);
            case NextParam np: return c.Src.NextParam(NextRequest(c));   // 玩家选牌参数（与 GetNextRandomValue 共用同一游标）
            // 相邻格 / 队首的卡 id（「用另一张牌」的 id 来源）。取不到就 -1：TriggerCard 收到负数不出手。
            case GridCard g:
            {
                var gs = c.Src;                     // 原码用的都是 src 的格号
                BattleCard? gc2 = g.Which == "prev"
                    ? GridFunctions.PreviousGridCard(gs, gs.CurrentCardGrid)
                    : GridFunctions.NextGridCard(gs, gs.CurrentCardGrid);
                return gc2?.Id ?? -1;
            }
            case DeckTop d:
            {
                var ds = c.Side(d.Side);
                BattleDeck.Item? head = ds.Deck.PeekCard();
                if (head is null) return -1;
                int slot = head.Slot;
                return slot >= 0 && slot < ds.Board.Count ? ds.Board[slot].Id : 0;
            }
            case BuffVal bv: return Enum.TryParse<BuffType>(bv.Buff, out var b2) ? c.Side(bv.Side).GetBuffValue(b2) : 0;
            case Neg n: return -Eval(n.E, c);
            case Arith a:
                int lv = Eval(a.L, c), rv = Eval(a.R, c);
                return a.Op switch
                {
                    '+' => lv + rv,
                    '-' => lv - rv,
                    '*' => lv * rv,
                    '/' => rv == 0 ? 0 : lv / rv,     // C# 整除向零截断
                    _ => 0,
                };
            case Cond cd: return Eval(cd.C, c) != 0 ? Eval(cd.A, c) : Eval(cd.B, c);
            case Not nt: return Eval(nt.E, c) == 0 ? 1 : 0;
            case Pred pd: return EvalPred(pd, c);
            case And an: return Eval(an.L, c) != 0 && Eval(an.R, c) != 0 ? 1 : 0;
            case Or on: return Eval(on.L, c) != 0 || Eval(on.R, c) != 0 ? 1 : 0;   // 短路：右侧可能抽数
            case Cmp cp:
                int cvl = Eval(cp.L, c), cvr = Eval(cp.R, c);
                bool ok = cp.Op switch
                {
                    "gt" => cvl > cvr,
                    "ge" => cvl >= cvr,
                    "lt" => cvl < cvr,
                    "le" => cvl <= cvr,
                    "eq" => cvl == cvr,
                    "ne" => cvl != cvr,
                    _ => false,
                };
                return ok ? 1 : 0;
            case Call cl:
                switch (cl.Name)
                {
                    case "max": return Math.Max(Eval(cl.Args[0], c), Eval(cl.Args[1], c));
                    case "min": return Math.Min(Eval(cl.Args[0], c), Eval(cl.Args[1], c));
                    case "abs": return Math.Abs(Eval(cl.Args[0], c));
                    // ConfigManager.GetTalentConfig(N).otherParams[i] / TalentResonancePanel.GetResonanceTalentConfig(N).otherParams[i]
                    // —— 抽取器把它们改写成 TalentParam_N_i / ResoParam_N_i（见 _PRE_REWRITE）。
                    case "talentparam": return c.Src.Config.TalentOtherParams(Eval(cl.Args[0], c)).At(Eval(cl.Args[1], c));
                    // KeYinCardFunctions.GetTotalKeYinOtherparam(<side>, N, M)：刻印参数表。
                    // 抽取器把 side 编进了名字（`json` 的 strlit 到 sim 会被丢掉，不能承载信息）。
                    case "keyinparam_src": return c.Src.Subs.KeYinOtherparam(c.Src, Eval(cl.Args[0], c), Eval(cl.Args[1], c));
                    case "keyinparam_dst": return c.Src.Subs.KeYinOtherparam(c.Dst, Eval(cl.Args[0], c), Eval(cl.Args[1], c));
                    case "resoparam":   return c.Src.Config.ResonanceOtherParams(Eval(cl.Args[0], c)).At(Eval(cl.Args[1], c));
                    case "int": return Eval(cl.Args[0], c);
                    case "float": return Eval(cl.Args[0], c);
                    // Mathf.CeilToInt/FloorToInt/RoundToInt。整数入参时 ceil/floor/round 都是恒等，
                    // 真正有意义的是**裹着浮点除法**的那支 → 抽取器转成 ceildiv/floordiv/rounddiv。
                    case "ceil": return Eval(cl.Args[0], c);
                    case "floor": return Eval(cl.Args[0], c);
                    case "round": return Eval(cl.Args[0], c);
                    // UnityEngine.Random.Range(lo, hi) —— 真随机区间，取**期望值（中点）**。
                    // 复现不了游戏那一次的掷骰；对「比盘面」这个用途，期望是无偏的
                    // （用下界会系统性低估这些牌）。正解是让 oracle 记录实际掷出的值，待下次采集。
                    case "randrange": return (Eval(cl.Args[0], c) + Eval(cl.Args[1], c)) / 2;
                    case "ceildiv": return CeilDiv(Eval(cl.Args[0], c), Eval(cl.Args[1], c));
                    case "floordiv": return FloorDiv(Eval(cl.Args[0], c), Eval(cl.Args[1], c));
                    case "rounddiv": return CeilDiv(Eval(cl.Args[0], c), Eval(cl.Args[1], c));
                    default:
                        UnknownCalls++;   // 绝不静默返回 0（与 UnknownOps 同一条原则）
                        return 0;
                }
            default: return 0;
        }
    }

    /// <summary>
    /// `Mathf.CeilToInt((float)a / b)` 的**精确**整数等价。
    /// 不能写成 `a / b` 再 ceil：C# 的 `/` 向零截断，`5 / 2 = 2`，ceil 之后还是 2，
    /// 而真值是 `ceil(2.5) = 3` —— 差 1 点，正是那种最难查的偏差。
    /// </summary>
    internal static int CeilDiv(int a, int b)
    {
        if (b == 0) return 0;
        int q = a / b;
        if (a % b != 0 && (a > 0) == (b > 0)) q++;
        return q;
    }

    /// <summary>`Mathf.FloorToInt((float)a / b)` 的精确整数等价（负数下与截断除法不同）。</summary>
    internal static int FloorDiv(int a, int b)
    {
        if (b == 0) return 0;
        int q = a / b;
        if (a % b != 0 && (a > 0) != (b > 0)) q--;
        return q;
    }

    private static int FieldOf(string name, Ctx c) => name switch
    {
        "def" => c.Card.Def,
        "attack" => c.Card.Attack,
        "attackCount" => c.Card.AttackCount,
        "anima" => c.Card.Anima,
        "physique" => c.Card.Physique,
        "rarity" => Meta(c)?.Rarity ?? 0,
        // 本张牌的整 id（部分卡按 cardConfig.id 精确分流）
        "id" => c.Card.Id,
        // 境界 / 剑意：卡上被「按持有者天赋改牌面」改过就用卡上的（卡 19），否则取卡表静态值。
        "level" => c.Card.Level > 0 ? c.Card.Level : (Meta(c)?.Level ?? 0),
        "jianYi" => c.Card.JianYi > 0 ? c.Card.JianYi : (Meta(c)?.JianYi ?? 0),
        // 下面这些来自卡表（BattleCard 不带，只有 JsonBattleConfig 有）。
        // 缺卡表时返回 0 —— 与「该字段本来就是 0」不可区分，所以调用方要靠 missingConfig 在抽取期挡住。
        "guaXiang" => Meta(c)?.GuaXiang ?? 0,
        "hpCost" => Meta(c)?.HpCost ?? 0,
        "randomAttack" => Meta(c)?.RandomAttack ?? 0,
        "cardType" => Meta(c)?.CardType ?? 0,
        "subcategory" => Meta(c)?.Subcategory ?? 0,
        // usecard 之后读到的是**临时牌**的 actionAgain（见 usecard 注释）；否则是本牌卡表的静态值。
        "actionAgain" => c.Vars.TryGetValue(TempActionAgainVar, out var tag) ? tag : (Meta(c)?.ActionAgain == true ? 1 : 0),
        _ => 0,
    };

    /// <summary>
    /// 谓词求值：每个分支都直调 sim 里对应那个**忠实移植过的**判定函数，不重写逻辑。
    /// 载荷有两种：buff 类（hasbuff/wuxing）走 ResolveBuff；其余走 Eval 取整数。
    /// </summary>
    private static int EvalPred(Pred p, Ctx c)
    {
        var s = p.Side == "dst" ? c.Dst : c.Src;
        switch (p.Name)
        {
            case "hasbuff":  return p.Arg is not null && TryResolveBuff(p.Arg, c, out var b1) && s.HasBuff(b1) ? 1 : 0;
            case "wuxing":
            {
                bool _hit = p.Arg is not null && TryResolveBuff(p.Arg, c, out var b2) && WuXingFunctions.CheckWuXing(s, b2);
                // [诊断] 仅在 oracle 诊断模式开着时统计（它在热路径上，生产/吞吐基准里不该付这个成本）。
                if (TraceWuxing && p.Arg is not null && TryResolveBuff(p.Arg, c, out var b2b))
                {
                    string _k = b2b.ToString() + (_hit ? "=TRUE" : "=false");
                    WuxingHits[_k] = WuxingHits.GetValueOrDefault(_k) + 1;
                }
                return _hit ? 1 : 0;
            }
            case "hastalent": return s.HasTalent(Eval(p.Arg, c)) ? 1 : 0;
            case "hasreso":  return s.HasTalentResonance(Eval(p.Arg, c)) ? 1 : 0;
            case "resoeff":  return s.IsTalentResonanceEffective(Eval(p.Arg, c)) ? 1 : 0;
            case "keyin":    return KeYinCardFunctions.HasKeYinType(s, Eval(p.Arg, c)) ? 1 : 0;
            case "debuffcount": return s.GetDebuffCount();
            case "houzhao":  return HouZhaoFunctions.CheckHouZhao(s, c.Card.Id, s.CurrentCardGrid) ? 1 : 0;
            case "xingwei":  return GridMarkFunctions.CheckXingWei(s, s.CurrentCardGrid) ? 1 : 0;
            case "putong":   return CardTypes.IsPuTongGongJi(c.Card.Id) ? 1 : 0;
            case "yigua":    return CardTypes.YiGuaZiJieCheck(s) ? 1 : 0;
            case "hadused":  return s.CurrentCardGrid >= 0 && s.HadUsed(s.CurrentCardGrid) ? 1 : 0;   // card_.cardItem.hadUsed
            case "randnext": return s.GetNextRandomValue(NextRequest(c));
            case "nextparam": return s.NextParam(NextRequest(c));         // GetNextParam：直接出队，不消耗卦象
            // 五行激活计数（10551 / 24684）：种类数 / 层数之和。卡 13 用它决定「再次行动」。
            case "wuxingcount":  return WuXingFunctions.GetWuXingActiveCount(s);
            case "wuxingnumber": return WuXingFunctions.GetWuXingActiveNumber(s);
            case "wuxingindeck": return WuXingFunctions.GetWuXingCountInDeck(s);
            case "zongzicount":  return s.ZongZiBuffCount();
            default:
                // ctype:<牌型> —— 走 CardTypes 里同名谓词
                if (p.Name.StartsWith("ctype:", StringComparison.Ordinal))
                {
                    int cid = c.Card.Id;
                    return p.Name switch
                    {
                        "ctype:JianZhen" => CardTypes.IsJianZhen(s, cid) ? 1 : 0,
                        "ctype:LingJian" => CardTypes.IsLingJian(s, cid) ? 1 : 0,
                        "ctype:KuangJian" => CardTypes.IsKuangJian(s, cid) ? 1 : 0,
                        "ctype:YunJian" => CardTypes.IsYunJian(s, cid) ? 1 : 0,
                        "ctype:BengQuan" => CardTypes.IsBengQuan(s, cid) ? 1 : 0,
                        _ => 0,
                    };
                }
                return 0;
        }
    }

    private static JsonBattleConfig.CardMeta? Meta(Ctx c)
        => (c.Cfg as JsonBattleConfig)?.Card(c.Card.Id);

    private static int TmpOf(string side, string name, Ctx c)
    {
        var who = c.Side(side);
        return name switch
        {
            "anima" => who.Anima,
            "hp" => who.Hp,
            "def" => who.Def,
            "maxHp" => who.MaxHp,
            "physique" => who.TiPo,
            // 命元（`characterUI.tempLife`，抽取器改写成 battleTempData.tempLife）。
            // ⚠ 早先没这一条 → 落进 `_ => 0` → `if (tempLife <= 0)` **恒真**，
            //   「命元耗尽即死」开局就成立（卡 99000105 裂虚界元枪第一手把对手 SetHp(0)）。
            "tempLife" => who.TempLife,
            _ => UnknownTempField(name),
        };
    }

    /// <summary>
    /// 未识别的 `battleTempData.<字段>`：**不静默返回 0**（见文件头那条铁律 —— 一个悄悄变成 0 的
    /// 门槛比一个抛出来的错误难查一百倍）。计数供诊断，值仍返回 0 以免整卡崩掉。
    /// </summary>
    private static int UnknownTempField(string name)
    {
        UnknownTemps[name] = UnknownTemps.GetValueOrDefault(name) + 1;
        return 0;
    }
}
