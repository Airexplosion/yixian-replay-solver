using System;
using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;
using Yx.BattleSim.Effects;

namespace Yx.BattleSim.Model;

/// <summary>
/// 一方在一场战斗中的可变工作态，对应游戏 <c>BattleTempData</c> + <c>BattleCharacter</c> 的战斗相关字段。
///
/// 忠实移植说明：游戏战斗是「可变状态机」，复现结果必须镜像 ModifyHp/ModifyDef/buff 的原地改。
/// 取舍：每场 <see cref="Combatant"/> 全新构造、互不共享 → 并发按场隔离、无跨线程共享可变态；
/// 结算器对外是纯函数（<see cref="BattleInput"/> → <see cref="BattleOutcome"/>）。此处忠实于引擎优先于通用不可变约定。
///
/// 复杂的 ModifyHp / ModifyMaxHp / ModifyDef / AfterHpModifyEffect / OnHit 在伙伴文件
/// CombatantHp.cs / CombatantDef.cs 里忠实移植（按原分支）；本文件只放状态与基础原语。
/// </summary>
public sealed partial class Combatant
{
    public required string Uid { get; init; }

    public int Hp;
    public int MaxHp;
    public int Def;
    public int Anima;   // 灵气
    public int TiPo;    // 体魄

    /// <summary>meta 生命（回合外的血，胜负后按 CalLifeDamage 扣）。</summary>
    public int Life;

    /// <summary>已解锁的格数（游戏 lastRoundData.unlockGrids）。牌组长度与环形回绕都按它。缺省 8。</summary>
    public int UnlockGrids = 8;

    /// <summary>本回合摆上的牌（按格子顺序）。**逐场可变**：升级/降级/复制/变形会就地换掉某一格。</summary>
    public required IReadOnlyList<BattleCard> Board { get; init; }

    /// <summary>
    /// 运行期换掉某一格的牌（升级 / 降级 / 连音复制 / 变形）。
    /// 原码是直接改写常驻 CardItem 的 <c>cardInfo.id</c>，所以**是永久的**（yisim 同样写回 cards[idx]）。
    /// <see cref="BattleResolver.ToCombatant"/> 把输入盘面拷成 <c>List</c>，所有读 <see cref="Board"/> 的地方
    /// （牌型谓词 / 相邻格 / 牌库统计）就会一起看到新牌。
    /// </summary>
    public void ReplaceCard(int grid, BattleCard card)
    {
        // 卡 19 澄心剑胚：换上场（开局 / 升级 / 变牌）时按天赋改牌面（原码 RefreshSpecialCard → Card_19.UpdateCardInfo）。
        if (Board is List<BattleCard> list && grid >= 0 && grid < list.Count) list[grid] = Effects.Card19Functions.Apply(this, card);
    }

    /// <summary>炼化（刻印）卡 id 列表：id%10000 为类型，KeYinOtherParams(id) 为其参数。</summary>
    public IReadOnlyList<int> BattleKeYinCards = [];

    /// <summary>下一张要打出的刻印牌序号（原码 battleTempData.currentUsingKeYinCardIndex；只增不回绕）。</summary>
    public int CurrentUsingKeYinCardIndex;

    /// <summary>
    /// 玩家选牌参数队列。**与对手共享同一条**（游戏里它挂在 battleExecuter 上，是全局单队列，
    /// 10738 的 <c>battleParamsQueue.Dequeue()</c>）—— 见 <see cref="BattleParamQueue"/>。
    /// </summary>
    public BattleParamQueue Params = BattleParamQueue.Empty;

    /// <summary>battleTempData.actionAgainPerRound：本回合允许的再行动次数（初始 1，刻印山风会减）。</summary>
    public int ActionAgainPerRound = 1;

    /// <summary>取下一个玩家参数（对应游戏的 <c>battleParamsQueue.Dequeue()</c>）。取空返回 -1。</summary>
    /// <summary>
    /// TryCostAnima（原码 10961）：「最多耗 maxCost 灵气」—— 有多少耗多少（上限 maxCost），返回实际耗的量。
    /// 共鸣 50 + 天赋 153：不够的部分改为按天赋 153 的倍率扣血（共鸣 50 免体魄），付得起就视作耗满。
    /// ⚠ 有副作用（扣灵气），卡效果里只能调用一次；不要做成数据驱动的谓词（同一守卫下多条 op 会重复扣）。
    /// </summary>
    public int TryCostAnima(int maxCost)
    {
        if (maxCost <= 0) return 0;
        int num = Anima;
        if (num > maxCost) num = maxCost;
        ModifyAnima(-num);
        if (HasTalentResonance(50) && HasTalent(153) && num < maxCost)
        {
            int hpCost = (maxCost - num) * Config.TalentOtherParams(153).At(0);
            int tiPoCost = (maxCost - num) * Config.TalentOtherParams(153).At(1);
            if (HasTalentResonance(50)) tiPoCost = 0;
            if (hpCost < Hp && tiPoCost <= TiPo)
            {
                ModifyHp(-hpCost, canRevive: false, isCost: true);
                if (tiPoCost > 0) ModifyTiPo(-tiPoCost);
                num = maxCost;
            }
        }
        return num;
    }

    /// <summary>
    /// 没有真实参数队列（摆牌求解）：按消费点声明的请求估参数（<see cref="Effects.ParamEstimator"/>），不读队列。
    /// </summary>
    public bool EstimateParams;

    /// <summary>
    /// 取下一个玩家参数（GetNextParam，10738）。真参数模式出队（请求被忽略，行为与加请求之前逐位一致）；
    /// 估值模式按 <paramref name="req"/> 估。所有生产路径的消费点都必须带请求。
    /// </summary>
    public int NextParam(in ParamRequest req)
    {
        int v = EstimateParams ? Effects.ParamEstimator.Estimate(this, req) : Params.Next();
        ParamProbe?.Invoke(this, req, v);
        if (ParamTrace is not null)
        {
            var st = new System.Diagnostics.StackTrace(1, false);
            var chain = string.Join("<", System.Linq.Enumerable.Take(st.GetFrames(), 4)
                .Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name));
            ParamTrace($"{Uid} param={v} site={req.Site} [{chain}]");
        }
        return v;
    }

    /// <summary>不声明语义的取值：只给测试 / 诊断用（估值模式下返回 -1）。</summary>
    public int NextParam() => NextParam(ParamRequest.None);

    /// <summary>
    /// 诊断用：每次取参数后回调（取值方, 请求, 实际取到的值）。对拍工具用它在真参数轨迹上同时算估值器的值，逐次比对。生产路径保持 null。
    /// </summary>
    public static Action<Combatant, ParamRequest, int>? ParamProbe;

    /// <summary>诊断用：非空时每次抽 battleParams 都回调（定位「多抽 / 少抽一次导致整串错位」）。生产路径保持 null。</summary>
    public static Action<string>? ParamTrace;

    /// <summary>
    /// GetZongZiBuffCount（24775）：四种「粽子」buff 的**层数之和** —— 内伤(100)/恢复(248)/食欲(392)/食滞(393)。
    /// 卡面写作「每有1层[内伤]、[恢复]、[食欲]、[食滞]…」，三张粽子卡（90/108/303 系）都靠它。
    /// </summary>
    public int ZongZiBuffCount()
        => GetBuffValue(BuffType.NeiShang) + GetBuffValue(BuffType.HuiFu)
         + GetBuffValue(BuffType.ShiYu) + GetBuffValue(BuffType.ShiZhi);

    /// <summary>
    /// GetNextRandomValue（10751）：**不是随机数**——先按手上的「卦象」类资源扣一层（紫芒星宝→星力 / 卦象 / 共鸣71+灵气），
    /// 再返回玩家预选的参数（battleParamsQueue）。所以战斗里的所谓随机全是确定性输入。
    /// 扣到了资源时，服务器写进队列的是**最优值**（「耗灵气代替卦象来让随机结果取最优」）：
    /// 估值模式据此把请求标成 <see cref="ParamRequest.Lucky"/>；真参数模式照旧出队。
    /// </summary>
    public int GetNextRandomValue(in ParamRequest req, bool useGuaXiang = true)
    {
        bool lucky = false;
        if (useGuaXiang)
        {
            if (HasBuff(BuffType.ZiMangXingBao) && GetBuffValue(BuffType.XingLi) > 0)
            {
                ModifyBuffValue(BuffType.XingLi, -1);
                lucky = true;
            }
            else if (HasBuff(BuffType.GuaXiang))
            {
                ModifyBuffValue(BuffType.GuaXiang, -1);
                ModifyBuffValue(BuffType.GuaXiangShengXiaoCiShu, 1);
                lucky = true;
            }
            else if (IsTalentResonanceEffective(71) && CheckTalentResonanceTempFlag(71) && Anima > 0)
            {
                ModifyAnima(-1);
                lucky = true;
            }
        }
        return lucky ? NextParam(req with { Lucky = true }) : NextParam(req);
    }

    /// <summary>对手（reflect / 跨方效果 / defaultOpponentTarget）。由结算器组装时互相设定。</summary>
    public Combatant Opponent = null!;

    /// <summary>配置数值来源。</summary>
    public IBattleConfig Config = StubBattleConfig.Empty;

    /// <summary>子系统接缝：默认把炼化(P4)做实、其余中性（五行/共鸣/牌库/debuff 待 P5）。</summary>
    public ISubsystems Subs = DefaultSubsystems.Instance;

    /// <summary>当前正在出的牌的 baseId（部分 buff 分支按当前牌判定；0 表示无）。</summary>
    public int CurrentCardBaseId;

    /// <summary>当前正在出的牌的**牌名**（雷霆心法等按名 contains 判定；空串表示无/无数据）。</summary>
    public string CurrentCardName = "";

    /// <summary>当前正在出的牌的**完整 id**（部分分支按 cardConfig.id 精确判定；0 表示无）。</summary>
    public int CurrentCardId;

    /// <summary>当前正在出的牌在牌桌上的**格位**（出牌前钩子/相邻效果按它判定；-1 表示无）。</summary>
    public int CurrentCardGrid = -1;

    /// <summary>
    /// 诊断用：非空时每次 <see cref="ModifyHp"/> 生效（delta≠0）都回调一次，附带调用栈前几帧。
    /// 只为定位「血量对不上是哪条规则干的」，不参与结算 —— 生产路径保持 null。
    /// </summary>
    public static Action<string>? HpTrace;

    /// <summary>
    /// 当前这张牌打完是否可再行动一次（= BattleCard.ActionAgain 的副本；卡效果可在出牌程中改写它，
    /// 对应游戏里对 cardConfig.actionAgain 的写入）。放在战斗工作态上，避免写脏调用方复用的 BattleCard。
    /// </summary>
    public bool CurrentCardActionAgain;

    /// <summary>
    /// 出牌前钩子把当前格标记为出局（原码 `cardItem.skip = true`：汲然咒印 / 使用下张牌后消耗 / 描述带 [耗尽]，均限非临时牌）。
    /// 由 BattleResolver 在出牌收尾时并入 <c>BattleDeck.MarkUsed</c>；每次出牌开头清零。
    /// </summary>
    public bool CurrentCardSkip;

    /// <summary>当前出的牌是否「狂剑」（KuangJianLingShi 吸血分支判定用）。</summary>
    public bool CurrentCardIsKuangJian;

    /// <summary>当前牌的牌型标志（部分加攻分支按牌型判定；结算器出牌时置，默认全 false）。</summary>
    public bool CurrentCardIsBengQuan;
    public bool CurrentCardIsPuTong;
    public bool CurrentCardIsLingJian;

    /// <summary>
    /// 每格「这张牌本次战斗是否已经出过」——对应游戏 <c>CardItem.hadUsed</c>。
    /// 开局全 false（游戏在摆盘时 <c>cardItem.hadUsed = false</c>），**在 OnAfterExecuted 里置 true**，
    /// 此后整场为 true。后招（<see cref="Effects.HouZhaoFunctions.CheckHouZhao"/>）等 30+ 处靠它判「首次使用」。
    /// </summary>
    private bool[]? _gridUsed;

    /// <summary>该格的牌是否已经出过（越界/未初始化返回 false）。</summary>
    public bool HadUsed(int grid) => _gridUsed is not null && grid >= 0 && grid < _gridUsed.Length && _gridUsed[grid];

    /// <summary>置该格的「已出过」标志（对应 <c>cardItem.hadUsed = …</c>）。</summary>
    public void SetHadUsed(int grid, bool used)
    {
        if (grid < 0 || grid >= Board.Count) return;
        _gridUsed ??= new bool[Board.Count];
        _gridUsed[grid] = used;
    }

    /// <summary>
    /// 角色 id（对应 <c>playerData.publicData.characterId</c>）。目前只被 <c>脉(Min)</c> 的正负号分支用到
    /// （4000003 回血，其余扣血）；0 = 未采集 → 走默认（扣血）支路。
    /// </summary>
    public int CharacterId;

    /// <summary>已死亡标志（OnDead 置）。</summary>
    public bool IsDead;

    /// <summary>
    /// getCardTotalOtherparamInDeck：牌库里 baseId 命中者，<paramref name="paramIndex"/> &lt; 0 时数张数，
    /// 否则累加其 otherParams[<paramref name="paramIndex"/>]。忠实移植 BattleCharacter 同名方法。
    /// </summary>
    public int CardTotalOtherparamInDeck(int cardBaseId, int paramIndex = -1)
    {
        int n = 0;
        foreach (var c in Board)
        {
            if (CardTypes.BaseId(c.Id) != cardBaseId) continue;
            n += paramIndex < 0 ? 1 : Config.CardOtherParams(c.Id).At(paramIndex);
        }
        return n;
    }

    /// <summary>
    /// 本场牌组队列 + 各格出局（skip）状态。**每场必须新建** —— 出牌会就地改写格子，
    /// 跨场复用会把上一场的 skip 带过来（这也是不能把它做成 Board 那样的共享不可变输入的原因）。
    /// </summary>
    public BattleDeck Deck { get; init; } = new();

    private Dictionary<BuffType, int>? _buffs;

    /// <summary>诊断用：所有非零 buff（trace 的 YX_TRACE_BUFFS=1 时打印）。</summary>
    public string DumpBuffs()
    {
        if (_buffs is null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var kv in _buffs)
            if (kv.Value != 0) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
        return sb.ToString();
    }
    private readonly IReadOnlyCollection<int>? _fateStrategies;
    private readonly IReadOnlyCollection<int>? _talents;

    public Combatant(IReadOnlyCollection<int>? fateStrategies = null, IReadOnlyCollection<int>? talents = null)
    {
        _fateStrategies = fateStrategies is { Count: > 0 } ? fateStrategies : null;
        _talents = talents is { Count: > 0 } ? talents : null;
    }

    // ---- buff 原语 ----

    /// <summary>
    /// 是否持有某 buff。忠实移植 BattleCharacter.HasBuff：<c>LianYun</c>（练云）会被天赋/共鸣改写成「视同持有」。
    /// 注：原码判 <c>buffs.ContainsKey</c>（值为 0 也算持有），本 sim 判「值 != 0」——更符合直觉，且 oracle 已验证。
    /// </summary>
    public bool HasBuff(BuffType buff)
    {
        if (buff == BuffType.LianYun
            && (HasTalent(14) || (HasTalentResonance(10) && CheckTalentResonanceTempFlag(10)) || HasBuff(BuffType.LongMaJingShen)))
            return true;
        return _buffs is not null && _buffs.TryGetValue(buff, out var v) && v != 0;
    }

    /// <summary>
    /// 读 buff 层数。
    /// ⚠ **体魄特殊**：本 sim 把它存在 <see cref="TiPo"/> 字段里（见 <see cref="ModifyTiPo"/> 的注释），
    /// <c>BuffType.TiPo</c> 从不被写入 —— 于是 <c>GetBuffValue(BuffType.TiPo)</c> 会恒 0。
    /// 而卡体里到处拿它当**数值**用（`buffval src TiPo`），最典型是卡 10000066 返璞归真：
    /// <c>delta = TiPo / otherParams[0]</c> 恒 0 → 整张牌白挂、普攻永远拿不到那 +1 攻
    /// （完整批次：real `L=36 R=0` / sim `L=9 R=7`）。这里回落到字段，一次修好所有读点。
    /// </summary>
    public int GetBuffValue(BuffType buff)
    {
        if (buff == BuffType.TiPo) return TiPo;
        return _buffs is not null && _buffs.TryGetValue(buff, out var v) ? v : 0;
    }

    public void AddBuff(BuffType buff, int value)
    {
        _buffs ??= new Dictionary<BuffType, int>();
        _buffs.TryGetValue(buff, out var cur);
        _buffs[buff] = cur + value;
    }

    // ModifyBuffValue（805 行的写入联动链）在伙伴文件 CombatantBuffs.cs 里忠实移植。

    /// <summary>SetBuffValue：直接置值。</summary>
    public void SetBuffValue(BuffType buff, int value)
    {
        _buffs ??= new Dictionary<BuffType, int>();
        _buffs[buff] = value;
    }

    // ---- debuff（忠实移植 BattleCharacter.GetDebuffList / GetDebuffCount / RemoveAllDebuff）----
    // category 来自配置表 BuffConfig（ConfigManager.s_BuffCategoryMap），不是硬编码名单。

    /// <summary>GetDebuffList：category==Negative 且层数 &gt; 0 的 buff，按枚举值排序。</summary>
    public List<BuffType> GetDebuffList()
    {
        var list = new List<BuffType>();
        if (_buffs is null) return list;
        foreach (var kv in _buffs)
            if (kv.Value > 0 && Config.BuffCategoryOf(kv.Key) == BuffCategory.Negative) list.Add(kv.Key);
        list.Sort();
        return list;
    }

    /// <summary>GetDebuffCount：所有负面 buff 的**层数之和**（不是种类数）。</summary>
    public int GetDebuffCount()
    {
        int n = 0;
        foreach (var b in GetDebuffList()) n += GetBuffValue(b);
        return n;
    }

    /// <summary>RemoveAllDebuff：移除全部负面 buff（每种最多 count 层；count=-1 为全清），返回移除总层数。</summary>
    public int RemoveAllDebuff(int count = -1)
    {
        int total = 0;
        foreach (var b in GetDebuffList())
        {
            int v = GetBuffValue(b);
            int take = count == -1 || count > v ? v : count;
            ModifyBuffValue(b, -take);
            total += take;
        }
        return total;
    }

    public void RemoveBuff(BuffType buff) => _buffs?.Remove(buff);

    // ---- 仙命 / 天赋 ----

    public bool HasFateStrategy(int id) => _fateStrategies is not null && _fateStrategies.Contains(id);

    public bool HasTalent(int id) => _talents is not null && _talents.Contains(id);

    /// <summary>本方的天赋列表（出牌前钩子的「每张牌」天赋循环要遍历它）。</summary>
    public IReadOnlyCollection<int> Talents => _talents ?? (IReadOnlyCollection<int>)Array.Empty<int>();

    /// <summary>
    /// 上一回合留下的**永久** buff（游戏写进 playerData，跨回合/跨场保留；本 sim 每场独立，
    /// 由调用方（AI 摆牌层）在跨轮时喂进来；空表则视作 0）。
    /// </summary>
    public IReadOnlyDictionary<BuffType, int> LastRoundPermanentBuffs = new Dictionary<BuffType, int>();

    /// <summary>GetLastRoundPermanentBuffValue：读上回合的永久 buff 值。</summary>
    public int GetLastRoundPermanentBuffValue(BuffType buff)
        => LastRoundPermanentBuffs.TryGetValue(buff, out var v) ? v : 0;

    // ---- 天赋共鸣（E）----
    // 游戏里共鸣是**单个选中值**（privateData.talentResonanceData.selectionData.selected），不是列表。

    /// <summary>选中的共鸣 id（0 = 未选）。</summary>
    public int SelectedResonance;

    /// <summary>当前回合数（GetCurrentRound ← battleResult.round）。IsTalentResonanceEffective 的门槛用。</summary>
    public int CurrentRound;

    /// <summary>境界（Level 枚举值，0 = InvalidLevel）。共鸣的 effectLevel 门槛用。</summary>
    public int Level;

    /// <summary>
    /// 战斗内共鸣位标记（battleTempData.resonanceTalentFlags，按 id 存位图）。
    /// 与「永久标记」（写回 playerData，跨场保留）分开——本 sim 每场独立，只保留战斗内那份。
    /// </summary>
    private Dictionary<int, int>? _resonanceFlags;

    /// <summary>CheckTalentResonanceTempFlag：第 bit 位是否置起。</summary>
    public bool CheckTalentResonanceTempFlag(int talentId, int bit = 0)
        => _resonanceFlags is not null && _resonanceFlags.TryGetValue(talentId, out var v) && (v & (1 << bit)) != 0;

    /// <summary>SetTalentResonanceTempFlag：置/清第 bit 位。</summary>
    public void SetTalentResonanceTempFlag(int talentId, bool value, int bit = 0)
    {
        _resonanceFlags ??= new Dictionary<int, int>();
        _resonanceFlags.TryGetValue(talentId, out var cur);
        _resonanceFlags[talentId] = value ? cur | (1 << bit) : cur & ~(1 << bit);
    }

    /// <summary>RemoveTalentResonanceTempFlag：整条清掉（回合末回收）。</summary>
    public void RemoveTalentResonanceTempFlag(int talentId) => _resonanceFlags?.Remove(talentId);

    /// <summary>HasTalentResonance：是否选中的就是这个共鸣（不问门槛）。</summary>
    public bool HasTalentResonance(int talentId) => talentId != 0 && SelectedResonance == talentId;

    /// <summary>
    /// IsTalentResonanceEffective：选中 + 过门槛（effectRound / effectLevel）。
    /// effectRound=0 表示不限回合；effectLevel=0（InvalidLevel）表示不限境界。
    /// </summary>
    public bool IsTalentResonanceEffective(int talentId)
    {
        if (!HasTalentResonance(talentId)) return false;
        var (effectRound, effectLevel) = Config.ResonanceGate(talentId);
        if (effectRound > 0 && effectRound > CurrentRound) return false;
        if (effectLevel > 0 && effectLevel > Level) return false;
        return true;
    }

    /// <summary>
    /// 灵气增减：忠实移植 <c>BattleCharacter.ModifyAnima</c>（剥浮字/特效/音效，保留全部数值分支）。
    /// 前半段是「加灵气前」的修正（倍数/替代/衰减），落值后加灵气还会触发一大串「加灵气时…」联动；
    /// 扣灵气（animaDelta&lt;0）只走前半段与落值，不进后半段。
    /// </summary>
    public void ModifyAnima(int animaDelta)
    {
        if (animaDelta == 0) return;

        if (animaDelta > 0 && Subs.IsTalentResonanceEffective(this, 77) && !Subs.CheckTalentResonanceTempFlag(this, 77))
        {
            Subs.SetTalentResonanceTempFlag(this, 77, true);
            animaDelta += Config.ResonanceOtherParams(77).At(0);
        }
        if (animaDelta > 0 && GetBuffValue(BuffType.XiaCiJiaLingQiDuoJia) > 0)
        {
            animaDelta += GetBuffValue(BuffType.XiaCiJiaLingQiDuoJia);
            RemoveBuff(BuffType.XiaCiJiaLingQiDuoJia);
        }
        if (HasBuff(BuffType.BaBaoZaLiangZongLingQi) && animaDelta > 0)
        {
            animaDelta += GetBuffValue(BuffType.BaBaoZaLiangZongLingQi);
            ModifyHp(GetBuffValue(BuffType.BaBaoZaLiangZongShengMing));
        }
        if (HasBuff(BuffType.QiCaiLingHe) && animaDelta > 0) animaDelta *= GetBuffValue(BuffType.QiCaiLingHe);
        if (animaDelta > 0 && GetBuffValue(BuffType.XiaCiJiaLingQiJiaBei) > 0)
        {
            animaDelta *= 2;
            RemoveBuff(BuffType.XiaCiJiaLingQiJiaBei);
        }
        if (animaDelta > 0 && GetBuffValue(BuffType.JiaLingQiJianBan) > 0)
            animaDelta = (int)MathF.Floor(animaDelta / 2f);
        // 天赋204：加灵气改为加体魄（带共鸣137 时不转），且不再加灵气。
        if (animaDelta > 0 && HasTalent(204) && !Subs.HasTalentResonance(this, 137))
        {
            ModifyTiPo(animaDelta);
            animaDelta = 0;
        }
        // 灵气改为星力：把（封顶后的）灵气转成星力。
        if (animaDelta > 0 && HasBuff(BuffType.LingQiGaiWeiXingLi))
        {
            int bv = GetBuffValue(BuffType.LingQiGaiWeiXingLi);
            int take = animaDelta > bv ? bv : animaDelta;
            ModifyBuffValue(BuffType.LingQiGaiWeiXingLi, -take);
            ModifyBuffValue(BuffType.XingLi, take);
        }
        // 扣灵气：实际扣掉多少 → 加防 / 加剑气。
        if (animaDelta < 0)
        {
            if (Anima > 0 && GetBuffValue(BuffType.YuLingXinFaHaoLingQiShiJiaFang) > 0)
            {
                int n = Anima + animaDelta >= 0 ? -animaDelta : Anima;
                ModifyDef(n * GetBuffValue(BuffType.YuLingXinFaHaoLingQiShiJiaFang));
            }
            if (Anima > 0 && GetBuffValue(BuffType.BaiNiaoYeYingJue) > 0)
            {
                int n = Anima + animaDelta >= 0 ? -animaDelta : Anima;
                ModifyBuffValue(BuffType.JianQi, n * GetBuffValue(BuffType.BaiNiaoYeYingJue));
            }
        }

        Anima += animaDelta;
        if (Anima < 0) Anima = 0;

        if (animaDelta > 0)
        {
            ModifyBuffValue(BuffType.JiLuJiaGuoLingQi, animaDelta);
            ModifyBuffValue(BuffType.JiaLingQiCiShu, 1);
        }
        if (animaDelta <= 0) return;

        // ---- 加灵气联动（顺序照原码）----
        if (HasBuff(BuffType.JingQiXinFa)) ModifyHp(animaDelta * GetBuffValue(BuffType.JingQiXinFa));
        if (HasBuff(BuffType.YuLingQu)) ModifyDef(animaDelta * GetBuffValue(BuffType.YuLingQu));
        if (HasBuff(BuffType.YuLingXinFaJiaLingQiShiJiaFang))
            ModifyDef(animaDelta * GetBuffValue(BuffType.YuLingXinFaJiaLingQiShiJiaFang));
        if (HasBuff(BuffType.MengLiangYiJiaShengMing)) ModifyHp(animaDelta * GetBuffValue(BuffType.MengLiangYiJiaShengMing));
        if (HasBuff(BuffType.MengLiangYiJiaFang)) ModifyDef(animaDelta * GetBuffValue(BuffType.MengLiangYiJiaFang));
        if (HasBuff(BuffType.JiaLingQiShiJiaFang)) ModifyDef(animaDelta * GetBuffValue(BuffType.JiaLingQiShiJiaFang));
        if (HasBuff(BuffType.JinQiZong)) ModifyHp(animaDelta * GetBuffValue(BuffType.JinQiZong));
        if (HasBuff(BuffType.LingGuaShu))
            ModifyBuffValue(BuffType.GuaXiang, animaDelta * GetBuffValue(BuffType.LingGuaShu));
        if (HasBuff(BuffType.XingYueQianKunShan))
            Combat.CombatMath.ApplyDamage(this, Opponent,
                DamageInfo.Create(this, DamageType.Damage, animaDelta * GetBuffValue(BuffType.XingYueQianKunShan), skipWoundCheck: true));
        if (HasBuff(BuffType.YeDuZhiYin))
        {
            ModifyBuffValue(BuffType.YeDuZhiYin, -1);
            ModifyBuffValue(BuffType.ExActionAgain, 1);
        }
        if (HasTalent(47) && !HasBuff(BuffType.JianLingJiYing))
        {
            int need = Config.TalentOtherParams(47).At(0);
            if (animaDelta >= need)
            {
                ModifyBuffValue(BuffType.ExActionAgain, 1);
                ModifyBuffValue(BuffType.JianLingJiYing, 1);
            }
        }
        if (HasTalent(195) && !HasBuff(BuffType.LingGuaZiYanYiChuFa))
            ModifyBuffValue(BuffType.LingGuaZiYanYiChuFa, 1);
        if (HasTalent(261))
        {
            ModifyMaxHp(Config.TalentOtherParams(261).At(0));
            ModifyHp(Config.TalentOtherParams(261).At(1));
        }
        if (HasBuff(BuffType.WuYouLingNiang))
        {
            ModifyBuffValue(BuffType.WuYouLingNiang, -1);
            int n = Config.FateOtherParams(407).At(0);
            ModifyMaxHp(n);
            ModifyHp(n);
        }
        if (HasTalent(267))
        {
            int cap = Config.TalentOtherParams(267).At(0);
            if (GetBuffValue(BuffType.FengLingDuanQuJiShu) < cap)
            {
                int d = Math.Min(animaDelta, cap - GetBuffValue(BuffType.FengLingDuanQuJiShu));
                ModifyBuffValue(BuffType.FengLingDuanQuJiShu, d);
                ModifyTiPo(d);
            }
        }
        if (HasBuff(BuffType.FengLingZhanYi))
        {
            int bv = GetBuffValue(BuffType.FengLingZhanYi);
            RemoveBuff(BuffType.FengLingZhanYi);
            ModifyBuffValue(BuffType.ShenFa, bv);
        }
    }

    /// <summary>
    /// 体魄增减。忠实移植 11589 <c>BattleCharacter.ModifyTiPo</c>。
    ///
    /// **核心：体魄涨会连带抬最大生命、并按「超出体魄上限的部分」回等量血** —— 这是「体魄能当血用」的
    /// 全部来源。漏掉它，纯炼体卡就会少算几十点血：oracle <c>b5_205_0</c> 的 real L=44 / sim L=4，
    /// 差的 40 正是 205 每轮 +10 体魄 × 4 轮回出来的。
    ///
    /// 依据（先 yisim 后反编译）：yisim <c>gamestate.js increase_idx_physique</c> 里
    /// <c>increase_idx_max_hp(amt)</c> + 按 <c>max_physique</c> 回血；反编译 11589-11655 印证：
    /// <c>ModifyMaxHp(delta, showText: false)</c>，随后 <c>TiPo &gt; TiPoShangXian</c> 时
    /// <c>num3 = min(delta, TiPo - TiPoShangXian)</c> 再 <c>ModifyHp(num3)</c>。
    /// 本 sim 以 <see cref="TiPo"/> 字段为准（<c>BuffType.TiPo</c> 不另存），体魄上限走 buff（默认 0）。
    /// </summary>
    public void ModifyTiPo(int delta)
    {
        if (delta == 0) return;

        if (Subs.IsTalentResonanceEffective(this, 56))
            delta += Config.ResonanceOtherParams(56).At(0);

        TiPo += delta;
        if (delta > 0) ModifyBuffValue(BuffType.BenChangZhanDouTiPoJiShu, delta);

        if (HasTalent(184) && delta > 0)
            ModifyDef(Math.Min(delta, Config.TalentOtherParams(184).At(1)));

        if (HasFateStrategy(163) && delta > 0 && !HasBuff(BuffType.ChanXinJuLingYiChuFa))
        {
            ModifyBuffValue(BuffType.ChanXinJuLingYiChuFa, 1);
            ModifyAnima(1);
        }

        // 体魄涨 = 最大生命同涨（无条件，减体魄时也降）。
        ModifyMaxHp(delta);

        if (delta > 0 && TiPo > GetBuffValue(BuffType.TiPoShangXian))
        {
            int room = TiPo - GetBuffValue(BuffType.TiPoShangXian);
            int heal = Math.Min(delta, room);
            if (GetBuffValue(BuffType.DuanGuPlusHp) > 0)
                ModifyHp(heal * GetBuffValue(BuffType.DuanGuPlusHp));
            if (GetBuffValue(BuffType.DuanGuPlusDmg) > 0)
                CombatMath.ApplyDamage(this, Opponent, DamageInfo.Create(
                    this, DamageType.ReflectDamage, heal * GetBuffValue(BuffType.DuanGuPlusDmg), skipWoundCheck: true));
            if (Subs.IsTalentResonanceEffective(this, 82)) heal *= 2;
            ModifyHp(heal);
            if (Subs.HasKeYinType(this, 107) && !HasBuff(BuffType.KeYinFaXiang))
            {
                ModifyBuffValue(BuffType.KeYinFaXiang, 1);
                ModifyBuffValue(BuffType.JiaGong, Subs.KeYinOtherparam(this, 107, 0));
                ModifyBuffValue(BuffType.TiPoShangXian, Subs.KeYinOtherparam(this, 107, 1));
            }
        }
    }

    // ---- 仙命(P3)所需的回合外 / 牌桌 数据钩子（默认中性；由结算器/上层按真实局面填）----

    /// <summary>上一回合的修为（<c>lastRoundData.exp</c>；仙命 397 / 卡 43 比的就是它）。</summary>
    public int LastRoundExp;

    /// <summary>
    /// **当前**修为（<c>characterUI.exp</c>）：开局 = 上一回合修为，局内由 <see cref="ModifyTempExp"/> 增减（卡 67 / 69）。
    /// 卡 323 / 1000054、刻印 22 读的是它。
    /// </summary>
    public int Exp;

    /// <summary>ModifyTempExp（原码 11676）：改当前修为，不低于 0。</summary>
    public void ModifyTempExp(int delta)
    {
        if (delta == 0) return;
        Exp += delta;
        if (Exp < 0) Exp = 0;
    }

    /// <summary>玩家宗门 / 职业（<c>publicData.sect / career</c>，0 = 无）：卡 67「下一格与自己同宗门或同职业」按它判。</summary>
    public int Sect, Career;

    /// <summary>上一回合的手牌（<c>lastRoundData.handCards</c>，卡 id）。仙命 27 / 384、天赋 220、共鸣 99 / 128 等读它。</summary>
    public IReadOnlyList<int> LastRoundHandCards = Array.Empty<int>();

    /// <summary>
    /// 开局盘面的卡 id（<c>privateData.usedCards</c>：开战时从 lastRoundData.usedCards 复制，局内换牌不改它）。
    /// </summary>
    public IReadOnlyList<int> UsedCardsAtStart = Array.Empty<int>();

    /// <summary>上一回合手牌数（仙命 27 触发条件等）。</summary>
    public int LastRoundHandCardCount => LastRoundHandCards.Count;

    private HashSet<int>? _fateSwitchUsed;

    /// <summary>上一回合保留的永久 buff 值（护身法宝/保存卦象 等）。与 <see cref="GetLastRoundPermanentBuffValue"/> 同一份数据。</summary>
    public int LastRoundPermanentBuff(BuffType buff) => GetLastRoundPermanentBuffValue(buff);

    /// <summary>上一回合的天赋临时数据（<c>lastRoundData.talentTempDatas</c>：天赋 id → 值；免死 / 抵伤次数等）。</summary>
    public IReadOnlyDictionary<int, int> TalentTempDatas = new Dictionary<int, int>();

    /// <summary>共鸣的**永久**标记位（<c>publicData.resonanceTalentFlags</c>：共鸣 id → 按位存的标记）。</summary>
    public IReadOnlyDictionary<int, int> ResonancePermanentFlags = new Dictionary<int, int>();

    /// <summary>
    /// 天赋自带数据（<c>publicData.talentDatas</c>：天赋 id → commonParams）。
    /// 189 号是**灵悟牌**（基础卡 id 列表，天赋 192 生效时算作云剑 / 狂剑 / 灵剑 / 剑阵）。
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>> TalentDatas = new Dictionary<int, IReadOnlyList<int>>();

    /// <summary>刻印点数上限（<c>privateData.keYinData.maxKeYin</c>）。</summary>
    public int MaxKeYin;

    /// <summary>私有的天赋自带数据（<c>privateData.talentDatas</c>）：199 号是五行玉屏选的牌。</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>> PrivateTalentDatas = new Dictionary<int, IReadOnlyList<int>>();

    /// <summary>GetLingWuCards（原码 13876）：talentDatas[189].commonParams，没有则空表。</summary>
    public IReadOnlyList<int> LingWuCards
        => TalentDatas.TryGetValue(189, out var v) ? v : Array.Empty<int>();

    /// <summary>天赋 192 生效且这张牌的**基础 id** 在灵悟牌里（原码四个剑类判定共用的那段）。</summary>
    public bool IsLingWuCard(int cardId)
    {
        if (!HasTalent(192)) return false;
        int b = Effects.CardTypes.BaseId(cardId);
        var l = LingWuCards;
        for (int i = 0; i < l.Count; i++) if (l[i] == b) return true;
        return false;
    }

    /// <summary>仙命「开关」是否激活（对应 tempDatas[id]==0；默认未用过=激活）。</summary>
    public bool FateSwitchActive(int strategyId)
        => _fateSwitchUsed is null || !_fateSwitchUsed.Contains(strategyId);

    /// <summary>标记某仙命开关已用（tempDatas[id]!=0）。</summary>
    public void SetFateSwitchUsed(int strategyId)
    {
        _fateSwitchUsed ??= new HashSet<int>();
        _fateSwitchUsed.Add(strategyId);
    }

    /// <summary>能否复活（CanRevive）：残血 + 任一复活类 buff。盘古符优先，且不被「禁止复活」否决。</summary>
    public bool CanRevive()
    {
        if (Hp <= 0 && HasBuff(BuffType.PanGuFu)) return true;
        if (HasBuff(BuffType.JinZhiFuHuo)) return false;
        if (Hp > 0) return false;
        return HasBuff(BuffType.YanHunGuiFu) || HasBuff(BuffType.YuHuoFengHuang)
            || HasBuff(BuffType.TianNvBaiYuLun) || HasBuff(BuffType.KeYinXuTianMing)
            || HasBuff(BuffType.QiXingJieMing);
    }

    /// <summary>临时生命（对应 characterUI.tempLife —— 战斗逻辑会读它做天赋门槛）。</summary>
    public int TempLife;

    /// <summary>ModifyTempLife：改临时生命。</summary>
    public void ModifyTempLife(int delta) => TempLife += delta;

    /// <summary>SetMaxHp：直接设上限（下限 0；超上限的血会被压回）。</summary>
    public void SetMaxHp(int maxHp)
    {
        MaxHp = maxHp < 0 ? 0 : maxHp;
        if (Hp > MaxHp) Hp = MaxHp;
    }

    /// <summary>SetHp：直接设血（封顶到上限）。</summary>
    public void SetHp(int hp) => Hp = hp > MaxHp ? MaxHp : hp;

    /// <summary>死亡（不能复活/死战时）。headless 只置标志。</summary>
    public void OnDead() => IsDead = true;
}
