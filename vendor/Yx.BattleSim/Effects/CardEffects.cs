using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 卡牌效果（P5）：对应游戏 CardActionBase 框架 + 927 个 Card_&lt;baseId&gt;.ExecuteEffect。
///
/// 模型（忠实）：出一张牌 = 该牌的 ExecuteEffect（施加效果）+ src.Attack(dst, attack, attackCount)。
/// 这里按 **baseId** 注册效果；未注册的牌走 <see cref="Default"/>（加防 + 攻击，覆盖纯攻/纯防/段数/无视防御 素卡）。
/// 有特殊效果的牌（挂 buff / 附伤 / 条件效果 / 五行…）逐个从对应 Card_&lt;baseId&gt; 移植注册——这是 P5 的长期 grind，
/// 每条移植后应对 oracle（B2b：仙命/炼化/注入 造带效果对拍）验证。见覆盖账。
/// </summary>
public static class CardEffects
{
    public delegate void Effect(Combatant src, Combatant dst, BattleCard card);

    private static readonly Dictionary<int, Effect> ByBase = new();

    static CardEffects() { RegisterAll(); }

    /// <summary>出牌：手工注册效果（优先）→ 数据驱动效果(342 规整卡)→ 默认(加防+攻击)。</summary>
    public static void Play(Combatant src, Combatant dst, BattleCard card)
    {
        int baseId = CardTypes.BaseId(card.Id);
        // 原码 `Card_286 : Card_0`，OnExecuted 只调 Card_0 的 —— 286 就是普通攻击（逍遥曲 / 逍遥古琴 / 醉卧逍遥等加成全吃）。
        // 早先 286 的 ops 为空、退回默认效果，只打裸攻击（b7 第三批 b7_18 / 87：逍遥曲 +9 攻没加上）。
        if (baseId == 286) baseId = 0;
        if (ByBase.TryGetValue(baseId, out var e)) { e(src, dst, card); return; }
        // 丹药（DanYaoCardActionBase 的子类）：子类 OnExecuted 第一句是调基类 —— 灵气 > 0 则加、防 > 0 则加 ——
        // 再做自己的效果。抽取器只读子类体，把基类这步丢了（3 级飞云丹「灵气 +4」没加上，B7 实机裁决抓到）。
        // ops 为空的丹药不注册、走 Default（Default 自带灵气 / 防），不经过这里；手写的（驱邪丹）自己做了。
        if (DanYaoBases.Contains(baseId) && CardEffectData.IsLoaded(baseId))
        {
            if (card.Anima > 0) src.ModifyAnima(card.Anima);
            if (card.Def > 0) src.ModifyDef(card.Def);
        }
        if (CardEffectData.TryApply(baseId, src, dst, card))
        {
            // 219 双节棍 / 419：原码效果最后 SwitchJiaShi，抽取器不认识这个调用（ops 里没有）。
            if (baseId == 219 || baseId == 419) SwitchJiaShi(src);
            return;
        }
        Default(src, dst, card);
    }

    /// <summary>原码 DanYaoCardActionBase 的全部子类（基础 id）。</summary>
    private static readonly HashSet<int> DanYaoBases =
        [183, 2000001, 2000002, 2000003, 2000004, 2000005, 2000007, 2000008, 2000010, 2000011, 2000013];

    /// <summary>已登记特殊效果的 baseId 数（进度用）。</summary>
    public static int RegisteredCount => ByBase.Count;

    /// <summary>
    /// 默认路径：**忠实移植 <c>FallbackCardAction.OnExecuted</c>（37228）** —— 反编译里所有
    /// **没有 `Card_&lt;baseId&gt;` 类**的牌都走它（<c>CardFactory.FindCardAction</c> 的 `s_FallbackAction`）。
    ///
    /// ⚠ 早先这里只做「加防 + 攻击」，漏了 FallbackCardAction 里的另一半：
    /// <c>卦象 → 灵气 → 防御 → 剑意</c>（顺序照原码）。后果是**整批卡少一块**——
    /// 实测 105 张基础卡落在 Default 且 <c>an/jy/gx</c> 非零（91 灵气 / 12 剑意 / 9 卦象），
    /// 其中「剑意」型（1000010 剑劈、1000012 剑挡、1000017 凝意诀、1000031 云舞诀）在 b5 里整卡空转。
    ///
    /// 原码门控都是 <c>&gt; 0</c>（负的灵气/剑意**不由 Fallback 施加**，那些牌都有自己的 Card_N 类），
    /// 这里保持一致。
    /// </summary>
    public static void Default(Combatant src, Combatant dst, BattleCard card)
    {
        // ① 攻击（段数 + **随机区间**）：完全照 FallbackCardAction 的算法。
        //    `attackCount == 0` 视作 1；随机上限 `randomAttack` 存在时先吃 ExtraAttack/ExtraAttackCount 两个 buff；
        //    **只有当 `randomAttack > attack` 时才走随机分支** —— 那时每一段伤害**各自取一次** GetNextRandomValue()。
        int atk = card.Attack;
        int cnt = card.AttackCount == 0 ? 1 : card.AttackCount;
        // ⚠ 从**配置表**取（不是 BattleCard 字段）：oracle case 的盘面是从 JSON 造的，只带 a/d/c/an/p，
        //   没有 ra/rd —— 早先读 card.RandomAttack 恒为 0，整族白做。与 <see cref="AnimaOf"/> 同一个理由。
        int rndAtk = src.Config.CardRandomAttack(card.Id);
        if (src.HasBuff(BuffType.ExtraAttack))
        {
            int extra = src.GetBuffValue(BuffType.ExtraAttack);
            atk += extra;
            if (rndAtk > 0) rndAtk += extra;
            src.RemoveBuff(BuffType.ExtraAttack);
        }
        if (src.HasBuff(BuffType.ExtraAttackCount))
        {
            cnt += src.GetBuffValue(BuffType.ExtraAttackCount);
            src.RemoveBuff(BuffType.ExtraAttackCount);
        }
        bool ignore = card.IgnoreDefense && !src.HasBuff(BuffType.BenLunWuShiFangYu);
        if (ignore) src.AddBuff(BuffType.BenLunWuShiFangYu, 1);
        if (atk > 0 || rndAtk > 0)
        {
            if (rndAtk <= atk) CombatMath.Attack(src, dst, atk, cnt);
            else for (int i = 0; i < cnt; i++)
                CombatMath.Attack(src, dst, src.GetNextRandomValue(ParamRequest.Range(ParamSite.FallbackAttack, atk, rndAtk)), 1);
        }
        if (ignore) src.RemoveBuff(BuffType.BenLunWuShiFangYu);
        // ②③ 卦象 → 灵气（原码都在攻击之后、防御之前）
        int gx = src.Config.CardGuaXiang(card.Id);
        if (gx > 0) src.ModifyBuffValue(BuffType.GuaXiang, gx);
        if (card.Anima > 0) src.ModifyAnima(card.Anima);
        // ④ 防御：`randomDef > 0` 时改取 GetNextRandomValue()（原码 37228 同一段）。
        int rndDef = src.Config.CardRandomDef(card.Id);
        if (card.Def > 0 || rndDef > 0)
            src.ModifyDef(rndDef > 0 ? src.GetNextRandomValue(ParamRequest.Range(ParamSite.FallbackDef, card.Def, rndDef)) : card.Def);
        // ⑤ 剑意（卡 19 会被天赋改写，见 CardTypes.JianYiOf）
        int jy = CardTypes.JianYiOf(src, card);
        if (jy > 0) src.ModifyBuffValue(BuffType.JianYi, jy);
    }

    /// <summary>
    /// **切换架势**（拳 ⇄ 棍）：忠实移植 <c>CardActionBase.SwitchJiaShi</c>（原码 24809）。
    /// 调用方：卡 219 / 220 / 222 / 419。
    /// ⚠ **没有任何架势时不切换**（只有拳师角色 4000005 开局才有架势，见 OnBattleStarted 3625）——
    ///   此时只剩下末尾的仙命 349 / 429 两条。
    /// </summary>
    internal static void SwitchJiaShi(Combatant src)
    {
        if (src.HasFateStrategy(335))
        {
            src.ModifyBuffValue(BuffType.QiShiShangXian, 1);
            src.ModifyBuffValue(BuffType.QiShi, 1);
            src.ModifyDef(src.Config.FateOtherParams(335).At(0));
        }
        else if (src.HasBuff(BuffType.QuanJiaShi))
        {
            src.RemoveBuff(BuffType.QuanJiaShi);
            src.ModifyBuffValue(BuffType.GunJiaShi, 1);
            if (src.IsTalentResonanceEffective(94)) src.ModifyDef(src.Config.ResonanceOtherParams(94).At(1));
        }
        else if (src.HasBuff(BuffType.GunJiaShi))
        {
            src.RemoveBuff(BuffType.GunJiaShi);
            src.ModifyBuffValue(BuffType.QuanJiaShi, 1);
            if (src.IsTalentResonanceEffective(94)) src.ModifyTiPo(src.Config.ResonanceOtherParams(94).At(0));
        }
        if (src.HasFateStrategy(349))
            CombatMath.ApplyDamage(src, src.Opponent, DamageInfo.Create(src, DamageType.Damage, src.Config.FateOtherParams(349).At(0)));
        if (src.HasFateStrategy(429))
        {
            if (src.GetBuffValue(BuffType.QuanJiaShi) > 0)
                src.ModifyBuffValue(BuffType.QiShi, src.Config.FateOtherParams(429).At(0));
            else
                src.ModifyBuffValue(BuffType.JiaGong, src.Config.FateOtherParams(429).At(1));
        }
        // 原码末尾：`var anim = src.animator as CharacterBattleAnimator_4000005; if (anim != null) anim.jiaShi = …;
        //   anim.PlayAnimation(…)` —— PlayAnimation 在 null 判断**外面**：非拳师角色（4000005）必定空引用抛异常，
        //   ExecuteEffect 的 try 接住后**跳过这张牌的出牌后钩子**（本张攻击计数不清零，残留到下一张 → 察体等提前触发；b7_179 / 4 / 133）。
        if (src.CharacterId != CharacterQuanShi) throw new CardEffectAbort("SwitchJiaShi：非拳师角色空引用");
    }

    /// <summary>拳师角色 id（原码 CharacterBattleAnimator_4000005）。</summary>
    private const int CharacterQuanShi = 4000005;

    /// <summary>
    /// **触发/使用另一张牌**：按 id 造牌，跑它的效果。
    /// 对应游戏里 <c>cardItem.InitData(其他卡id, …)</c> → <c>CardFactory.FindCardAction(cardItem).ExecuteEffect(src, dst, isTempCard: true)</c>
    /// （卡把**自己**换成另一张牌，框架随后照常执行）。用于「随机使用 N 张 XX 牌」类效果
    /// （卡 354 秣马厉兵、186 狂剑•降神、64/352/355 抽门派牌、60 魔化鲛珠…）。
    ///
    /// ⚠ **不要再单独补一次 Attack**：<c>ExecuteEffect</c> = <c>OnBeforeExecuted → OnExecuted → OnAfterExecuted</c>（21573），
    /// 攻击卡的那次攻击写在它自己的 <c>OnExecuted</c> 里（例：7000001 的 raw 首条就是
    /// <c>Attack(dst, cardConfig.attack, cardConfig.attackCount)</c>），而 <see cref="Play"/> 正是照 <c>OnExecuted</c> 跑的
    /// —— 外面再打一次就是**双倍伤害**。
    ///
    /// ⚠ 仍是**近似**：游戏那条路会连带跑出牌前钩子（相邻效果 / OnBeforeExecuted）与行动权记账，这里只跑效果。
    ///    另加递归深度上限防止卡互相触发成环。
    /// </summary>
    /// <returns>
    /// **临时牌自己的**再行动标记（执行完之后的值）。
    /// ⚠ 引擎里临时牌写的是**它自己的** <c>cardConfig.actionAgain</c>；执行完 <c>InitData(原牌)</c> 还原，
    ///   主循环读的是原牌的配置 —— 所以临时牌**不能**覆盖外层牌的再行动。早先 sim 共用一个
    ///   <see cref="Combatant.CurrentCardActionAgain"/> 槽：被复制的是普攻时，普攻 ops 末尾那句
    ///   `actionAgain = HasBuff(逍遥古琴)`（= false）会把外层「无条件再行动」的牌（如 173 瑶光溯时镜 ag=1）清掉。
    ///   外层要读临时牌结果的（8000012 / 1000064 / 179：`if (cardItem.cardConfig.actionAgain) ExActionAgain+1`，
    ///   读在**还原之前**）就用这个返回值。
    /// </returns>
    internal static bool TriggerCard(Combatant src, Combatant dst, int cardId)
    {
        // ⚠ 只有**负** id 才是「没牌」（原码 `previousGridCardConfig.id == -1`）。
        //   **id 0 是普通攻击**（battle_config 里 `0 → 普通攻击 a=3`，探针 fillBoard 的空槽也补 0），
        //   早先写成 `cardId <= 0` 会把「重复上一格的普攻」整个吃掉 —— 卡 60 在 b5_60_0 上就差这 12 点（4 回合 ×3）。
        if (cardId < 0 || TriggerDepth >= MaxTriggerDepth) return false;
        BattleCard card = src.Config.BuildCard(cardId);
        bool outer = src.CurrentCardActionAgain;
        var ctx = CardContext.Capture(src);
        CardContext.Set(src, card);                  // 原码 cardItem.InitData(临时牌)：执行期间「当前牌」就是临时牌
        src.CurrentCardActionAgain = card.ActionAgain || src.Config.CardActionAgain(cardId);   // 临时牌自己的初值
        TriggerDepth++;
        try
        {
            ExecuteEffect(src, dst, card);           // isTempCard: true —— 钩子里按 TriggerDepth > 0 识别
            return src.CurrentCardActionAgain;
        }
        finally
        {
            TriggerDepth--;
            ctx.Restore(src);                        // 还原外层（原码 InitData(原牌)）
            src.CurrentCardActionAgain = outer;
        }
    }

    /// <summary>
    /// 原码 <c>CardActionBase.ExecuteEffect</c>（21573）：**OnBeforeExecuted → OnExecuted（卡效果）→ OnAfterExecuted**。
    /// ⚠ 每一次执行都完整跑前后钩子 —— 主循环出牌、「下 1 张牌连续生效两次」那一族的额外执行、
    ///   以及「使用 / 复制另一张牌」的临时执行（isTempCard，钩子内部按 <see cref="TriggerDepth"/> 识别）都一样。
    ///   早先 sim 只在主出牌时跑一次钩子，额外执行与临时牌都只跑卡效果，漏掉了它们各自的前后结算。
    /// </summary>
    internal static void ExecuteEffect(Combatant src, Combatant dst, BattleCard card)
    {
        StepLog.Card(src, card.Id, TriggerDepth > 0);   // 逐步对拍：对应探针挂在 ExecuteEffect 入口的前置钩子
        // 原码 ExecuteEffect 的 try { OnBeforeExecuted; OnExecuted; OnAfterExecuted } catch { LogException }：
        // 卡效果中途抛异常时，后面的步骤（含出牌后钩子）全部跳过，对局照常继续。
        try
        {
            CardHookFunctions.OnBeforeExecuted(src, dst);
            Play(src, dst, card);
            CardHookFunctions.OnAfterExecuted(src, dst, card.Id, src.CurrentCardGrid);
        }
        catch (CardEffectAbort) { }
        StepLog.BehaviorSink?.Invoke("effect-end", card.Id, src);
    }

    /// <summary>「随机使用 o[0] 张牌」一族的公共循环（见 ByBase[358] 处注释）。</summary>
    private static void UseParamCards(Combatant src, Combatant dst, BattleCard card, bool restoreHadUsed)
    {
        int n = src.Config.CardOtherParams(card.Id).At(0);
        int grid = src.CurrentCardGrid;
        bool had = src.HadUsed(grid);
        int baseId = CardTypes.BaseId(card.Id);
        var req = ParamRequest.FromCardPool(baseId, UseCardPoolOf(baseId));
        for (int i = 0; i < n; i++)
        {
            int id = src.NextParam(req);
            if (id == -1) continue;
            if (restoreHadUsed && grid >= 0) src.SetHadUsed(grid, had);
            if (TriggerCard(src, dst, id)) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        }
    }

    /// <summary>「随机使用 N 张 X 牌」一族各自的卡池（笔记 §3.8）。</summary>
    private static ParamCardPool UseCardPoolOf(int baseId) => baseId switch
    {
        64 => ParamCardPool.Sect,
        352 => ParamCardPool.SectOrCareer,
        353 => ParamCardPool.SectAttack,
        355 => ParamCardPool.OtherSect,
        358 => ParamCardPool.ZongZi,
        359 => ParamCardPool.ActionAgainOrShenFa,
        361 => ParamCardPool.MiShu,
        362 => ParamCardPool.Exclusive,
        363 => ParamCardPool.HuaShenDream,
        364 => ParamCardPool.JiYuan,
        365 => ParamCardPool.FanXu,
        _ => ParamCardPool.Sect,
    };

    /// <summary>触发链递归深度（防卡互相触发成环）。>0 表示当前正在「用另一张牌」的临时执行里。</summary>
    /// <remarks>⚠ 线程静态：全核并发试算时每个线程各算各的战斗，共享计数会把别的线程的临时执行当成自己的。</remarks>
    [ThreadStatic] internal static int TriggerDepth;
    private const int MaxTriggerDepth = 4;

    /// <summary>攻击助手（施完效果后出攻击段）。</summary>
    private static void Attack(Combatant src, Combatant dst, BattleCard card)
        => CombatMath.Attack(src, dst, card.Attack, card.AttackCount);

    /// <summary>
    /// 卡自带的 <c>cardConfig.anima</c>：优先取输入里带的（oracle 用例直接给数），
    /// 否则回落到配置表的 <c>an</c>（灵气牌的实际数值来源）。
    /// </summary>
    internal static int AnimaOf(Combatant src, BattleCard card)
        => card.Anima != 0 ? card.Anima : (src.Config as Config.JsonBattleConfig)?.Card(card.Id)?.Anima ?? 0;

    /// <summary>
    /// **出牌灵气费用**：忠实移植 <c>BattleCharacter.CheckAnima</c>（原码 24234）里那一串减耗 / 免费分支。
    /// 负 = 耗、正 = 回；调用方只在 <c>&lt; 0</c> 时扣费（与原码 <c>CheckCardCost</c> 一致）。
    /// </summary>
    /// <remarks>
    /// ⚠ **不要**把这些分支并进 <see cref="AnimaOf"/>：后者还被卡效果拿来**加灵气**（正值），
    ///   而下面的「直接置 0」分支只该作用于出牌费用 —— 并进去会把加灵气也清掉。
    /// ⚠ 分支**顺序敏感**，形状统一为「`num += X; if (num &gt; 0) num = 0;`」：减耗但**最多减到 0**。
    /// ⚠ 早先 sim 的费用只有「卡面 an」一层，下面整段都没有 —— 这些卡**多耗灵气**，表现为出得更晚 / 更少。
    /// ⬜ 未移植：天赋 153（以血代灵气）、剑契 / 紫芒星宝 / 刻印 47 等转血与抵扣项（见 BattleResolver 的 ⬜ 清单）。
    /// </remarks>
    internal static int CostOf(Combatant src, BattleCard card)
    {
        int num = AnimaOf(src, card);
        int baseId = CardTypes.BaseId(card.Id);

        // 九霄玲珑镯 + 天赋 222 → 免费。
        if (baseId == 16 && src.HasTalent(222)) num = 0;
        // 共鸣 126：消耗 / 持续牌少耗 1。
        if (src.HasTalentResonance(126)
            && (src.Config.CardIsConsume(card.Id) || src.Config.CardIsSustain(card.Id)) && num < 0)
        {
            num++;
            if (num > 0) num = 0;
        }
        // 下张牌灵气减耗（灵感迸发 6000008 施加）。
        if (src.HasBuff(BuffType.XiaZhangPaiLingQiJianHao) && num < 0)
        {
            num += src.GetBuffValue(BuffType.XiaZhangPaiLingQiJianHao);
            if (num > 0) num = 0;
        }
        // 百鸟灵剑诀：灵剑牌按层数减耗。
        if (src.HasBuff(BuffType.BaiNiaoLingJianJue) && CardTypes.IsLingJian(src, card.Id) && num < 0)
        {
            num += src.GetBuffValue(BuffType.BaiNiaoLingJianJue);
            if (num > 0) num = 0;
        }
        // 仙命 321：描述带「再次行动」的牌少耗 1。
        if (src.HasFateStrategy(321) && src.Config.CardDesc(card.Id).Contains("再次行动") && num < 0)
        {
            num++;
            if (num > 0) num = 0;
        }
        // 灵雀在后：后招牌按层数减耗。
        if (src.HasBuff(BuffType.LingQueZaiHouJiaLingQi) && CardTypes.IsHouZhao(src, card.Id) && num < 0)
        {
            num += src.GetBuffValue(BuffType.LingQueZaiHouJiaLingQi);
            if (num > 0) num = 0;
        }
        // 御空剑阵：已有防则免费。
        if (baseId == 1000025 && src.Def > 0) num = 0;
        // 1000093 / 1000098 云剑•追风：有[连云]或天赋 14 则免费。
        if ((baseId == 1000093 || baseId == 1000098) && (src.HasBuff(BuffType.LianYun) || src.HasTalent(14)))
            num = 0;
        // 五行灵击（7000095 / 极 7000107）：卡组里每有一种五行少耗 1。
        if (baseId == 7000095 || baseId == 7000107)
        {
            num += WuXingFunctions.GetWuXingCountInDeck(src);
            if (num > 0) num = 0;
        }
        // 水灵•波澜（7000019 / 幻 271）：已激活水灵则免费。
        if ((baseId == 7000019 || baseId == 271) && WuXingFunctions.CheckWuXing(src, BuffType.JiHuoShuiLing))
            num = 0;
        // 饿虎扑食 10000029：每层负面状态少耗 1。
        if (baseId == 10000029)
        {
            num += src.GetDebuffCount();
            if (num > 0) num = 0;
        }
        // 海鲜肉粽 108：每种粽子 buff 少耗 1。
        if (baseId == 108)
        {
            num += src.ZongZiBuffCount();
            if (num > 0) num = 0;
        }
        // 星月羽扇：在星位格出牌按层数减耗。
        if (src.HasBuff(BuffType.XingYueYuShan) && src.CurrentCardGrid >= 0
            && GridMarkFunctions.IsXingWei(src, src.CurrentCardGrid))
        {
            num += src.GetBuffValue(BuffType.XingYueYuShan);
            if (num > 0) num = 0;
        }
        // 刻印 95 / 71 让这两张免费。
        if (baseId == 4000041 && src.Subs.HasKeYinType(src, 95)) num = 0;
        if (baseId == 4000022 && src.Subs.HasKeYinType(src, 71)) num = 0;

        return num;
    }

    /// <summary>反转出牌方向：[反转出牌] 有则去、无则加，然后把牌组队列倒序（原码 `ReverseCardItems`）。</summary>
    private static void ToggleReverse(Combatant src)
    {
        if (src.HasBuff(BuffType.FanZhuanChuPai)) src.RemoveBuff(BuffType.FanZhuanChuPai);
        else src.ModifyBuffValue(BuffType.FanZhuanChuPai, 1);
        src.Deck.Reverse();
    }

    /// <summary>卡组（src.Board）里满足条件的牌数；<paramref name="skipGrid"/> ≥ 0 时排除该格（原码 `gridNumber != i`）。</summary>
    private static int CountDeck(Combatant src, int skipGrid, Func<int, bool> pred)
    {
        int n = 0;
        for (int i = 0; i < src.Board.Count; i++)
            if (i != skipGrid && pred(src.Board[i].Id)) n++;
        return n;
    }

    /// <summary>同 <see cref="CountDeck"/>，按牌名判。</summary>
    private static int CountDeckByName(Combatant src, int skipGrid, Func<string, bool> pred)
    {
        int n = 0;
        for (int i = 0; i < src.Board.Count; i++)
            if (i != skipGrid && pred(GridFunctions.CardNameAt(src, i))) n++;
        return n;
    }

    /// <summary>
    /// 「重复 times 次：取玩家参数，-1 即止，否则该 buff −1」—— 返回实际执行次数（82 / 417 共用）。
    /// </summary>
    private static int ConvertDebuffsByParam(Combatant src, int times, int site)
    {
        int done = 0;
        for (int i = 0; i < times; i++)
        {
            int b = src.NextParam(ParamRequest.OwnDebuff(site));
            if (b == -1) break;
            done++;
            src.ModifyBuffValue((BuffType)b, -1);
        }
        return done;
    }

    /// <summary>Level 枚举里的 JinDan（金丹）＝ 3。<see cref="Config.IBattleConfig.CardLevel"/> 与它比较用。</summary>
    private const int LevelJinDan = 3;

    /// <summary>Level 枚举里的 HuaShen（化神）＝ 5。</summary>
    private const int LevelHuaShen = 5;

    /// <summary>Level 枚举里的 YuanYing（元婴）＝ 4。卡体里 `level &lt;= Level.YuanYing` 的门槛用。</summary>
    private const int LevelYuanYing = 4;

    /// <summary>Career 枚举里的 QinShi（琴师）＝ 3。</summary>
    private const int CareerQinShi = 3;

    /// <summary>本牌是否吃到「相生之火」（天赋137）：火灵与任意非火灵互为相生。</summary>
    private static bool XiangShengZhiHuo(Combatant src) => src.HasTalent(137);

    // ─────────────────────────────────────────────────────────────────────
    // 已移植的卡效果（逐个从 Card_<baseId>.ExecuteEffect 忠实搬）。P5 grind 在此累积。
    // ─────────────────────────────────────────────────────────────────────
    private static void RegisterAll()
    {
        // Card_8 云剑•猫爪：挂[无视防御]再攻击（6×2）。忠实 Card_8.ExecuteEffect。
        ByBase[8] = static (src, dst, card) =>
        {
            src.ModifyBuffValue(BuffType.BenLunWuShiFangYu, 1);
            Attack(src, dst, card);
        };

        // ⚠ **卡 125 狂剑•猫爪 不是卡 8 的同类** —— 早先这里写的是 `ByBase[125] = ByBase[8];`（手工别名），
        //   但 125 有一条卡 8 没有的机制：「每用过 1 次"狂剑"追加 1 次攻击（使用此牌算作 2 次"狂剑"）」，
        //   即 `attack(cardConfig.attack, cardConfig.attackCount + GetBuffValue(KuangJian))`，
        //   然后自己再 +1 层 `KuangJian`（加上牌型记账的 +1 正好「算作 2 次」）。
        //   别名把它整个盖掉 → B5 实测 4 次出牌**每次都只打 2 段**（10 点），段数从不增长，
        //   卡 125 偏差 55（real 52/-2 vs sim 28/-3）。
        //   抽取出来的 ops 与反编译**逐条一致**，所以这里的正确做法是**去掉别名**、让它走数据驱动。
        //   （这也印证了 4000031 那条教训：`partial=False` 不等于抽对了 —— 但反过来，
        //    **核对过 ops 与反编译一致之后**，手工注册就该让位。）

        // ─────────────────────────────────────────────────────────────────
        // 相邻格 + 相生：用户指出的四类五行激活机制里的第 ④ 类
        //   「如果前一格相生后一格，则激活后一格」——**不是通用钩子，是这两张卡自己的效果**。
        //   判据都必须看**前后两格的名字**，数据驱动的 ops（施加 buff/加防/攻击）表达不了，
        //   所以手工注册。见 2026-09-22 覆盖账。
        // ─────────────────────────────────────────────────────────────────

        // Card_295（含 10295/20295 稀有度变体）：[消耗]+灵气；前后相邻**任一方向**相生 →
        // 激活**后一格**的元素 + 本张再行动。然后**无论相生与否**：按已激活五行层数加防（o[0] 倍）。
        // ⚠ 原码的 `break` 跳出的是状态机的 switch，不是方法 —— 尾段 `if (GetWuXingActiveNumber() > 0)
        //   ModifyDef(n * otherParams[0])` 总会执行。早先这里用 return，把加防整段漏了。
        ByBase[295] = static (src, dst, card) =>
        {
            src.ModifyAnima(AnimaOf(src, card));
            int grid = src.CurrentCardGrid;
            string prev = GridFunctions.CardNameAt(src, GridFunctions.GetPreviousGrid(src, grid));
            string next = GridFunctions.CardNameAt(src, GridFunctions.GetNextGrid(src, grid));
            bool zhiHuo = XiangShengZhiHuo(src);
            if (WuXingFunctions.IsXiangSheng(prev, next, zhiHuo) || WuXingFunctions.IsXiangSheng(next, prev, zhiHuo))
            {
                WuXingFunctions.ActiveWuXingInName(src, next);
                src.CurrentCardActionAgain = true;   // 原码 cardConfig.actionAgain = true
            }
            int active = WuXingFunctions.GetWuXingActiveNumber(src);
            if (active > 0) src.ModifyDef(active * src.Config.CardOtherParams(card.Id).At(0));
        };

        // Card_196 星弈•跳：先攻击；在星位时沿出牌方向往后看至多 o[0] 格，遇到一张「星弈」牌就挂 1 层[星弈•跳]
        // （SkipChain 据此一路跳到那张星弈牌）。抽取器折不了这个带 break 的 for，只剩一个恒 0 的计数 → 永远不挂。
        //   for (i < otherParams[0]) { name = GetNextGridCardConfig(src, g).name; g = GetNextGrid(src, g);
        //                              if (name.Contains("星弈")) { num2++; break; } }
        ByBase[196] = static (src, dst, card) =>
        {
            Attack(src, dst, card);
            int g = src.CurrentCardGrid;
            if (!GridMarkFunctions.CheckXingWei(src, g)) return;
            int steps = src.Config.CardOtherParams(card.Id).At(0);
            for (int i = 0; i < steps; i++)
            {
                string name = GridFunctions.CardNameAt(src, GridFunctions.GetNextGrid(src, g));
                g = GridFunctions.GetNextGrid(src, g);
                if (name.Contains("星弈"))
                {
                    src.ModifyBuffValue(BuffType.XingYiTiao, 1);
                    break;
                }
            }
        };

        // Card_4000063 轰雷掣电：原码是 `for (i < attackCount) { 有卦象 → 本段后 +1 灵气; num2 = GetNextRandomValue();
        //   if (num2 == -1) num2 = Random.Range(attack, randomAttack + 1); Attack(dst, num2); … }` ——
        //   **每段各掷一次**。抽取器没能折叠这个循环（只剩一段），且整句「+1 灵气」丢了，故手工注册。
        //   掷不出（-1）时按 sim 对 randrange 的既有约定取区间中值。
        ByBase[4000063] = static (src, dst, card) =>
        {
            int n = card.AttackCount;
            int ra = src.Config.CardRandomAttack(card.Id);
            for (int i = 0; i < n; i++)
            {
                int add = src.HasBuff(BuffType.GuaXiang) ? 1 : 0;
                int v = src.GetNextRandomValue(ParamRequest.Range(4000063, card.Attack, ra));
                if (v == -1) v = (card.Attack + ra) / 2;
                CombatMath.Attack(src, dst, v, 1);
                if (add > 0) src.ModifyAnima(add);
            }
        };

        // ── 三张「未折叠循环」卡（抽取器只留下一次迭代），照原码手工注册 ──
        // Card_129 灯影蚀焰：施加 o[0] 层内伤；重复 o[1] 次：掷骰 < o[2] 且对方有内伤 → 对方立刻按内伤层数掉血一次。
        ByBase[129] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            dst.ModifyBuffValue(BuffType.NeiShang, p.At(0));
            for (int i = 0; i < p.At(1); i++)
            {
                if (src.GetNextRandomValue(ParamRequest.Percent(129)) < p.At(2) && dst.GetBuffValue(BuffType.NeiShang) > 0)
                    dst.ModifyHp(-dst.GetBuffValue(BuffType.NeiShang));
            }
        };
        // Card_2000004 驱邪丹：丹药基类（防 / 灵气）之后，重复 o[0] 次：按玩家参数选一种 buff 减 o[1] 层。
        ByBase[2000004] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            if (card.Anima > 0) src.ModifyAnima(card.Anima);   // IL_0094 先灵气
            if (card.Def > 0) src.ModifyDef(card.Def);         // IL_0133 后防
            for (int i = 0; i < p.At(0); i++)
            {
                int b = src.NextParam(ParamRequest.OwnDebuff(2000004));
                if (b != -1) src.ModifyBuffValue((BuffType)b, -p.At(1));
            }
        };
        // Card_10000065 无尽崩绝：重复 o[0] 次：按玩家参数取一张牌**临时执行**，它若再行动则转一层外行动。
        ByBase[10000065] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            for (int i = 0; i < p.At(0); i++)
            {
                int id = src.NextParam(ParamRequest.FromCardPool(10000065, ParamCardPool.BengQuan));
                if (id == -1) break;                     // 原码不判（InitData(-1) 会抛异常中断），这里等价地停下
                if (TriggerCard(src, dst, id)) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
            }
        };

        // 马年一族「随机使用 o[0] 张 X 牌」：重复 o[0] 次，按玩家参数取牌**临时执行**，它若再行动则转一层外行动；
        // 参数为 -1 时跳过本次（原码 `if (nextParam != -1)`）。o[0] = 2 的四张要循环两次（抽取器只留下一次）。
        // 355 指鹿为马另有一步：每次临时执行前把本格 hadUsed 还原成出牌时的值（原码 `cardItem.hadUsed = hasUsed`）。
        ByBase[358] = static (src, dst, card) => UseParamCards(src, dst, card, restoreHadUsed: false);
        ByBase[361] = static (src, dst, card) => UseParamCards(src, dst, card, restoreHadUsed: true);   // IL_0146 还原 hadUsed
        ByBase[362] = static (src, dst, card) => UseParamCards(src, dst, card, restoreHadUsed: false);
        ByBase[355] = static (src, dst, card) => UseParamCards(src, dst, card, restoreHadUsed: true);
        // 同一族的其余几张（原码循环体逐位相同，只差循环前的一两句）：
        ByBase[352] = static (src, dst, card) => UseParamCards(src, dst, card, restoreHadUsed: false);
        ByBase[364] = static (src, dst, card) => UseParamCards(src, dst, card, restoreHadUsed: false);
        ByBase[365] = static (src, dst, card) => UseParamCards(src, dst, card, restoreHadUsed: false);
        ByBase[363] = static (src, dst, card) => UseParamCards(src, dst, card, restoreHadUsed: true);
        // 64 五彩鲛珠：同 355（每次还原 hadUsed）。⚠ 原码不判 -1（InitData(-1)），sim 跳过。
        ByBase[64] = static (src, dst, card) => UseParamCards(src, dst, card, restoreHadUsed: true);
        // 353 金戈铁马：先攻击，再同 358。
        ByBase[353] = static (src, dst, card) =>
        {
            Attack(src, dst, card);
            UseParamCards(src, dst, card, restoreHadUsed: false);
        };
        // 359 一马当先：先 +o[1] 身法，再同 355。
        ByBase[359] = static (src, dst, card) =>
        {
            src.ModifyBuffValue(BuffType.ShenFa, src.Config.CardOtherParams(card.Id).At(1));
            UseParamCards(src, dst, card, restoreHadUsed: true);
        };
        // 360 蛛丝马迹：同 358；每次用完的若是 4000006 / 10000017，且对方血高于 o[1] → 对方血直接设为 o[1]。
        ByBase[360] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            for (int i = 0; i < p.At(0); i++)
            {
                int id = src.NextParam(ParamRequest.FromCardPool(360, ParamCardPool.MaName));
                if (id == -1) continue;
                if (TriggerCard(src, dst, id)) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
                int b = CardTypes.BaseId(id);
                if ((b == 4000006 || b == 10000017) && dst.Hp > p.At(1)) dst.SetHp(p.At(1));
            }
        };

        // ── 「激活某格牌的五行」一族：抽取器不认 ActiveWuXingInName，这句整段丢了，照原码手工注册 ──
        // Card_153 浑星刺：攻击后，依次激活后 o[0] 格牌的五行；这些牌只含 1 种五行则 +1 灵气。
        ByBase[153] = static (src, dst, card) =>
        {
            CombatMath.Attack(src, dst, card.Attack, card.AttackCount);
            int g = src.CurrentCardGrid;
            var kinds = new HashSet<string>();
            int n = src.Config.CardOtherParams(card.Id).At(0);
            for (int i = 0; i < n; i++)
            {
                string name = GridFunctions.CardNameAt(src, GridFunctions.GetNextGrid(src, g));
                g = GridFunctions.GetNextGrid(src, g);
                WuXingFunctions.ActiveWuXingInName(src, name);
                string wx = WuXingFunctions.GetWuXingName(name);
                if (wx != "") kinds.Add(wx);
            }
            if (kinds.Count == 1) src.ModifyAnima(1);
        };
        // Card_200 浑天运笔：+灵气、+防；**然后**后一格是五行牌 → 激活其五行并再行动（原码 IL_009b → IL_0141 的顺序）。
        ByBase[200] = static (src, dst, card) =>
        {
            src.ModifyAnima(AnimaOf(src, card));
            src.ModifyDef(card.Def);
            string name = GridFunctions.CardNameAt(src, GridFunctions.GetNextGrid(src, src.CurrentCardGrid));
            if (WuXingFunctions.ActiveWuXingInName(src, name)) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        };
        // Card_292 幻•浑天印：+灵气、攻击，然后依次激活后 o[0] 格牌的五行。（被动「算作 o[1] 种五行」在 GetWuXingCountInDeck。）
        ByBase[292] = static (src, dst, card) =>
        {
            src.ModifyAnima(AnimaOf(src, card));
            CombatMath.Attack(src, dst, card.Attack, card.AttackCount);
            int g = src.CurrentCardGrid;
            int n = src.Config.CardOtherParams(card.Id).At(0);
            for (int i = 0; i < n; i++)
            {
                string name = GridFunctions.CardNameAt(src, GridFunctions.GetNextGrid(src, g));
                g = GridFunctions.GetNextGrid(src, g);
                WuXingFunctions.ActiveWuXingInName(src, name);
            }
        };
        // Card_7000101 混元化灵：先激活前后两格牌的五行，再 +「灵气 + 卡组五行种类数」。（被动「多算 o[0] 种」在 GetWuXingCountInDeck。）
        ByBase[7000101] = static (src, dst, card) =>
        {
            int g = src.CurrentCardGrid;
            src.ModifyAnima(AnimaOf(src, card) + WuXingFunctions.GetWuXingCountInDeck(src));   // IL_009b 先灵气
            WuXingFunctions.ActiveWuXingInName(src, GridFunctions.CardNameAt(src, GridFunctions.GetPreviousGrid(src, g)));
            WuXingFunctions.ActiveWuXingInName(src, GridFunctions.CardNameAt(src, GridFunctions.GetNextGrid(src, g)));
        };
        // Card_7000078 梦•混元无极阵：按玩家参数取一张五行灵印**临时执行**（再行动转外行动）；
        //   境界高于金丹时先激活后一格牌的五行。
        ByBase[7000078] = static (src, dst, card) =>
        {
            int id = src.NextParam(ParamRequest.Formula(card.Id));
            if (id == -1) return;
            if (CardTypes.LevelOf(src, card) > 3)
                WuXingFunctions.ActiveWuXingInName(src, GridFunctions.CardNameAt(src, GridFunctions.GetNextGrid(src, src.CurrentCardGrid)));
            if (TriggerCard(src, dst, id)) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        };

        // Card_262 幻•引气剑：按玩家参数取一张牌临时执行（-1 → 整段结束），它若再行动则转一层外行动；
        // 若那张牌**元婴及以下**，再追加 `attack + ⌊灵气 / o[1]⌋` 攻 × attackCount（本张自己的攻/次数）。
        // 抽取器只留下「用牌」，追加攻击被 `FindCardConfig(id).level <= YuanYing` 守卫整段丢掉。
        ByBase[262] = static (src, dst, card) =>
        {
            int id = src.NextParam(ParamRequest.Formula(card.Id));
            if (id == -1) return;
            if (TriggerCard(src, dst, id)) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
            if (src.Config.CardLevel(id) > LevelYuanYing) return;
            int per = src.Config.CardOtherParams(card.Id).At(1);
            int bonus = per > 0 ? src.Anima / per : 0;
            CombatMath.Attack(src, dst, card.Attack + bonus, card.AttackCount);
        };

        // Card_394 天劫麻辣粽：每用过 1 次[咸粽]（XianZongJiShu）就重复一次「生命 +o[0]」。
        // 抽取器没折叠这个 for，只剩一次加血。
        ByBase[394] = static (src, dst, card) =>
        {
            int times = src.GetBuffValue(BuffType.XianZongJiShu);
            int n = src.Config.CardOtherParams(card.Id).At(0);
            if (times <= 0 || n <= 0) return;
            for (int i = 0; i < times; i++) src.ModifyHp(n);
        };

        // ─────────────────────────────────────────────────────────────────
        // 「遍历卡组计数」一族：原码 `for (i < GetBattleDeckIdList().Count) if (IsXxx(deck[i])) n++;`，
        // 抽取器把它错抽成「对**本张牌**判一次牌型」（本张不是该牌型 → 计数恒 0）。照原码手工注册。
        // 牌组 = src.Board（同 GetBattleDeckIdList）。
        // ─────────────────────────────────────────────────────────────────

        // Card_188 剑灵葵：+灵气；卡组每有 1 张灵剑再 +1 灵气（最多 o[0]）。
        ByBase[188] = static (src, dst, card) =>
        {
            src.ModifyAnima(AnimaOf(src, card));
            int n = CountDeck(src, -1, id => CardTypes.IsLingJian(src, id));
            int cap = src.Config.CardOtherParams(card.Id).At(0);
            if (n > cap) n = cap;
            if (n > 0) src.ModifyAnima(n);
        };
        // Card_213 曳影剑阵：卡组每有 1 张剑阵追加 1 段（最多 o[0]）；本回合击伤过则按次数 × o[1] 加防。
        ByBase[213] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            int n = CountDeck(src, -1, id => CardTypes.IsJianZhen(src, id));
            if (n > p.At(0)) n = p.At(0);
            CombatMath.Attack(src, dst, card.Attack, card.AttackCount + n);
            if (src.HasBuff(BuffType.WoundedCount))
                src.ModifyDef(src.GetBuffValue(BuffType.WoundedCount) * p.At(1));
        };
        // Card_260 幻•暗鸦灵剑：攻击；有灵气则按 o[0]×灵气 加防（最多 o[2]）；卡组灵剑数 ≥ o[1] → 再行动（否则不再行动）。
        ByBase[260] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            CombatMath.Attack(src, dst, card.Attack, card.AttackCount);
            if (src.Anima > 0)
            {
                int d = p.At(0) * src.Anima;
                if (d > p.At(2)) d = p.At(2);
                src.ModifyDef(d);
            }
            src.CurrentCardActionAgain = CountDeck(src, -1, id => CardTypes.IsLingJian(src, id)) >= p.At(1);
        };
        // Card_1000062 合势剑阵：卡组中每有 1 张**其他**剑阵，多 o[0] 攻、o[1] 防（先攻后防）。
        ByBase[1000062] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            int n = CountDeck(src, src.CurrentCardGrid, id => CardTypes.IsJianZhen(src, id));
            CombatMath.Attack(src, dst, card.Attack + n * p.At(0), card.AttackCount);
            src.ModifyDef(card.Def + n * p.At(1));
        };
        // Card_126 澄心剑阵：前段（加防 + 澄心天赋分支）照数据；末尾「卡组（含自己）剑阵数 ≥ o[0] 则再行动」是遍历整副牌的循环，
        // 抽取器把它展平成「当前这张是剑阵就 +1」→ 永远只数到 1、不再行动（B7 r5 b7_142：真机追击出下一张幻•灵犀剑阵）。
        ByBase[126] = static (src, dst, card) =>
        {
            CardEffectData.TryApply(126, src, dst, card);
            var p = src.Config.CardOtherParams(card.Id);
            src.CurrentCardActionAgain = CountDeck(src, -1, id => CardTypes.IsJianZhen(src, id)) >= p.At(0);
        };
        // Card_414：激活土灵 +1；卡组（含自己）里名字带「印」或非五行牌的张数 n，攻 attack + n×o[0]、防 def + n×o[1]（先攻后防）。
        // 数据里这个计数循环被丢了（count 恒 0）。
        ByBase[414] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            src.ModifyBuffValue(BuffType.JiHuoTuLing, 1);
            int n = CountDeckByName(src, -1, nm => nm.Contains('印') || !GridFunctions.IsWuXingCard(nm));
            CombatMath.Attack(src, dst, card.Attack + n * p.At(0), card.AttackCount);
            src.ModifyDef(card.Def + n * p.At(1));
        };
        // Card_1000080 梦•御空剑阵：造成 o[0] 伤害；≤金丹：卡组每张剑阵 +o[1]；≥元婴：+ 剑阵使用计数×o[1]，
        // 且卡组剑阵数 ≥ o[2] 时再行动（否则不再行动）。
        ByBase[1000080] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            int lv = CardTypes.LevelOf(src, card);
            int dmg = p.At(0);
            int cnt = CountDeck(src, -1, id => CardTypes.IsJianZhen(src, id));
            if (lv <= LevelJinDan) dmg += cnt * p.At(1);
            if (lv >= LevelYuanYing)
            {
                dmg += src.GetBuffValue(BuffType.JianZhenCount) * p.At(1);
                src.CurrentCardActionAgain = cnt >= p.At(2);
            }
            CombatMath.ApplyDamage(src, dst, DamageInfo.Create(src, DamageType.Damage, dmg));
        };
        // Card_4000073 梦•落雷术：卡组中（除本格）每有 1 张名含「雷」或「卦」的牌 → 计数 +1（起始 1），多 o[0]×计数 攻；
        // 金丹以上再掷一次，< 10 追加一次同样的攻击。
        ByBase[4000073] = static (src, dst, card) =>
        {
            int n = 1 + CountDeckByName(src, src.CurrentCardGrid, nm => nm.Contains("雷") || nm.Contains("卦"));
            int ex = n > 0 ? src.Config.CardOtherParams(card.Id).At(0) * n : 0;
            CombatMath.Attack(src, dst, card.Attack + ex, 1);
            if (src.Config.CardLevel(card.Id) > LevelJinDan && src.GetNextRandomValue(ParamRequest.Percent(4000073)) < 10)
                CombatMath.Attack(src, dst, card.Attack + ex, 1);
        };
        // Card_29 狂雷电闪：施加 o[0] 层破绽；卡组中（除本格）有名含「雷」的牌才再行动（否则不再行动）。
        ByBase[29] = static (src, dst, card) =>
        {
            dst.ModifyBuffValue(BuffType.PoZhan, src.Config.CardOtherParams(card.Id).At(0));
            src.CurrentCardActionAgain = CountDeckByName(src, src.CurrentCardGrid, nm => nm.Contains("雷")) > 0;
        };
        // Card_82 万玄破魔掌：+体魄；每 o[0] 体魄按玩家参数去 1 层负面（取空即止），去掉几层就加几层加攻；再攻击。
        ByBase[82] = static (src, dst, card) =>
        {
            src.ModifyTiPo(card.Physique != 0 ? card.Physique : (src.Config as Config.JsonBattleConfig)?.Card(card.Id)?.Physique ?? 0);
            int per = src.Config.CardOtherParams(card.Id).At(0);
            int n = ConvertDebuffsByParam(src, per > 0 ? src.TiPo / per : 0, 82);
            if (n > 0) src.ModifyBuffValue(BuffType.JiaGong, n);
            CombatMath.Attack(src, dst, card.Attack, card.AttackCount);
        };
        // Card_417 万玄锻灵：+灵气；每 o[0] 体魄按玩家参数去 1 层负面（取空即止），每层转 2 灵气；然后按当前灵气加体魄。
        ByBase[417] = static (src, dst, card) =>
        {
            src.ModifyAnima(AnimaOf(src, card));
            int per = src.Config.CardOtherParams(card.Id).At(0);
            int n = ConvertDebuffsByParam(src, per > 0 ? src.TiPo / per : 0, 417);
            if (n > 0) src.ModifyAnima(n * 2);
            src.ModifyTiPo(src.Anima);
        };
        // Card_4000085 梦•白蛇吐信：攻击；重复 o[0] 次（后招再 +o[1]）：按玩家参数给对方挂 1 层 buff（-1 跳过）。
        ByBase[4000085] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            CombatMath.Attack(src, dst, card.Attack, card.AttackCount);
            int times = p.At(0);
            if (HouZhaoFunctions.CheckHouZhao(src, card.Id, src.CurrentCardGrid)) times += p.At(1);
            // 池：内伤 / 虚弱 / 外伤（元婴档加减攻）。原码取的是 dst 的 GetNextParam（队列共享，只影响值域按谁算）。
            var req = ParamRequest.FromDebuffPool(4000085, src.Config.CardLevel(card.Id) >= LevelYuanYing
                ? ParamDebuffPool.NeiXuWaiJian : ParamDebuffPool.NeiXuWai);
            for (int i = 0; i < times; i++)
            {
                int b = dst.NextParam(req);
                if (b != -1) dst.ModifyBuffValue((BuffType)b, 1);
            }
        };
        // Card_10000068 玄心斩魄：把自身每种负面状态各给对方挂 o[0] 层；挂[玄心斩魄]后攻击。
        ByBase[10000068] = static (src, dst, card) =>
        {
            int n = src.Config.CardOtherParams(card.Id).At(0);
            foreach (var b in src.GetDebuffList()) dst.ModifyBuffValue(b, n);
            src.ModifyBuffValue(BuffType.XuanXinZhanPo, 1);
            CombatMath.Attack(src, dst, card.Attack, card.AttackCount);
        };

        // Card_7000077 梦•五行刺：**二选一**（原码 IL_0098 的 if 两支各自 goto 结尾）——
        //   ≤ 金丹：攻击，然后卡组每有 1 种五行加 o[0] 防；
        //   > 金丹：按五行牌使用次数（JiLuWuXingPaiShiYongCiShu）× o[0] 加防（不攻击）。
        // 抽取器把两支都当成顺序执行，低境界时白多一份加防。
        ByBase[7000077] = static (src, dst, card) =>
        {
            int k = src.Config.CardOtherParams(card.Id).At(0);
            if (src.Config.CardLevel(card.Id) <= LevelJinDan)
            {
                CombatMath.Attack(src, dst, card.Attack, card.AttackCount);
                int d = WuXingFunctions.GetWuXingCountInDeck(src) * k;
                if (d > 0) src.ModifyDef(d);
            }
            else
            {
                int d = src.GetBuffValue(BuffType.JiLuWuXingPaiShiYongCiShu) * k;
                if (d > 0) src.ModifyDef(d);
            }
        };

        // Card_8000003：用过持续牌 → **一次两段**攻击（攻值分别为 attack / o[0]，原码 `Attack(dst, List{attack, o[0]})`）；
        // 否则普通攻击。抽取器把它抽成「有 buff 时先打一段、再无条件打一次」。
        ByBase[8000003] = static (src, dst, card) =>
        {
            if (src.HasBuff(BuffType.YongGuoChiXuPai))
                CombatMath.Attack(src, dst, new[] { card.Attack, src.Config.CardOtherParams(card.Id).At(0) });
            else
                CombatMath.Attack(src, dst, card.Attack, card.AttackCount);
        };
        // 同形状（条件成立 → 一次多段、每段攻值不同的列表攻击；否则普通攻击）：
        // 1000002 连云时 {attack, o[0]}；4000010 星位时 {attack, o[0]}；4000029 星位时 {attack, attack, o[0]}。
        ByBase[1000002] = static (src, dst, card) =>
        {
            if (src.HasBuff(BuffType.LianYun))
                CombatMath.Attack(src, dst, new[] { card.Attack, src.Config.CardOtherParams(card.Id).At(0) });
            else
                CombatMath.Attack(src, dst, card.Attack, card.AttackCount);
        };
        ByBase[4000010] = static (src, dst, card) =>
        {
            if (GridMarkFunctions.CheckXingWei(src, src.CurrentCardGrid))
                CombatMath.Attack(src, dst, new[] { card.Attack, src.Config.CardOtherParams(card.Id).At(0) });
            else
                CombatMath.Attack(src, dst, card.Attack, card.AttackCount);
        };
        ByBase[4000029] = static (src, dst, card) =>
        {
            if (GridMarkFunctions.CheckXingWei(src, src.CurrentCardGrid))
                CombatMath.Attack(src, dst, new[] { card.Attack, card.Attack, src.Config.CardOtherParams(card.Id).At(0) });
            else
                CombatMath.Attack(src, dst, card.Attack, card.AttackCount);
        };
        // Card_11000006：自己血不多于对方 → 攻击后再追加一次 o[0] 攻；否则只攻击。（if 块以 goto 结尾，块后是 else 支）
        ByBase[11000006] = static (src, dst, card) =>
        {
            bool low = src.Hp <= dst.Hp;
            CombatMath.Attack(src, dst, card.Attack, card.AttackCount);
            if (low) CombatMath.Attack(src, dst, src.Config.CardOtherParams(card.Id).At(0), 1);
        };

        // Card_310 幻•浩然正气：+灵气、+o[2] 身法；然后**重复 o[0] 次**按玩家参数给自己去 1 层 buff；
        // 负面层数实际减少了 n 层 → 再加 n×o[1] 身法与 n 层气势。抽取器只留下一次抽数。
        ByBase[310] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            src.ModifyAnima(AnimaOf(src, card));
            src.ModifyBuffValue(BuffType.ShenFa, p.At(2));
            int before = src.GetDebuffCount();
            for (int i = 0; i < p.At(0); i++)
            {
                int b = src.NextParam(ParamRequest.OwnDebuff(310));
                if (b != -1) src.ModifyBuffValue((BuffType)b, -1);
            }
            int n = before - src.GetDebuffCount();
            if (n > 0)
            {
                src.ModifyBuffValue(BuffType.ShenFa, n * p.At(1));
                src.ModifyBuffValue(BuffType.QiShi, n);
            }
        };

        // Card_10000024 崩拳•截脉：重复 o[0] 次：按玩家参数取一种 buff，自己 -1、对方 +1（-1 跳过）；然后攻击；挂 o[0] 层[崩拳•截脉]。
        // 抽取器把 repeat 只包住了抽数，转移那两句落在循环外。
        ByBase[10000024] = static (src, dst, card) =>
        {
            int n = src.Config.CardOtherParams(card.Id).At(0);
            for (int i = 0; i < n; i++)
            {
                int b = src.NextParam(ParamRequest.OwnDebuff(10000024));
                if (b == -1) continue;
                src.ModifyBuffValue((BuffType)b, -1);
                dst.ModifyBuffValue((BuffType)b, 1);
            }
            Attack(src, dst, card);
            src.ModifyBuffValue(BuffType.BengQuanJieMai, n);
        };
        // Card_5000012 同心曲：自身每种负面状态按**当前层数**同样施加给对方；然后挂 1 层[琴师牌]。
        ByBase[5000012] = static (src, dst, card) =>
        {
            foreach (var b in src.GetDebuffList()) dst.ModifyBuffValue(b, src.GetBuffValue(b));
            src.ModifyBuffValue(BuffType.QinShiPai, 1);
        };

        // ─────────────────────────────────────────────────────────────────
        // 「耗 1 灵气：…」一族（原码 `if (src.TryCostAnima(N) > 0) {…}`）：TryCostAnima 有副作用（扣灵气），
        // 抽取器不认这个守卫 → 这些效果整条被丢。照原码（按 IL 偏移序）手工注册。
        // ─────────────────────────────────────────────────────────────────
        // Card_102 麻辣粽：内伤 +o[0]；生命 +o[1] ×2；耗 1 灵气：生命 +o[2]。
        ByBase[102] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            src.ModifyBuffValue(BuffType.NeiShang, p.At(0));
            src.ModifyHp(p.At(1));
            src.ModifyHp(p.At(1));
            if (src.TryCostAnima(1) > 0) src.ModifyHp(p.At(2));
        };
        // Card_99 香菇粽：生命 +o[0]；耗 1 灵气：生命 +o[1]。
        ByBase[99] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            src.ModifyHp(p.At(0));
            if (src.TryCostAnima(1) > 0) src.ModifyHp(p.At(1));
        };
        // Card_1000089 丹雀灵剑：攻击；再耗 1 灵气：追加 o[0] 攻（1 段）。
        ByBase[1000089] = static (src, dst, card) =>
        {
            Attack(src, dst, card);
            if (src.TryCostAnima(1) > 0) CombatMath.Attack(src, dst, src.Config.CardOtherParams(card.Id).At(0), 1);
        };
        // Card_10000011 朝气蓬勃：攻击（每层气势多 o[0] 攻）；耗 1 灵气：气势 +o[1]。
        ByBase[10000011] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            int extra = src.HasBuff(BuffType.QiShi) ? src.GetBuffValue(BuffType.QiShi) * p.At(0) : 0;
            CombatMath.Attack(src, dst, card.Attack + extra, card.AttackCount);
            if (src.TryCostAnima(1) > 0) src.ModifyBuffValue(BuffType.QiShi, p.At(1));
        };
        // Card_10000012 血气方刚：攻击；耗 1 灵气：追加 o[0] 攻（1 段）。
        ByBase[10000012] = static (src, dst, card) =>
        {
            Attack(src, dst, card);
            if (src.TryCostAnima(1) > 0) CombatMath.Attack(src, dst, src.Config.CardOtherParams(card.Id).At(0), 1);
        };
        // Card_10000021 岿然不动：+防；气势 +o[0]；耗 1 灵气：生命 +o[1]。
        ByBase[10000021] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            src.ModifyDef(card.Def);
            src.ModifyBuffValue(BuffType.QiShi, p.At(0));
            if (src.TryCostAnima(1) > 0) src.ModifyHp(p.At(1));
        };
        // Card_10000022 势大力沉：耗 1 灵气 → 此牌多 o[0] 攻并获得[碎防]；然后攻击。
        ByBase[10000022] = static (src, dst, card) =>
        {
            int extra = 0;
            if (src.TryCostAnima(1) > 0)
            {
                extra += src.Config.CardOtherParams(card.Id).At(0);
                src.ModifyBuffValue(BuffType.SuiFang, 1);
            }
            CombatMath.Attack(src, dst, card.Attack + extra, card.AttackCount);
        };
        // Card_10000033 势如破竹：最多耗 o[0] 灵气，每点气势 +o[1]、此牌多 o[2] 攻；然后攻击。
        ByBase[10000033] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            int extra = 0;
            int cost = src.TryCostAnima(p.At(0));
            if (cost > 0)
            {
                src.ModifyBuffValue(BuffType.QiShi, cost * p.At(1));
                extra = cost * p.At(2);
            }
            CombatMath.Attack(src, dst, card.Attack + extra, card.AttackCount);
        };
        // Card_10000092 凌空飞扫：攻击（每 2 灵气多 1 攻，按出牌时灵气）；最多耗 o[1] 灵气：每点身法 +o[2]，
        // 并按玩家参数去 cost×o[3] 层 buff（-1 跳过）。
        ByBase[10000092] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            CombatMath.Attack(src, dst, card.Attack + src.Anima / 2, 1);
            int cost = src.TryCostAnima(p.At(1));
            if (cost <= 0) return;
            int sf = cost * p.At(2);
            if (sf > 0) src.ModifyBuffValue(BuffType.ShenFa, sf);
            int n = cost * p.At(3);
            for (int i = 0; i < n; i++)
            {
                int b = src.NextParam(ParamRequest.OwnDebuff(10000092));
                if (b != -1) src.ModifyBuffValue((BuffType)b, -1);
            }
        };
        // Card_10000097 金刚捣碓：最多耗 o[0] 灵气，每点此牌多 o[1] 攻、o[2] 防；先攻击（1 段）再加防。
        ByBase[10000097] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            int cost = src.TryCostAnima(p.At(0));
            int atk = card.Attack, def = card.Def;
            if (cost > 0) { atk += cost * p.At(1); def += cost * p.At(2); }
            CombatMath.Attack(src, dst, atk, 1);
            if (def > 0) src.ModifyDef(def);
        };

        // Card_4000015 坎卦：+卦象；后 o[0] 格成为星位（易卦自解时再多 4 格）。
        // 次数表达式带守卫变量，抽取器没折成 xingwei_forward。
        ByBase[4000015] = static (src, dst, card) =>
        {
            int gx = (src.Config as Config.JsonBattleConfig)?.Card(card.Id)?.GuaXiang ?? 0;
            src.ModifyBuffValue(BuffType.GuaXiang, gx);
            int n = src.Config.CardOtherParams(card.Id).At(0) + (CardTypes.YiGuaZiJieCheck(src) ? 4 : 0);
            int g = src.CurrentCardGrid;
            for (int i = 0; i < n; i++)
            {
                g = GridFunctions.GetNextGrid(src, g);
                GridMarkFunctions.AddXingWei(src, g);
            }
        };

        // Card_10 星爆术：共鸣 120 生效且本场未触发、星力 ≥ 共鸣 o[0] → 置标志，追加 1 次攻击；
        // 有星力则 -1 星力、此牌多 o[0] 攻。先主攻击，再按追加次数攻击（同攻值）。抽取器丢了共鸣那段。
        ByBase[10] = static (src, dst, card) =>
        {
            int exAtk = 0, exCnt = 0;
            if (src.IsTalentResonanceEffective(120) && !src.CheckTalentResonanceTempFlag(120)
                && src.GetBuffValue(BuffType.XingLi) >= src.Config.ResonanceOtherParams(120).At(0))
            {
                src.SetTalentResonanceTempFlag(120, true);
                exCnt = 1;
            }
            if (src.HasBuff(BuffType.XingLi))
            {
                src.ModifyBuffValue(BuffType.XingLi, -1);
                exAtk += src.Config.CardOtherParams(card.Id).At(0);
            }
            CombatMath.Attack(src, dst, card.Attack + exAtk, card.AttackCount);
            if (exCnt > 0) CombatMath.Attack(src, dst, card.Attack + exAtk, exCnt);
        };
        // Card_43 逆境不弃：命元低于对方 → 此牌多 o[0] 攻；攻击；生命低于对方 → 生命 +o[1]；
        // 修为低于对方 → 身法 +o[2]（修为取上回合数据 LastRoundExp，输入没有时双方都是 0）。
        ByBase[43] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            int extra = src.TempLife < dst.TempLife ? p.At(0) : 0;
            CombatMath.Attack(src, dst, card.Attack + extra, card.AttackCount);
            if (src.Hp < dst.Hp) src.ModifyHp(p.At(1));
            if (src.LastRoundExp < dst.LastRoundExp) src.ModifyBuffValue(BuffType.ShenFa, p.At(2));
        };
        // Card_138 玄心盛气掌：每有 1 种负面状态加 o[0] 气势（先），然后攻击（每层气势多 o[1] 攻，1 段）。
        ByBase[138] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            int kinds = src.GetDebuffList().Count;
            if (kinds > 0) src.ModifyBuffValue(BuffType.QiShi, kinds * p.At(0));
            CombatMath.Attack(src, dst, card.Attack + src.GetBuffValue(BuffType.QiShi) * p.At(1), 1);
        };

        // Card_323 幻•勤拙剑：攻击；修为 ≥ o[1] → [幻琴拙剑再次行动] +o[2]，否则 +o[0]。
        // 原码读的是当前修为 characterUI.exp（= Combatant.Exp）。
        ByBase[323] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            Attack(src, dst, card);
            src.ModifyBuffValue(BuffType.HuanQinZhuoJianZaiCiXingDong, src.Exp >= p.At(1) ? p.At(2) : p.At(0));
        };
        // Card_393 道韵腊肉粽：清掉全部负面状态，按清掉的层数 n：生命 +n×o[0]、恢复 +n×o[1]。
        ByBase[393] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            int n = src.RemoveAllDebuff();
            if (n <= 0) return;
            int hp = n * p.At(0);
            if (hp > 0) src.ModifyHp(hp);
            int hf = n * p.At(1);
            if (hf > 0) src.ModifyBuffValue(BuffType.HuiFu, hf);
        };
        // Card_1000033 灵犀剑阵：+防；有剑意 → 剑意全部转为灵气，然后（共鸣 61 生效且本场未触发）返还一半剑意。
        // 没有剑意时原码 `goto end`，共鸣那段也不走。
        ByBase[1000033] = static (src, dst, card) =>
        {
            src.ModifyDef(card.Def);
            if (!src.HasBuff(BuffType.JianYi)) return;
            int jy = src.GetBuffValue(BuffType.JianYi);
            src.ModifyAnima(jy);
            src.RemoveBuff(BuffType.JianYi);
            if (src.IsTalentResonanceEffective(61) && !src.CheckTalentResonanceTempFlag(61))
            {
                src.SetTalentResonanceTempFlag(61, true);
                src.ModifyBuffValue(BuffType.JianYi, jy / 2);
            }
        };

        // ── 「反转出牌方向」三张：切换[反转出牌]并把牌组队列倒序（原码 ReverseCardItems）。sim 早先没有队列倒序。──
        // Card_168 梦蝶游仙：攻击；反转；掷骰 < 10 → 再行动。
        ByBase[168] = static (src, dst, card) =>
        {
            Attack(src, dst, card);
            ToggleReverse(src);
            if (src.GetNextRandomValue(ParamRequest.Percent(168)) < 10) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        };
        // Card_4000066 黄雀在后：反转；[黄雀在后] +o[0]。
        ByBase[4000066] = static (src, dst, card) =>
        {
            ToggleReverse(src);
            src.ModifyBuffValue(BuffType.HuangQueZaiHou, src.Config.CardOtherParams(card.Id).At(0));
        };
        // Card_4000075 梦•黄雀在后：攻击（1 段）；[先机]（本场首次且 ≤元婴，或化神）→ 反转；
        // 用过后招牌：≤金丹 追加 o[0] 攻 1 段；>金丹 追加 o[0] 攻 × 后招牌计数。
        ByBase[4000075] = static (src, dst, card) =>
        {
            int lv = CardTypes.LevelOf(src, card);
            int o0 = src.Config.CardOtherParams(card.Id).At(0);
            CombatMath.Attack(src, dst, card.Attack, 1);
            bool first = src.CurrentCardGrid >= 0 && !src.HadUsed(src.CurrentCardGrid);
            if ((first && lv <= LevelYuanYing) || lv == LevelHuaShen) ToggleReverse(src);
            int hz = src.GetBuffValue(BuffType.HouZhaoPaiJiShu);
            if (lv <= LevelJinDan && hz > 0) CombatMath.Attack(src, dst, o0, 1);
            if (lv > LevelJinDan && hz > 0) CombatMath.Attack(src, dst, o0, hz);
        };

        // Card_4000076 梦•海底捞月：[后招]：o[0] 攻 × o[1]；>金丹 且本格位于后 o[2] 格（grid > 7 − o[2]）时每段多 o[3] 攻。
        ByBase[4000076] = static (src, dst, card) =>
        {
            if (!HouZhaoFunctions.CheckHouZhao(src, card.Id, src.CurrentCardGrid)) return;
            var p = src.Config.CardOtherParams(card.Id);
            int extra = src.Config.CardLevel(card.Id) > LevelJinDan && src.CurrentCardGrid > 7 - p.At(2) ? p.At(3) : 0;
            CombatMath.Attack(src, dst, p.At(0) + extra, p.At(1));
        };
        // Card_7000073 五行天髓诀：+灵气（>0）→ +防（>0）→ 刻印 104 且卡组五行 ≤2 种：[五行天髓诀] +2×o[0]；
        // 否则卡组仅 1 种五行（或仙命 408 且 2 种）：+o[0]。（goto 链：默认段 → IL_00a5 灵气 → IL_0143 防 → IL_01df。）
        ByBase[7000073] = static (src, dst, card) =>
        {
            int o0 = src.Config.CardOtherParams(card.Id).At(0);
            int an = AnimaOf(src, card);
            if (an > 0) src.ModifyAnima(an);
            if (card.Def > 0) src.ModifyDef(card.Def);
            int kinds = WuXingFunctions.GetWuXingCountInDeck(src);
            if (src.Subs.HasKeYinType(src, 104) && kinds <= 2)
                src.ModifyBuffValue(BuffType.WuXingTianSuiJue, o0 * 2);
            else if (kinds == 1 || (src.HasFateStrategy(408) && kinds == 2))
                src.ModifyBuffValue(BuffType.WuXingTianSuiJue, o0);
        };
        // Card_10000062 破茧化蝶：身法 +o[0]；清空负面状态，按清掉的层数 n：体魄 +n×o[1]、生命 +n×o[1]。
        ByBase[10000062] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            src.ModifyBuffValue(BuffType.ShenFa, p.At(0));
            int n = src.RemoveAllDebuff();
            if (n <= 0) return;
            src.ModifyTiPo(n * p.At(1));
            src.ModifyHp(n * p.At(1));
        };

        // Card_10000064 鬼音葬魂：对 [内伤, 破绽, 虚弱, 困缚, 外伤] **逐种**先给自己、再给对方各加 o[0] 层（原码 foreach 内交替）。
        ByBase[10000064] = static (src, dst, card) =>
        {
            int n = src.Config.CardOtherParams(card.Id).At(0);
            foreach (var b in new[] { BuffType.NeiShang, BuffType.PoZhan, BuffType.XuRuo, BuffType.KunFu, BuffType.WaiShang })
            {
                src.ModifyBuffValue(b, n);
                dst.ModifyBuffValue(b, n);
            }
        };
        // Card_99000101 玉露瓶：生命 +o[0]；命元 +o[1]（原码对镜像不加，本 sim 无镜像）。
        ByBase[99000101] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            src.ModifyHp(p.At(0));
            src.ModifyTempLife(p.At(1));
        };
        // Card_99000216 幻羽鹦：正在复制中（计数 > 0）→ 什么都不做；否则计数 +1、生命 +o[0]，
        // 对方同一格有牌 → 临时执行那张牌（再行动 → 外行动）；最后计数 −1。对方格子不存在时直接结束（计数也 −1）。
        ByBase[99000216] = static (src, dst, card) =>
        {
            if (src.GetBuffValue(BuffType.HuanYuYingCiShu) > 0) return;
            src.ModifyBuffValue(BuffType.HuanYuYingCiShu, 1);
            src.ModifyHp(src.Config.CardOtherParams(card.Id).At(0));
            int g = src.CurrentCardGrid;
            if (g >= 0 && g < dst.Board.Count && TriggerCard(src, dst, dst.Board[g].Id))
                src.ModifyBuffValue(BuffType.ExActionAgain, 1);
            src.ModifyBuffValue(BuffType.HuanYuYingCiShu, -1);
        };

        // Card_22 算无遗策：抽取器因 `AddXingWei`（后一格成为星位）判 complex、整卡不注册 → 早先退回默认效果，
        //   灵气 / 卦象 / 星位 / 星力 全丢（yisim 单卡隔离对拍抓到：1 级在纯普攻盘面上看不出，2 级多了「星力 +o[0]」才露馅）。
        // 原码 goto 链：灵气 → 卦象 → AddXingWei(下一格) → o[0]>0 星力 → o[1]>0 上限与血 → 共鸣 121（命元高则加防，并再行动）。
        ByBase[22] = static (src, dst, card) =>
        {
            var o = src.Config.CardOtherParams(card.Id);
            src.ModifyAnima(AnimaOf(src, card));
            src.ModifyBuffValue(BuffType.GuaXiang, src.Config.CardGuaXiang(card.Id));
            GridMarkFunctions.AddXingWei(src, GridFunctions.GetNextGrid(src, src.CurrentCardGrid));
            if (o.At(0) > 0) src.ModifyBuffValue(BuffType.XingLi, o.At(0));
            if (o.At(1) > 0)
            {
                src.ModifyMaxHp(o.At(1));
                src.ModifyHp(o.At(1));
            }
            if (src.IsTalentResonanceEffective(121))
            {
                if (src.TempLife > dst.TempLife) src.ModifyDef(src.Config.ResonanceOtherParams(121).At(0));
                src.ModifyBuffValue(BuffType.ExActionAgain, 1);
            }
        };

        // Card_9000020 冰封血莲：原码「自身减血」包在 `for (i < otherParams[1])` 里（2 级减 2 次、3 级减 3 次），之后护体 +o[1]。
        //   抽取器把循环丢了 —— 1 级 o[1]=1 看不出来，yisim 单卡隔离对拍在 2 / 3 级上抓到。
        ByBase[9000020] = static (src, dst, card) =>
        {
            var o = src.Config.CardOtherParams(card.Id);
            for (int i = 0; i < o.At(1); i++)
            {
                int n = o.At(0);
                if (n >= src.Hp) n = src.Hp - 1;
                if (src.HasBuff(BuffType.HuTi) && n <= 0) n = 1;
                if (n > 0) src.ModifyHp(-n);
            }
            src.ModifyBuffValue(BuffType.HuTi, o.At(1));
        };

        // ── 修为三张：抽取器把 ModifyTempExp / characterUI.exp 全丢了（探针不设修为 / 宗门，B 系列对拍从没触发过）──
        // Card_67：下一格牌与自己同宗门或同职业（都要非无效值）→ 当前修为 −1（原码 84379）。
        ByBase[67] = static (src, dst, card) =>
        {
            int next = GridFunctions.NextGridCard(src, src.CurrentCardGrid)?.Id ?? -1;
            if (next < 0) return;
            int se = src.Config.CardSect(next), ca = src.Config.CardCareer(next);
            if ((se != 0 && se == src.Sect) || (ca != 0 && ca == src.Career)) src.ModifyTempExp(-1);
        };
        // Card_69：命元 −o[0]，当前修为 +o[1]（原码 84615）。
        ByBase[69] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            src.ModifyTempLife(-p.At(0));
            src.ModifyTempExp(p.At(1));
        };
        // Card_1000054 勤拙剑：攻 + 当前修为 / o[0]（原码 93489）。
        ByBase[1000054] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            int bonus = p.At(0) == 0 ? 0 : src.Exp / p.At(0);
            CombatMath.Attack(src, dst, card.Attack + bonus, card.AttackCount);
        };

        // Card_11000025 天星•牵引：灵气 +anima；从**本格**往后扫到卡组末尾（不回绕），每遇到一张带[开局]的牌就触发它 o[1] 次
        //   （触发者格 = 牵引自己的格），累计 o[0] 张就停（原码 IL_0123 / IL_03da / IL_0381）。
        //   抽取器只留下「灵气」，核心的触发整段丢了（partial, skipped=3）—— B7 实机裁决抓到（b7_312）。
        ByBase[11000025] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            src.ModifyAnima(card.Anima);
            int own = src.CurrentCardGrid, count = 0;
            if (own < 0) return;
            for (int i = own; i <= src.Board.Count - 1; i++)
            {
                if (CardOpeningFunctions.HasOpening(src, i))
                {
                    for (int j = 0; j < p.At(1); j++) CardOpeningFunctions.Trigger(src, i, own);
                    count++;
                }
                if (count == p.At(0)) break;
            }
        };

        // Card_11000026 天星•御心：防 +def、生命 +o[2]；从**本格往前**扫到第 0 格，每张带[开局]的牌触发 o[1] 次，累计 o[0] 张就停
        //   （触发者格 = 御心自己的格）。与牵引同型，抽取器同样把触发段丢了（b7_210）。
        ByBase[11000026] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            src.ModifyDef(card.Def);
            src.ModifyHp(p.At(2));
            int own = src.CurrentCardGrid, count = 0;
            if (own < 0) return;
            for (int i = own; i >= 0; i--)
            {
                if (i < src.Board.Count && CardOpeningFunctions.HasOpening(src, i))
                {
                    for (int j = 0; j < p.At(1); j++) CardOpeningFunctions.Trigger(src, i, own);
                    count++;
                }
                if (count == p.At(0)) break;
            }
        };

        // Card_7000047 土灵•韵金：[土灵]：防 +o[0]，然后「失去所有防再加回」；[金灵]：本场每失去过 o[1] 防加 1 锋锐。
        //   原码 goto 链：土灵成立 → 加防 → IL_0143（失去全部防再加回）→ IL_01f8；**不成立直接跳 IL_01f8**。
        //   抽取器把 IL_0143 那段展平成了无条件 → 没激活土灵时也「失去防」，LoseDefCount 虚增，[金灵] 段多给锋锐（b7_87）。
        ByBase[7000047] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            if (WuXingFunctions.CheckWuXing(src, BuffType.JiHuoTuLing))
            {
                src.ModifyDef(p.At(0));
                int def = src.Def;
                if (def > 0)
                {
                    src.ModifyDef(-def);
                    src.ModifyDef(def);
                }
            }
            if (WuXingFunctions.CheckWuXing(src, BuffType.JiHuoJinLing) && src.HasBuff(BuffType.LoseDefCount))
            {
                int n = p.At(1) == 0 ? 0 : src.GetBuffValue(BuffType.LoseDefCount) / p.At(1);
                if (n > 0) src.ModifyBuffValue(BuffType.FengRui, n);
            }
        };

        // Card_10000086 梦•磅礴之势：境界 ≤ 金丹：有气势或灵气 → 体魄 +o[0]、生命 +o[1]；
        //   更高境界：身法 +o[3]，有气势则耗尽气势、体魄 / 生命按气势 × o[1] / o[2] 加（原码 IL_01d4，只从这个分支跳入）。
        //   抽取器把 IL_01d4 展平成了无条件 → 低境界有气势时多加体魄和血（分支标签块扫描抓到）。
        ByBase[10000086] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            int qishi = src.GetBuffValue(BuffType.QiShi);
            int anima = src.Anima;
            if (src.Config.CardLevel(card.Id) <= 3)   // Level.JinDan
            {
                if (qishi > 0 || anima > 0)
                {
                    src.ModifyTiPo(p.At(0));
                    src.ModifyHp(p.At(1));
                }
            }
            else
            {
                src.ModifyBuffValue(BuffType.ShenFa, p.At(3));
                if (qishi > 0)
                {
                    src.ModifyBuffValue(BuffType.QiShi, -qishi);
                    src.ModifyTiPo(qishi * p.At(1));
                    src.ModifyHp(qishi * p.At(2));
                }
            }
        };

        // Card_79 龙：加攻 o[0]、护体 o[1]、[龙] o[2]，再把**每回合再次行动上限**加 o[2]（封顶 8）。
        // 抽取器只认得 ModifyBuffValue，漏了尾部直接写 battleTempData.actionAgainPerRound 的那句（原码 85863）。
        ByBase[79] = static (src, dst, card) =>
        {
            var o = src.Config.CardOtherParams(card.Id);
            src.ModifyBuffValue(BuffType.JiaGong, o.At(0));
            src.ModifyBuffValue(BuffType.HuTi, o.At(1));
            src.ModifyBuffValue(BuffType.Long, o.At(2));
            src.ActionAgainPerRound += o.At(2);
            if (src.ActionAgainPerRound >= 8) src.ActionAgainPerRound = 8;
        };

        // Card_4000033 海底捞月：[后招] 成立 → o[0] 攻（1 段）并再行动；否则**不**再行动。
        // 数据把两条 actionAgain（假 / 真）都无条件执行，结果永远再行动。
        // ⚠ 早先因「v0 / v1 real 相同」被列进 real 可疑名单 —— 但 sim 的 v0 / v1 也相同（盘面差异不影响它），那条理由对它不成立。
        ByBase[4000033] = static (src, dst, card) =>
        {
            if (HouZhaoFunctions.CheckHouZhao(src, card.Id, src.CurrentCardGrid))
            {
                CombatMath.Attack(src, dst, src.Config.CardOtherParams(card.Id).At(0), 1);
                src.CurrentCardActionAgain = true;
            }
            else src.CurrentCardActionAgain = false;
        };

        // Card_7000067：**前→后 单向**相生 → 按后一格牌名激活其元素（无再行动）。
        ByBase[7000067] = static (src, dst, card) =>
        {
            int grid = src.CurrentCardGrid;
            string prev = GridFunctions.CardNameAt(src, GridFunctions.GetPreviousGrid(src, grid));
            string next = GridFunctions.CardNameAt(src, GridFunctions.GetNextGrid(src, grid));
            if (!WuXingFunctions.IsXiangSheng(prev, next, XiangShengZhiHuo(src))) return;
            WuXingFunctions.ActiveWuXingInName(src, next);
            // IL_0248 起（审计补）：用后一格那张牌临时执行一次；它稀有度高于本张时，降到本张的稀有度
            // （`GetCardBaseId(id) + 10000 * 本张 rarity`）。它若再行动 → 转一层外行动。格位不变。
            BattleCard? nc = GridFunctions.NextGridCard(src, grid);
            if (nc is null) return;
            int id = nc.Id;
            var cc = src.Config as Config.JsonBattleConfig;
            int myR = cc?.Card(card.Id)?.Rarity ?? 0;
            int nextR = cc?.Card(id)?.Rarity ?? 0;
            if (nextR > myR) id = CardTypes.BaseId(id) + 10000 * myR;
            if (TriggerCard(src, dst, id)) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        };









        // ── Card_7000098 火灵•焚脉诀：「[灵气]+anima；[加攻]补到 otherParams[0]，每补 1 点生命上限 -otherParams[1]；[火灵]激活时对方生命及上限 -otherParams[2]×[加攻]层数」──
        // 原码 Card_7000098.OnExecuted 状态机（沿 goto 链的执行序）：
        //     src.ModifyAnima(cardConfig.anima);
        //     buffValue = src.GetBuffValue(BuffType.JiaGong);
        //     num2 = cardConfig.otherParams[0] - buffValue;
        //     if (num2 > 0) { src.ModifyBuffValue(JiaGong, num2);
        //                     src.ModifyMaxHpWithFx(-num2 * otherParams[1], showText: true, "FX_5Xing_Huo_Hit"); }
        //     if (CardActionBase.CheckWuXing(src, BuffType.JiHuoHuoLing))
        //     {   buffValue = src.GetBuffValue(BuffType.JiaGong);
        //         dst.ModifyHp(-buffValue * otherParams[2]);
        //         dst.ModifyMaxHpWithFx(-buffValue * otherParams[2], showText: true, "FX_5Xing_Huo_Hit"); }
        // ⚠ 抽取器丢了两条（`droppedExpr=2, skipped=4`）：`buffValue` 是**先声明后赋值**的裸局部变量
        //   （`int buffValue;` 在 try 顶、赋值在中途），不在它的 vars 表里 → 整段消失。
        // ⚠ `ModifyMaxHpWithFx` 带 FX 后缀，抽取器的规则只认 `ModifyMaxHp`（全库 8 张卡用它）。
        //   语义相同：`ModifyMaxHp` 末尾 `if (Hp > MaxHp) Hp = MaxHp;` 已忠实照抄。
        // ⚠ 自伤那一段**没有** `ModifyHp` —— 它只削上限（当前血靠 clamp 掉），别自作主张补一条掉血。
        ByBase[7000098] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            src.ModifyAnima(card.Anima);
            int num2 = p.At(0) - src.GetBuffValue(BuffType.JiaGong);
            if (num2 > 0)
            {
                src.ModifyBuffValue(BuffType.JiaGong, num2);
                src.ModifyMaxHp(-num2 * p.At(1));
            }
            if (src.Subs.CheckWuXing(src, BuffType.JiHuoHuoLing))
            {
                int bv = src.GetBuffValue(BuffType.JiaGong);
                dst.ModifyHp(-bv * p.At(2));
                dst.ModifyMaxHp(-bv * p.At(2));
            }
        };

        // ── Card_4000097 弯弓射虎：「[先机]：生命及上限+otherParams[0]；[后招]：attack 攻（每加过 otherParams[1] 点生命多 1 攻）；otherParams[2]% 概率[再次行动]」──
        // 原码 Card_4000097.OnExecuted 状态机：
        //     if (!cardItem.hadUsed) { src.ModifyMaxHp(o[0]); src.ModifyHp(o[0]); }          // [先机]
        //     if (!src.HasBuff(BuffType.AddHpCount) || !CheckHouZhao(src, cardItem)) break;
        //     num2 = Mathf.FloorToInt(src.GetBuffValue(AddHpCount) / o[1]);
        //     src.Attack(dst, cardConfig.attack + num2);                                     // ← 唯一的攻击
        //     ...（break 之后，两条路都到）
        //     if (src.GetNextRandomValue() < o[2]) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        // ⚠ **这张卡没有无条件攻击**：a=8 的 8 攻只挂在 [后招] 分支上。卡面已核实
        //   （「[后招]：{attack}攻（每加过{otherParams[1]}生命多1攻）」）。
        // ⚠ `||` 短路：没有 AddHpCount 时 **CheckHouZhao 不会被调用** —— 它的副作用（挂 ShiYongHouZhao、
        //   首用天赋）不能提前发生。顺序照抄。
        // ⚠ `GetBuffValue` 与 `otherParams[1]` 都是 int → C# 整除；原码只是对 int 结果再 FloorToInt。
        ByBase[4000097] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            int grid = src.CurrentCardGrid;
            if (!src.HadUsed(grid))                     // [先机]
            {
                src.ModifyMaxHp(p.At(0));
                src.ModifyHp(p.At(0));
            }
            if (src.HasBuff(BuffType.AddHpCount)
                && HouZhaoFunctions.CheckHouZhao(src, card.Id, grid))
            {
                int per = p.At(1);
                int extra = per > 0 ? src.GetBuffValue(BuffType.AddHpCount) / per : 0;
                CombatMath.Attack(src, dst, card.Attack + extra, 1);
            }
            if (src.GetNextRandomValue(ParamRequest.Percent(4000097)) < p.At(2))
                src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        };

        // ── Card_10000090 醉拳架势：「[体魄]+physique；生命+otherParams[0]；获得 otherParams[1] 层[破绽]；[持续]（不可叠加）：每失去 1 层[破绽]获得 otherParams[2] 层[加攻]」──
        // 原码 Card_10000090.OnExecuted（沿 goto 链的执行序）：
        //     physique = cardConfig.physique;
        //     if (physique > 0) src.ModifyTiPo(physique);
        //     num3 = cardConfig.otherParams[0];
        //     if (num3 > 0) src.ModifyHp(num3);
        //     num2 = cardConfig.otherParams[1];
        //     if (num2 > 0) src.ModifyBuffValue(BuffType.PoZhan, num2);
        //   // switch 之后（IL_01f8 收尾，**三条都走完才到这里**）：
        //     if (cardConfig.rarity >= 2 || src.GetBuffValue(BuffType.ZuiQuanJiaShi) == 0)
        //         src.ModifyBuffValue(BuffType.ZuiQuanJiaShi, cardConfig.otherParams[2]);
        // ⚠ 抽取器 `skipped=1` 丢的正是最后那条 —— 它的条件是 `A || B(调用)`，复合条件解析不出来。
        //   这条丢了 = 「[持续]」整句消失 → 醉拳架势挂不上 `ZuiQuanJiaShi`，
        //   而**读取侧 sim 早就有**（CombatantBuffs 287：`delta<0 && PoZhan && ZuiQuanJiaShi>0` → 加攻），
        //   于是「每失去 1 层破绽就加 1 层加攻」静默失效。又一次「读侧有、写侧丢」。
        // ⚠ 三条 hp/tipo/PoZhan 都带 `> 0` 门（原码就是 `if (x > 0)`），不能省。
        // ⚠ 「不可叠加」的语义就在这里：**rarity >= 2 时无视已有层数照样挂**（覆盖），
        //   否则只在当前为 0 时挂。别写成无条件的 `ModifyBuffValue`。
        ByBase[10000090] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            if (card.Physique > 0) src.ModifyTiPo(card.Physique);
            if (p.At(0) > 0) src.ModifyHp(p.At(0));
            if (p.At(1) > 0) src.ModifyBuffValue(BuffType.PoZhan, p.At(1));
            if (src.Config.CardRarity(card.Id) >= 2 || src.GetBuffValue(BuffType.ZuiQuanJiaShi) == 0)
                src.ModifyBuffValue(BuffType.ZuiQuanJiaShi, p.At(2));
        };

        // ── Card_4000053 投石问路：攻击；「施加 0~o0 层虚弱」「施加 0~o1 层破绽」各取一次 GetNextRandomValue，**> 0 才施加** ──
        // 原码 104869 / 104887 两步都是 `if (randomValue > 0)`。数据驱动那份把虚弱那步的守卫丢了（`__if1` 算了却没挂到 op 上）：
        // 真参数不会是负数，对拍一直没暴露；但估值 / 空队列给 -1 时会对敌方 ModifyBuffValue(虚弱, -1)，反而减掉敌方一层虚弱。
        ByBase[4000053] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            Attack(src, dst, card);
            int xuRuo = src.GetNextRandomValue(ParamRequest.Range(4000053, 0, p.At(0)));
            if (xuRuo > 0) dst.ModifyBuffValue(BuffType.XuRuo, xuRuo);
            int poZhan = src.GetNextRandomValue(ParamRequest.Range(4000053, 0, p.At(1)));
            if (poZhan > 0) dst.ModifyBuffValue(BuffType.PoZhan, poZhan);
        };

        // ── Card_4000031 白蛇吐信：「otherParams[0]% 概率施加 otherParams[1] 层[破绽]；attack 攻」──
        // 原码 Card_4000031.OnExecuted：
        //     if (src.GetNextRandomValue() < cardConfig.otherParams[0])       // ← 10 = 概率门
        //         dst.ModifyBuffValue(BuffType.PoZhan, cardConfig.otherParams[1]);
        //     await src.Attack(dst, cardConfig.attack, cardConfig.attackCount);   // ← IL_013e，两条路都到
        // ⚠ 抽取器把 `[破绽]` 那条抽成了**无条件**（`partial=False`，看不出问题）——
        //   这个形状（if 里先 `await UniTask.Delay` 再执行效果）破坏了它的括号配对，
        //   于是 condition 整个丢了。**静默错**是最坏的一类。
        //   全库同型的只有 3 张（42 / 11000026 / 4000031），另两张是 partial 所以只是少效果、不会错。
        // ⚠ 判据来源：yisim 第三方仲裁 —— `b5_4000031_0` real 13/-2、**sim 25/-2**、**yisim 13/-2**，
        //   yisim 站 real ⇒ sim 有 bug（`tools/refs/card-pipeline/yisim_arb.py arbitrate 4000031`）。
        //   机理对得上：破绽无条件多施 → 对方掉血快 → 打得短 → **左血偏高**（sim 25 > real 13），
        //   而右侧两种情形都会被打死，所以 R 三边一致。
        ByBase[4000031] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            if (src.GetNextRandomValue(ParamRequest.Percent(4000031)) < p.At(0))
                dst.ModifyBuffValue(BuffType.PoZhan, p.At(1));
            CombatMath.Attack(src, dst, card.Attack, card.AttackCount);
        };

        // ── Card_8000012 回响阵纹：「若第一格为持续牌，重复其效果」──
        // 原码 Card_8000012.OnExecuted（IL_00a6 起）：
        //     orignId = cardConfig.id; orignSkinId = cardItem.skinId;
        //     num2 = src.GetBattleDeckIdList()[0];                       // ← **牌组第一格**的牌
        //     if (cardConfigDict[num2].cardType == CardType.Sustain)
        //     {
        //         for (i < otherParams[0]) { if (cardConfigDict[num2].noUpgrade) break; num2 += 10000; }  // 升到最高档
        //         cardItem.InitData(num2, …);
        //         await CardFactory.FindCardAction(cardItem).ExecuteEffect(src, dst, isTempCard: true);    // 重复它的效果
        //     }
        //     …
        //     if (cardItem.cardConfig.actionAgain) ModifyBuffValue(ExActionAgain, 1);   // ← 查的是**临时牌**的 actionAgain
        //     cardItem.InitData(orignId, …);                                            // 之后才恢复原牌
        // ⚠ 抽取器把 `num2` 抽丢了（它是**重排段里的裸赋值**），ops 里成了 `usecard(num2)` 引用**未定义变量** ——
        //   而 sim 求值未定义变量**静默返回 0** → `usecard(0)` = 触发「普通攻击」，
        //   于是这张卡从「重复第一格持续牌」变成「**白打一下普攻**」。
        ByBase[8000012] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            if (src.Board.Count > 0)
            {
                int num2 = src.Board[0].Id;                      // GetBattleDeckIdList()[0]
                if (src.Config.CardIsSustain(num2))
                {
                    for (int i = 0; i < p.At(0); i++)
                    {
                        if (!src.Config.CanUpgrade(num2)) break;
                        num2 += 10000;                           // 升到最高稀有度档
                    }
                    // 原码 IL_02bd：只在**执行了**临时牌的分支里判它的 actionAgain（读在还原原牌之前）。
                    if (TriggerCard(src, dst, num2)) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
                }
            }
        };

        // ── Card_1000064 连环剑阵：「[防]+def；重复卡组中上一张'剑阵'牌的效果」──
        // 原码 Card_1000064.OnExecuted（IL_00d5 起）：
        //     src.ModifyDef(cardConfig.def);
        //     orignId = cardConfig.id; orignSkinId = cardItem.skinId;
        //     num2 = cardItem.gridNumber;
        //     if (HasBuff(LianHuanTempPos)) num2 = GetBuffValue(LianHuanTempPos) - 1;   // ← 上回找到的位置（游标）
        //     num3 = -1;
        //     battleDeckIdList = src.GetBattleDeckIdList();
        //     for (num4 = num2 - 1; num4 >= 0; num4--)                                   // **从后往前**找
        //         if (src.IsJianZhen(cardConfigDict[battleDeckIdList[num4]], ignoreTempBuff: true))
        //         { num3 = battleDeckIdList[num4]; src.SetBuffValue(LianHuanTempPos, num4 + 1); break; }
        //     if (num3 != -1) { cardItem.InitData(num3, …); await ExecuteEffect(isTempCard: true); }
        //   IL_035e（收尾）：src.RemoveBuff(LianHuanTempPos);                            // ← 游标**用完就清**
        //                    if (cardItem.cardConfig.actionAgain) ModifyBuffValue(ExActionAgain, 1);
        //                    cardItem.InitData(orignId, …);
        // ⚠ 同 8000012：`num3` 被抽丢 → `usecard(num3)` 静默变 0 → 白打一下普攻；
        //   而且 ops 把 `LianHuanTempPos` 抽成了 **removebuff**（没有读）—— 那个游标是**搜索起点**，不读就等于每次都从头找。
        ByBase[1000064] = static (src, dst, card) =>
        {
            src.ModifyDef(card.Def);
            int start = src.CurrentCardGrid;
            if (src.HasBuff(BuffType.LianHuanTempPos))
                start = src.GetBuffValue(BuffType.LianHuanTempPos) - 1;
            for (int i = start - 1; i >= 0; i--)
            {
                if (i >= src.Board.Count) continue;
                int id = src.Board[i].Id;
                if (!CardTypes.IsJianZhen(src, id)) continue;
                src.SetBuffValue(BuffType.LianHuanTempPos, i + 1);
                bool tempAg = TriggerCard(src, dst, id);         // ExecuteEffect(isTempCard: true)
                // 原码 IL_035e（**只在找到剑阵并执行了之后**才走到这里）：
                src.RemoveBuff(BuffType.LianHuanTempPos);
                if (tempAg) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
                break;
            }
        };

        // ── Card_179 鸿蒙镇天钟：「[蓄灵]；从卡组第一格开始直至此牌，依次重复每一张**已用过的**牌的效果」──
        // 原码 Card_179.OnExecuted：
        //     if (src.GetBuffValue(BuffType.DongHuangZhongTimes) > 0) { 显示「不可循环复制」; return; }   // ← **防递归守卫**
        //     src.ModifyBuffValue(BuffType.DongHuangZhongTimes, 1);
        //     orignId/orignSkinId/orignGridNumber = …
        //     for (__CG_i = 0; __CG_i < orignGridNumber; __CG_i++)
        //         if (src.GetBattleDeckCardItemList()[__CG_i].hadUsed)              // ← **只重放「出过」的格**
        //         {
        //             int id = …[__CG_i].cardConfig.id;
        //             cardItem.InitData(id, …); cardItem.gridNumber = __CG_i;       // ← 把格位设成那一格
        //             await ExecuteEffect(src, dst, isTempCard: true);
        //         }
        //     cardItem.gridNumber = orignGridNumber;
        //     收尾：ModifyBuffValue(DongHuangZhongTimes, -1);
        // ⚠ 抽取器丢了两样：① **防递归守卫**（`>0` 就整个不执行）；② 循环里那一整段（`id` 成了未定义变量
        //   → `usecard(id)` 静默变 0 → 变成白打一下普攻）。
        // ⚠ `HadUsed(grid)` 在 sim 里就是 `_gridUsed` 数组（`SetHadUsed` 在出牌收尾时置位）✓。
        // ⚠ 重放时要把 `CurrentCardGrid` 临时改成被重放的那一格 —— 原码改的是 `cardItem.gridNumber`，
        //   而格位是「星位加星力」「相邻效果」等一堆判定的输入，不改就全错。
        ByBase[179] = static (src, dst, card) =>
        {
            if (src.GetBuffValue(BuffType.DongHuangZhongTimes) > 0) return;      // 防递归
            src.ModifyBuffValue(BuffType.DongHuangZhongTimes, 1);
            int orignGrid = src.CurrentCardGrid;
            for (int i = 0; i < orignGrid; i++)
            {
                if (i < 0 || i >= src.Board.Count) continue;
                if (!src.HadUsed(i)) continue;                                   // 只重放已用过的格
                int saved = src.CurrentCardGrid;
                src.CurrentCardGrid = i;                                         // 原码 cardItem.gridNumber = i
                // 原码 IL_03c5：**每重放一张**就判一次那张临时牌的 actionAgain（读在还原原牌之前）。
                if (TriggerCard(src, dst, src.Board[i].Id)) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
                src.CurrentCardGrid = saved;
            }
            src.ModifyBuffValue(BuffType.DongHuangZhongTimes, -1);
        };

        // ── Card_7000054 混元守护：「[防]+def；若已激活至少 2 种[五行]，再加 otherParams[0] 生命及上限」──
        // 原码 Card_7000054.OnExecuted：
        //     IL_009c: src.ModifyDef(cardConfig.def);
        //     IL_0128: if (card_.GetWuXingActiveCount(src) >= 2)
        //              { src.ModifyMaxHp(cardConfig.otherParams[0]); goto IL_01c7; }
        //              goto end_IL_000e;                 // ← 不满足直接**结束**
        //     IL_01c7: src.ModifyHp(cardConfig.otherParams[0]);   // ← **在 if 里面**
        // ⚠ 抽取器把 `hp` 抽成了**无条件**（`maxhp` 带门、`hp` 不带）—— 又是「else/收尾段被展平」那一类。
        //   后果：没有五行激活时 sim 白送 6 点生命（real 20 vs sim 38，R 却完全对得上）。
        ByBase[7000054] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            src.ModifyDef(card.Def);
            if (WuXingFunctions.GetWuXingActiveCount(src) >= 2)
            {
                src.ModifyMaxHp(p.At(0));
                src.ModifyHp(p.At(0));
            }
        };

        // ── Card_99000110 金元三尖枪：「attack 攻×attackCount；**每次攻击后**施加 1 层[破绽]」──
        // 原码 Card_99000110.OnExecuted 的循环（沿 goto 链）：
        //     IL_019e: if (__CG_i < cardConfig.attackCount) { await src.Attack(dst, cardConfig.attack); goto IL_009c; } break;
        //     IL_009c/IL_010b: dst.ModifyBuffValue(BuffType.PoZhan, 1);
        //     IL_0185: __CG_i++; goto IL_019e;
        // 即 `for (i < attackCount) { Attack(attack); 对方 PoZhan+1 }`。
        // ⚠ 抽取器把循环**展平成一个 pass**（`attack` 带 `cond i<attackCount`、后面是 `PoZhan`、`i++`），
        //   而 sim 的求值端**不回头**，等于只出了一次 —— 卡 99000110 偏差 36
        //   （real 13/-2 vs sim 4/10：对方少挨 2 轮、破绽也少 2 层）。
        // ⚠ 段数用 `card.AttackCount` 循环、每轮 `Attack(attack)` 是**单段**（原码 2 参重载）。
        // ⚠ `attackCount == 0` 时循环不执行 —— 与原码 `if (i < 0)` 一致，别「顺手」归一成 1。
        ByBase[99000110] = static (src, dst, card) =>
        {
            for (int i = 0; i < card.AttackCount; i++)
            {
                CombatMath.Attack(src, dst, card.Attack, 1);
                dst.ModifyBuffValue(BuffType.PoZhan, 1);
            }
        };

        // ── Card_76 魔龙之爪：「attack 攻×attackCount；**每次攻击后**施加 otherParams[0] 层[外伤]」──
        // 原码 Card_76.OnExecuted（goto 链）：`__CG_i = 0; IL_01ae: if (i < attackCount) { await Attack(dst, attack); … }
        //   IL_010b: dst.ModifyBuffValue(WaiShang, o[0]); IL_0195: i++; goto IL_01ae;`
        // ⚠ 抽取器把它展平成一个 pass（`attack` 带 `cond i<attackCount`、后跟 `WaiShang`、`i++`），
        //   而求值端不回头 → 只出一次。card 76 偏差 35（real 52/-11 vs sim 40/-5）。
        ByBase[76] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            for (int i = 0; i < card.AttackCount; i++)
            {
                CombatMath.Attack(src, dst, card.Attack, 1);
                dst.ModifyBuffValue(BuffType.WaiShang, p.At(0));
            }
        };

        // ── Card_105 板栗粽：「[咸粽] 耗 1[灵气]：将 1 层[负面状态]转为[食欲]；重复 otherParams[0] 次：生命+otherParams[1]」──
        // 原码 Card_105.OnExecuted 的循环：`__CG_i = 0; if (i < o[0]) { src.ModifyHp(o[1]); … } __CG_i++;`
        // ⚠ 同 76：展平成一遍 → 生命只加 1 次（应加 o[0]=3 次）。卡 105 偏差 36。
        // ⬜ 「耗 1 灵气把 1 层负面状态转成食欲」那半句抽取器**没抽出来**（`skipped=3`）——
        //   本卡注册**只补循环**，那半句仍然缺（与注册前一致，不会变差）；B5 盘面左侧无负面状态，影响为零。
        ByBase[105] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            // IL_0098（审计补）：耗 1 灵气 → 抽一个玩家参数，那种 buff −1、食欲 +1。
            if (src.TryCostAnima(1) > 0)
            {
                int b = src.NextParam(ParamRequest.OwnDebuff(105));
                if (b != -1)
                {
                    src.ModifyBuffValue((BuffType)b, -1);
                    src.ModifyBuffValue(BuffType.ShiYu, 1);
                }
            }
            for (int i = 0; i < p.At(0); i++) src.ModifyHp(p.At(1));
        };

        // ── Card_10000081 梦•迎风掌：「attack 攻×attackCount；已加过[防]则追加 otherParams[0] 攻」──
        // 原码 Card_10000081.OnExecuted：
        //     int num2 = 0;
        //     if (cardConfig.level <= Level.YuanYing)
        //     { await src.Attack(dst, cardConfig.attack, cardConfig.attackCount); goto IL_00ae; }   // ← 跳出，**跳过 else 段**
        //     num2 += Mathf.FloorToInt(src.battleTempData.def / cardConfig.otherParams[0]);
        //     await src.Attack(dst, cardConfig.attack, cardConfig.attackCount + num2);
        //   IL_00ae:
        //     if (src.GetBuffValue(BuffType.JiLuJiaFang) > 0)
        //         await src.Attack(dst, cardConfig.otherParams[0], cardConfig.otherParams[1]);
        // ⚠ 抽取器把 `num2 += …` 与**第二条 attack** 都抽成了**无条件**（它靠字面 `else` 取反，
        //   而这里是 `goto IL_00ae` 跳出去的）→ 低境界时 sim **打了两轮**：多出的那一轮段数 = def/o[0]。
        //   卡 10000081 偏差 36（real 13/-2 vs sim 28/-11）。
        // ⚠ `IL_00ae` 那一下是**两条路都会到**的（在 if/else 之外），所以它写在 if/else 之后。
        // ⚠ `JiLuJiaFang`（本场累计加过的防）是那个门的唯一输入 —— 它本身在 ops 里是被 `buffval` 读的✓。
        ByBase[10000081] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            if (src.Config.CardLevel(card.Id) <= LevelYuanYing)
            {
                CombatMath.Attack(src, dst, card.Attack, card.AttackCount);
                // IL_00ae：只有 ≤元婴 这支会走到（另一支 `break` 出 switch 直接结束，审计 2026-09-22 更正）。
                if (src.GetBuffValue(BuffType.JiLuJiaFang) > 0)
                    CombatMath.Attack(src, dst, p.At(0), p.At(1));
            }
            else
            {
                int num2 = src.Def / p.At(0);                 // FloorToInt(def / o[0])
                CombatMath.Attack(src, dst, card.Attack, card.AttackCount + num2);
            }
        };

        // 卡 410（像「混元化灵」7000101 的旧版）：代码还在，但**当前所有配置表都没有 id 410**（2026-09-22 查证，
        // 热更 DLL 与本机 refs 同 hash）—— 已下线的牌。用户决定不再支持：不注册，b4 里带它的坏样本已从对拍数据剔除。

        // ── Card_32 双鸳逆克：「[灵气]+anima；[防]+def；若**前一格克制后一格**，则激活后一格的五行并再次行动」──
        // 原码 Card_32.OnExecuted：
        //     name  = GetPreviousGridCardConfig(grid).name;
        //     name2 = GetNextGridCardConfig(grid).name;
        //     if (card_.IsKeZhi(name, name2))
        //     {   按 name2 逐个 `if (Contains("金灵")) JiHuoJinLing+1;` … 木/水/火/土 …
        //         goto IL_039f; }
        //     cardConfig.actionAgain = false; break;
        //   IL_039f: cardConfig.actionAgain = true;                 // ← **两条分支都赋值**
        // ⚠ 抽取器给出的 ops 是 `anima` / `def` / `actionagain(0)` / `actionagain(1)` —— 两个 actionagain
        //   串起来是「先置 false 再置 true」，**恒等于 true** → sim 里这张卡**无条件再次行动**，
        //   而真实的门是「前一格五行克制后一格」（`skipped=5` 丢的就是那个 `if` 和里面的激活）。
        // ⚠ 注意条件方向是 `IsKeZhi(name, name2)`（**前克后**），不是双向；与本文件 `ByBase[295]` 的相生不同。
        ByBase[32] = static (src, dst, card) =>
        {
            src.ModifyAnima(AnimaOf(src, card));
            src.ModifyDef(card.Def);
            int grid = src.CurrentCardGrid;
            string prev = GridFunctions.CardNameAt(src, GridFunctions.GetPreviousGrid(src, grid));
            string next = GridFunctions.CardNameAt(src, GridFunctions.GetNextGrid(src, grid));
            if (WuXingFunctions.IsKeZhi(prev, next))
            {
                WuXingFunctions.ActiveWuXingInName(src, next);   // 按**后一格**的名字激活
                src.CurrentCardActionAgain = true;               // IL_039f
            }
            else
            {
                src.CurrentCardActionAgain = false;
            }
        };

        // ── Card_73 疯魔：「获得 otherParams[0] 层[冥] 和 otherParams[1] 层随机[负面状态]；[体魄]+physique
        //    （每有 1 层[负面状态]多 1，最多加 otherParams[2]）」──
        // 原码 Card_73.OnExecuted：
        //     ModifyBuffValue(BuffType.Min, otherParams[0]);
        //     ModifyBuffValue((BuffType)nextParam, 1);        // ← 玩家参数队列抽一个负面 buff
        //     num2 = cardConfig.physique + src.GetDebuffCount();
        //     if (num2 > otherParams[2]) num2 = otherParams[2];       // 封顶 o[2]=10
        //     src.ModifyTiPo(num2);
        // ⚠ 抽取器把最后三行整段丢了（`skipped=2`，`raw` 里却留着 `ModifyTiPo(num2)`）→ **体魄一点不加**。
        // ⚠ `nextParam` 那半句本层拿不到（玩家选牌参数队列为空 → `NextParam()` 返回 -1），
        //   与原数据驱动路径一致：**不会更差**。B5 参数队列本来就是空的。
        // ⚠ `num2` 用的是**加上随机负面状态之后**的 `GetDebuffCount()` —— 顺序不能换。
        ByBase[73] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            src.ModifyBuffValue(BuffType.Min, p.At(0));
            for (int i = 0; i < p.At(1); i++)          // IL_0172：重复 o[1] 次（审计更正，早先只抽一次）
            {
                int np = src.NextParam(ParamRequest.FromDebuffPool(73, ParamDebuffPool.Seven));
                if (np != -1) src.ModifyBuffValue((BuffType)np, 1);
            }
            int num2 = card.Physique + src.GetDebuffCount();
            if (num2 > p.At(2)) num2 = p.At(2);
            src.ModifyTiPo(num2);
        };

        // ── Card_7000065 五行绽放：「使用全部[五行]灵印 / [再次行动] / [消耗]」──
        // 原码 Card_7000065.OnExecuted（IL_0234 的循环）：
        //     __CG_lingYinId_5__2 = new int[5] { 7000003, 7000009, 7000011, 7000001, 7000006 };   // ← 五张灵印，**顺序写死**
        //     for (__CG_i = 0; __CG_i < 5; __CG_i++)
        //     {
        //         cardItem.InitData(__CG_lingYinId_5__2[__CG_i] + 10000 * cardConfig.rarity);     // 换成那一档灵印
        //         await CardFactory.FindCardAction(cardItem).ExecuteEffect(src, dst, isTempCard: true);
        //     }
        // 即「**依次执行五张灵印各自的效果**」（灵印是稀有度变体，跟本牌的 rarity 走）。
        // ⚠ 抽取结果几乎是空的（`skipped=1`，raw 也是空）—— 整张卡在 sim 里**什么都没做**（偏差 33）。
        //   和卡 322 / 179 / 8000012 是同一类：**循环 + 临时换牌执行**，抽取器看不进 `InitData`+`ExecuteEffect` 那一段。
        // ⚠ 五张 id 的**顺序照抄**（木→?→土→金→水），不是按 7000001..7000005 排的。
        ByBase[7000065] = static (src, dst, card) =>
        {
            int rarity = src.Config.CardRarity(card.Id);
            int[] lingYin = { 7000003, 7000009, 7000011, 7000001, 7000006 };
            for (int i = 0; i < lingYin.Length; i++)
                TriggerCard(src, dst, lingYin[i] + 10000 * rarity);   // ExecuteEffect(isTempCard: true)
        };

        // ── Card_5000014 转弦合调：「[灵气]+anima；若**使用过琴师牌**或**后一格为琴师牌**则[再次行动]」──
        // 原码 Card_5000014.OnExecuted：
        //     src.ModifyAnima(cardConfig.anima);
        //     if (src.HasBuff(QinShiPai) || GetNextGridCardConfig(src, gridNumber).career == Career.QinShi)
        //          cardConfig.actionAgain = true;  else  cardConfig.actionAgain = false;     // ← 两支都赋值
        //     src.ModifyBuffValue(BuffType.QinShiPai, 1);                                   // ← 判完**之后**才记自己
        // ⚠ 抽取器把中间那段 if/else 整条丢了（`skipped=2`）→ 永不再行动。
        // ⚠ 「使用过琴师牌」判的是**判定时**的 QinShiPai —— 自己那一层在判完之后才加，所以首次打出不算自己。
        // ⚠ career 取自配置 f5（Career.QinShi = 3），2026-09-23 补抽。
        ByBase[5000014] = static (src, dst, card) =>
        {
            src.ModifyAnima(AnimaOf(src, card));
            int next = GridFunctions.GetNextGrid(src, src.CurrentCardGrid);
            int nextId = next >= 0 && next < src.Board.Count ? src.Board[next].Id : 0;
            src.CurrentCardActionAgain = src.HasBuff(BuffType.QinShiPai) || src.Config.CardCareer(nextId) == CareerQinShi;
            src.ModifyBuffValue(BuffType.QinShiPai, 1);
        };

        // ── Card_7000080 梦•木灵柳纷飞：「生命+o[0]；每有 1 层[水势]，下次攻击就多 1 攻」──
        // 原码 Card_7000080.OnExecuted（沿 goto 链：IL_0098 → IL_012d → 收尾）：
        //     src.ModifyHp(o[0]);
        //     if (level <= JinDan) ModifyBuffValue(XiaCiGongJiDuoGong, GetBuffValue(ShuiShi));
        //     else { num2 = FloorToInt(ShuiShi / o[1]); if (num2 > o[2]) num2 = o[2]; ModifyBuffValue(JiaGong, num2); }
        //     cardConfig.actionAgain = (ShuiShi > 0 || JiaGong > 0) && level > JinDan;
        // ⚠ 抽取结果被标成 `complex:expr` → **整卡不注册、退回默认效果**（a=0 → 什么都不做，连 +生命 都没有）。
        //   我一度只看 `partial=False` 就以为它抽对了 —— 其实 `complex` 才是那个标志。
        //   装载器现在会把这类卡列进 `CardEffectData.SkippedIds`（Bench oracle 输出第一段里能看到）。
        ByBase[7000080] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            bool high = src.Config.CardLevel(card.Id) > LevelJinDan;
            src.ModifyHp(p.At(0));
            if (!high)
            {
                src.ModifyBuffValue(BuffType.XiaCiGongJiDuoGong, src.GetBuffValue(BuffType.ShuiShi));
            }
            else
            {
                int per = p.At(1);
                int num2 = per > 0 ? src.GetBuffValue(BuffType.ShuiShi) / per : 0;
                if (num2 > p.At(2)) num2 = p.At(2);
                src.ModifyBuffValue(BuffType.JiaGong, num2);
            }
            src.CurrentCardActionAgain = (src.GetBuffValue(BuffType.ShuiShi) > 0 || src.GetBuffValue(BuffType.JiaGong) > 0) && high;
        };

        // ── Card_173 瑶光溯时镜：「[激活]全部[五行]；复制后一格牌的效果；[再次行动]（不会被其他效果阻止）」──
        // 原码 Card_173.OnExecuted（goto 链）：
        //     if (GetBuffValue(KunLunJingTimes) > 0) goto end;          // 防递归（复制到另一面镜子时不再展开）
        //     ModifyBuffValue(KunLunJingTimes, 1);
        //     IL_018b: 激活 木/火/土/金/水 五行
        //     IL_0253: id = GetNextGridCardConfig(src, grid).id;  if (id == -1) break;
        //              cardItem.InitData(id) → ExecuteEffect(src, dst, isTempCard: true) → 还原原牌/格位
        //     收尾：ModifyBuffValue(WuShiKunXianKunFu, 1);  ModifyBuffValue(KunLunJingTimes, -1);
        // ⚠ 抽取结果的 ops **顺序是乱的**：`usecard(var id)` 排在定义它的 `let` 之前 → 用的时候 `id` 未定义 →
        //   静默为 0 → 「复制后一格」变成了「打一下普攻」。
        // ⚠ `WuShiKunXianKunFu` 就是卡面「再次行动（不会被其他效果阻止）」的那半句（行动权判定里的 flag2 豁免）。
        ByBase[173] = static (src, dst, card) =>
        {
            if (src.GetBuffValue(BuffType.KunLunJingTimes) > 0) return;
            src.ModifyBuffValue(BuffType.KunLunJingTimes, 1);
            src.ModifyBuffValue(BuffType.JiHuoMuLing, 1);
            src.ModifyBuffValue(BuffType.JiHuoHuoLing, 1);
            src.ModifyBuffValue(BuffType.JiHuoTuLing, 1);
            src.ModifyBuffValue(BuffType.JiHuoJinLing, 1);
            src.ModifyBuffValue(BuffType.JiHuoShuiLing, 1);
            int next = GridFunctions.GetNextGrid(src, src.CurrentCardGrid);
            if (next >= 0 && next < src.Board.Count)
            {
                // 原码只 InitData(后一格的 id)，**不改 gridNumber**（存了 originGridNumber 只为还原）——
                // 被复制的牌用 173 自己的格位判星位 / 相邻（审计 2026-09-22 更正）。
                TriggerCard(src, dst, src.Board[next].Id);
            }
            src.ModifyBuffValue(BuffType.WuShiKunXianKunFu, 1);
            src.ModifyBuffValue(BuffType.KunLunJingTimes, -1);
        };

        // ── Card_220 赤金盘龙棍：「[拳]：[气势]+o[0]，每 o[1] 体魄加 1 生命；[棍]：attack(+体魄/o[2]) 攻×attackCount；使用后[切换架势]」──
        // 原码（goto 链）：if (HasBuff(QuanJiaShi)) → IL_01bc: QiShi += o[0]; num2 = TiPo / o[1]; if (num2 > 0) ModifyHp(num2);
        //                  IL_0293（两路汇合）: if (!HasBuff(GunJiaShi)) break; Attack(attack + TiPo / o[2], attackCount);
        //                  收尾: SwitchJiaShi(src);
        // ⚠ 与 222 同病：抽取器把「[拳]」分支当成无条件（没有架势时白给气势）。
        ByBase[220] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            if (src.HasBuff(BuffType.QuanJiaShi))
            {
                src.ModifyBuffValue(BuffType.QiShi, p.At(0));
                int num2 = p.At(1) > 0 ? src.TiPo / p.At(1) : 0;
                if (num2 > 0) src.ModifyHp(num2);
            }
            if (src.HasBuff(BuffType.GunJiaShi))
            {
                int num3 = p.At(2) > 0 ? src.TiPo / p.At(2) : 0;
                CombatMath.Attack(src, dst, card.Attack + num3, card.AttackCount);
            }
            SwitchJiaShi(src);
        };

        // ── Card_222 转势：「[身法]+o[0]；[拳]：多加 o[1] [身法]；[棍]：attack 攻；使用后[切换架势]」──
        // 原码 Card_222.OnExecuted（goto 链）：
        //     if (HasBuff(QuanJiaShi)) → IL_01b9: ModifyBuffValue(ShenFa, o[0] + o[1]);          // 拳
        //     if (HasBuff(GunJiaShi))  → ModifyBuffValue(ShenFa, o[0]); Attack(attack, attackCount); // 棍
        //     IL_0320: card_.SwitchJiaShi(src);                                                       // 两支都到
        // ⚠ 抽取结果把拳分支那句 `ShenFa += o[0]+o[1]` 当成了**无条件**，又不认识 SwitchJiaShi（`partial`）。
        //   **没有架势时两支都不走**（探针角色不是拳师，开局不给架势）→ 原码什么都不加；
        //   sim 却白给 12 层身法（身法 ≥10 会换一次再行动）—— 卡 222 偏差 33。
        ByBase[222] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            if (src.HasBuff(BuffType.QuanJiaShi))
            {
                src.ModifyBuffValue(BuffType.ShenFa, p.At(0) + p.At(1));
            }
            else if (src.HasBuff(BuffType.GunJiaShi))
            {
                src.ModifyBuffValue(BuffType.ShenFa, p.At(0));
                CombatMath.Attack(src, dst, card.Attack, card.AttackCount);
            }
            SwitchJiaShi(src);
        };

        // ── Card_4000045 天元心法：「[星力]+otherParams[0]（此牌在第一格时多加 1）；[持续]：全部格子都是[星位]」──
        // 原码 Card_4000045.ExecuteEffect：
        //     for (int i = 0; i < 8; i++) if (!src.IsXingWei(i)) src.AddXingWei(i, showText: false);   // 47
        //     num2 = (cardItem.gridNumber == 0) ? 1 : 0;
        //     src.ModifyBuffValue(BuffType.XingLi, num2 + cardConfig.otherParams[0]);                   // 71
        // ⚠ 抽取器在这张卡上 ops **为空**（`reason='cond'`、skipped=2、dropped=1）→ 整卡空转。
        ByBase[4000045] = static (src, dst, card) =>
        {
            int extra = src.CurrentCardGrid == 0 ? 1 : 0;       // IL_00cd 先星力
            src.ModifyBuffValue(BuffType.XingLi, extra + src.Config.CardOtherParams(card.Id).At(0));
            for (int i = 0; i < 8; i++)                          // IL_0173 再把 8 格都变星位
                if (!GridMarkFunctions.IsXingWei(src, i)) GridMarkFunctions.AddXingWei(src, i);
        };

        // ── Card_7000093 梦•混元碎击：「[灵气]+anima；attack 攻；卡组中每有 1 种[五行]就追加 1 次 otherParams[0] 攻（最多 otherParams[1] 次）」──
        // 原码 Card_7000093.ExecuteEffect（IL_000e）：
        //     int num2 = 0;
        //     src.ModifyAnima(cardConfig.anima);
        //     if (cardConfig.level <= Level.YuanYing)          // ← 化神及以上走另一支
        //     { src.Attack(dst, cardConfig.attack, cardConfig.attackCount); goto IL_00c8; }
        //     num2 += src.GetWuXingCountInDeck();
        //     src.Attack(dst, cardConfig.attack + cardConfig.otherParams[0] * GetBuffValue(JiLuWuXingPaiShiYongCiShu),
        //                cardConfig.attackCount + num2);
        //   IL_00c8:
        //     num3 = min(src.GetWuXingCountInDeck(), cardConfig.otherParams[1]);
        //     if (num3 > 0) src.Attack(dst, cardConfig.otherParams[0], num3);
        ByBase[7000093] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            src.ModifyAnima(card.Anima);
            int num2 = 0;
            if (src.Config.CardLevel(card.Id) <= LevelYuanYing)
            {
                CombatMath.Attack(src, dst, card.Attack, card.AttackCount);   // goto IL_00c8
                // IL_00c8：只有这一支会走到（高境界那支 `break` 出 switch 后直接结束，审计 2026-09-22 更正）。
                int num3 = WuXingFunctions.GetWuXingCountInDeck(src);
                if (num3 > p.At(1)) num3 = p.At(1);
                if (num3 > 0) CombatMath.Attack(src, dst, p.At(0), num3);
            }
            else
            {
                num2 += WuXingFunctions.GetWuXingCountInDeck(src);
                int atk = card.Attack + p.At(0) * src.GetBuffValue(BuffType.JiLuWuXingPaiShiYongCiShu);
                CombatMath.Attack(src, dst, atk, card.AttackCount + num2);
            }
        };

        // ── Card_4000046 五雷轰顶：「重复 5 次：otherParams[0]% 概率 otherParams[1] 攻」──
        // 原码 Card_4000046.ExecuteEffect（IL_000e）：
        //     int num2 = 0;
        //     for (int i = 0; i < 5; i++) if (src.GetNextRandomValue() < cardConfig.otherParams[0]) num2++;
        //     if (num2 > 0) src.Attack(dst, cardConfig.otherParams[1], num2, "MultiCast");
        // ⚠ 那个「概率」是 **GetNextRandomValue()（玩家参数队列）**，不是真随机 —— 5 次各取一个参数，
        //   所以 sim 能精确复现（与走马观花同一条路）。抽取器在这里把 `for` 压平成了一个 `if`。
        ByBase[4000046] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            int hits = 0;
            for (int i = 0; i < 5; i++)
            {
                int roll = src.GetNextRandomValue(ParamRequest.Percent(4000046));
                if (roll < p.At(0)) hits++;
            }
            if (hits > 0) CombatMath.Attack(src, dst, p.At(1), hits);
        };

        // ── Card_99000213 万象道种：「卡组中每有 1 种不同境界的牌就追加 1 次攻击」──
        // 原码 Card_99000213.ExecuteEffect（IL_000e）：
        //     List<int> deck = src.GetBattleDeckIdList();
        //     List<Level> levels = new List<Level>();
        //     for (int i = 0; i < deck.Count; i++)
        //     {
        //         Level level = CardFactory.FindCardConfig(deck[i]).level;
        //         if (level >= Level.LianQi && level <= Level.HuaShen && cardItem.gridNumber != i && !levels.Contains(level))
        //             levels.Add(level);
        //     }
        //     int num2 = levels.Count + 1;
        //     src.Attack(dst, cardConfig.attack, cardConfig.attackCount + num2);
        // ⚠ 三个细节都不能漏：① 只数 **LianQi(1)..HuaShen(5)** 的牌；② **排除本牌自己那格**；
        //   ③ 段数 = 卡面段数 **+ num2**（不是替换成 num2）。境界取自 battle_config 的 `lv`。
        ByBase[99000213] = static (src, dst, card) =>
        {
            var seen = new bool[6];                 // Level 1..5
            int kinds = 0;
            for (int i = 0; i < src.Board.Count; i++)
            {
                if (i == src.CurrentCardGrid) continue;
                int lv = src.Config.CardLevel(src.Board[i].Id);
                if (lv >= 1 && lv <= 5 && !seen[lv]) { seen[lv] = true; kinds++; }
            }
            CombatMath.Attack(src, dst, card.Attack, card.AttackCount + kinds + 1);
        };

        // ── Card_7000025 金灵•蓄锐：[防]+def；[锋锐]+otherParams[0]（**[金灵]激活**时再加 def/otherParams[1]）──
        // 原码 Card_7000025.OnExecuted（IL_012a）：
        //     num2 = 0;
        //     if (CardActionBase.CheckWuXing(src, BuffType.JiHuoJinLing))
        //         num2 = Mathf.FloorToInt(src.battleTempData.def / cardConfig.otherParams[1]);
        //     src.ModifyBuffValue(BuffType.FengRui, cardConfig.otherParams[0] + num2);
        //     src.ModifyDef(cardConfig.def);
        // ⚠ `num2` 用的是**加防之前**的 def（`ModifyDef` 在最后）—— 顺序不能换。
        // ⚠ 抽取器在这张卡上 `reason='cond'`、丢了一条 op 和一条语句（`num2` 的赋值在状态机里是**裸赋值**，
        //   而 def 的 floor 除法形状它没合上）→ 结果只剩 `[防]+4`，锋锐整行消失。
        //   这一族（形状对但抽取器没吃下）用**手工注册**比改抽取器稳。
        // ⚠（更正）按 IL 偏移序：IL_009b（ModifyDef）先于 IL_012a（算锋锐），所以 num2 用的是**加防之后**的 def。
        ByBase[7000025] = static (src, dst, card) =>
        {
            src.ModifyDef(card.Def);
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            int fengRui = p.At(0);
            if (src.Subs.CheckWuXing(src, BuffType.JiHuoJinLing))
            {
                int per = p.At(1);
                if (per > 0) fengRui += src.Def / per;      // 原码是 Mathf.FloorToInt((float)def / per)
            }
            src.ModifyBuffValue(BuffType.FengRui, fengRui);
        };

        // ── Card_60 魔化鲛珠：[命元]-otherParams[0]；**重复上一格牌的效果** otherParams[1] 次 ──
        // 忠实 Card_60.OnExecuted（83506-83517）：
        //     if (MoHuaJiaoZhuCiShu 正在触发中) return;          // 递归守卫（上一格还是 60 时不无限套娃）
        //     src.ModifyTempLife(-cardConfig.otherParams[0]);
        //     previousGridCardConfig = GetPreviousGridCardConfig(src, cardItem.gridNumber);
        //     cardId = previousGridCardConfig.id;  if (cardId == -1) return;
        //     for (i = 0; i < otherParams[1]; i++) {
        //         cardItem.InitData(cardId, …);
        //         if (GetBaseCardId(cardId) == 60) cardItem.gridNumber = GetPreviousGrid(src, originGridNumber);
        //         await FindCardAction(cardItem).ExecuteEffect(src, dst, isTempCard: true);
        //         if (cardItem.cardConfig.actionAgain) src.ModifyBuffValue(ExActionAgain, 1);
        //         cardItem.InitData(orignId, …); cardItem.gridNumber = originGridNumber;
        //     }
        //     src.ModifyBuffValue(MoHuaJiaoZhuCiShu, -1);
        // ⚠ 抽取器在这里标了 reason=usecard：`ExecuteEffect` 用的是**上一格的卡 id**，
        //   不是玩家参数队列，它找不到来源。所以这张卡必须手工注册。
        // ⚠ `if (GetBaseCardId(cardId) == 60) gridNumber = GetPreviousGrid(...)` 那步（上一格仍是 60 时再往前挪一格）
        //   由上面的递归守卫取代等价：嵌套时 `MoHuaJiaoZhuCiShu > 0` 直接返回，不会套娃。
        ByBase[60] = static (src, dst, card) =>
        {
            if (src.GetBuffValue(BuffType.MoHuaJiaoZhuCiShu) > 0) return;
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            src.ModifyTempLife(-p.At(0));
            BattleCard? prev = GridFunctions.PreviousGridCard(src, src.CurrentCardGrid);
            if (prev is null || prev.Id < 0) return;       // 上一格没牌 → 原码 cardId == -1 直接 break（id 0 = 普攻，要照打）
            int cardId = prev.Id;
            src.ModifyBuffValue(BuffType.MoHuaJiaoZhuCiShu, 1);
            try
            {
                for (int i = 0; i < p.At(1); i++)
                {
                    // IL_03de 读的是临时执行**之后**的 cardConfig.actionAgain（29 / 260 / 32 … 会动态改写它）。
                    if (TriggerCard(src, dst, cardId)) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
                }
            }
            finally { src.ModifyBuffValue(BuffType.MoHuaJiaoZhuCiShu, -1); }
        };

        // ── Card_186 狂剑•降神：[防]+4；生命+otherParams[1]；随机使用 otherParams[0] 张「狂剑」──
        // 结构与 Card_354 **完全相同**（原码同样是「本卡换成 `src.GetNextParam()` 选中的那张牌 → 框架照常执行 → 还原」）：
        //     src.ModifyDef(cardConfig.def);  src.ModifyHp(otherParams[1]);
        //     for (i = 0; i < otherParams[0]; i++) { int id = src.GetNextParam(); if (id != -1) cardItem.InitData(id, …); }
        //     cardItem.InitData(orignId, …);
        // ⚠ 抽取器把这段拉草成了「两个 buff + 一个没有循环的真实执行的空转」，**那张牌从来没被执行过**。
        //   实测 B5 卡 186：real `L=3 R=-2` / sim `L=28 R=16`。
        ByBase[186] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            src.ModifyDef(card.Def);
            src.ModifyHp(p.At(1));
            for (int i = 0; i < p.At(0); i++)
            {
                int id = src.NextParam(ParamRequest.FromCardPool(186, ParamCardPool.KuangJian));
                if (id != -1 && TriggerCard(src, dst, id)) src.ModifyBuffValue(BuffType.ExActionAgain, 1);   // IL_025d
            }
        };

        // ── Card_354 秣马厉兵：[防]+def；随机使用 otherParams[0] 张「不含攻击效果的门派牌」──
        // 原码把**本卡自己**换成 `src.GetNextParam()` 选中的那张牌，再交给框架照常执行，最后还原：
        //     src.ModifyDef(cardConfig.def);
        //     for (i = 0; i < otherParams[0]; i++) { int id = src.GetNextParam(); if (id != -1) cardItem.InitData(id, …); }
        //     cardItem.InitData(orignId, …);
        // ⚠ 抽取器把这段拉成了两条直线 op（`let nextParam = randnext` + 一个 buff），**完全没有执行那张牌**。
        //   实测 B5 卡 354：real `L=? R=?`，sim 只加了 5 防。
        ByBase[354] = static (src, dst, card) =>
        {
            src.ModifyDef(card.Def);
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            for (int i = 0; i < p.At(0); i++)
            {
                int id = src.NextParam(ParamRequest.FromCardPool(354, ParamCardPool.SectNoAttack));
                if (id != -1 && TriggerCard(src, dst, id)) src.ModifyBuffValue(BuffType.ExActionAgain, 1);   // IL_0239
            }
        };

        // ── Card_339 逍遥无影拳：次数 = attackCount + 牌组里普攻的张数；每「次」加体魄/身法 ──
        // 原码：
        //     attackCount = cardConfig.attackCount;                                  // 4
        //     foreach (card in src.GetBattleDeckIdList()) if (IsPuTongGongJi(card)) attackCount++;
        //     for (i = 0; i < attackCount; i++) { src.ModifyTiPo(op[0]); src.ModifyBuffValue(ShenFa, op[1]); }
        // 卡面：「{attack}攻×{attackCount}（卡组中每有 1 张普通攻击就多攻击 1 次）/ 此牌**每次攻击时**加 1 体魄和 1 身法」。
        // ⚠ 抽取器把循环拉直 → 只加一次。B5 单卡盘面里 attackCount = 4 + 7(空格普攻) = **11**，
        //   于是少给 10 层身法（**身法够 10 层就能再行动**）+ 10 体魄 —— 不只是数值差，是**出手次数**差。
        //   实测 B5 卡 339：real `L=91 R=-8` / sim `L=4 R=16`（误差 111，B5 当前最大）。
        // ⚠ 修正：原码的 `Attack` **在循环体内**（`for (i < attackCount) { await src.Attack(dst, cardConfig.attack); … }`），
        //   是「**每一段**打 attack 点」；早先写成循环外一次 `Attack(src, dst, card)`，
        //   那等于 `attack × attackCount` 段只算了一遍 —— B5 盘面 n = 4 + 7 = 11 段，
        //   真实 22 点 / sim 只有 8 点，且体魄/身法各少 10 层（**身法满 10 层会再次行动**）。
        ByBase[339] = static (src, dst, card) =>
        {
            int n = card.AttackCount;
            foreach (BattleCard c in src.Board)
                if (CardTypes.IsPuTongGongJi(c.Id)) n++;
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            for (int i = 0; i < n; i++)
            {
                CombatMath.Attack(src, dst, card.Attack, 1);     // 每段 attack 点（原码在循环内）
                src.ModifyTiPo(p.At(0));
                src.ModifyBuffValue(BuffType.ShenFa, p.At(1));
            }
        };

        // ── Card_164 玄冥雷云劫：重复 otherParams[0](5) 次，每次都打自己（10% 概率避免）和对方 ──
        // 原码：
        //     for (i = 0; i < cardConfig.otherParams[0]; i++)          // 5
        //     {
        //         if (src.GetNextRandomValue() >= 10)                  // 90% 命中，10% 避免
        //             src.ApplyDamage(src, DamageInfo.Create(src, …, otherParams[1]));   // 自伤 7
        //         src.ApplyDamage(dst, DamageInfo.Create(src, …, otherParams[2]));       // 打对方 9
        //     }
        // ⚠ 抽取器**把循环提升成了直线语句**（`let i = 0; let i = i+1; <body>`），
        //   而循环体里那条**自带条件**的 op 又丢掉了循环守卫 → 自伤只结算一次；
        //   对方那条恰好保住了 `i < 5` 守卫，但仍然只执行一次（直线语句没有真正的循环）。
        //   实测 B5 卡 164：real `L=6 R=-11` / sim `L=-2 R=22` —— R 少挨约 33 ≈ 少打的 4×9。
        // 📌 **这是一类系统性缺口，不止这一张**：全池**96 张**卡有循环变量、且存在「不带循环守卫的 op」。
        //    根因是抽取器与 sim 的 op 模型**都没有「循环」这个概念**（抽取器只有 debuffList /
        //    `new List<BuffType>` 两条特判）。改起来要同时动抽取器与 C# 求值器，先按卡手工补 B5 头名。
        ByBase[164] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            for (int i = 0; i < p.At(0); i++)
            {
                if (src.GetNextRandomValue(ParamRequest.Percent(164)) >= 10)
                    CombatMath.ApplyDamage(src, src, DamageInfo.Create(src, DamageType.Damage, p.At(1)));
                CombatMath.ApplyDamage(src, dst, DamageInfo.Create(src, DamageType.Damage, p.At(2)));
            }
        };

        // ── Card_351 梦•寒冰咒：重复 otherParams[1] 次对方减 otherParams[0] 生命；再挂两个 buff ──
        // 原码：
        //     for (int i = 0; i < card_.cardConfig.otherParams[1]; i++)          // = 2
        //         dst.ModifyHpWithFx(-card_.cardConfig.otherParams[0], 0f, canRevive: false, 0, "FX_Sub_Mantra");   // -5
        //     dst.ModifyBuffValue(BuffType.WuFaJiaShengMing, otherParams[2]);     // 令对方下回合无法加生命
        //     dst.ModifyBuffValue(BuffType.MengYinLeiZhen,    otherParams[2]);     // 令对方下回合无法加防
        // ⚠ 抽取器**把带伤害的那条整条丢了**（`skipped:1`）——它的 `raw` 日志里连这行都没有。
        //   原因是 `ModifyHpWithFx(...)` 这种**带特效参数的 5 实参形式**在取值阶段没解析出来
        //   （最后一个实参是字符串字面量 `"FX_Sub_Mantra"`）。于是「重复2次、每次减5血」整段消失。
        //   实测 B5 卡 351：real `L=28 R=-3` / sim `L=4 R=16`（误差 43）。
        ByBase[351] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            for (int i = 0; i < p.At(1); i++) dst.ModifyHp(-p.At(0), canRevive: false);
            dst.ModifyBuffValue(BuffType.WuFaJiaShengMing, p.At(2));
            dst.ModifyBuffValue(BuffType.MengYinLeiZhen, p.At(2));
        };

        // ── Card_1000067 梦•灵气灌注：[灵气]+anima；**按境界二选一** ──
        // 原码：
        //     if (card_.cardConfig.level <= Level.JinDan)     // 境界 ≤ 金丹
        //         src.ModifyBuffValue(BuffType.XiaCiGongJiDuoGong, otherParams[0]);   // **下次**攻击多X攻（一次）
        //     else
        //         src.ModifyBuffValue(BuffType.JiaGong, otherParams[0]);              // 永久加攻 X
        //     src.ModifyAnima(card_.cardConfig.anima);
        // ⚠ 抽取器把 if/else **拆成了两条都执行** —— 于是「下次攻击多 5 攻」变成了
        //   **永久加攻 5**，每一下都多吃 5 点。本卡 `lv=1`（炼气 ≤ 金丹）走的是**前者**。
        //   实测 B5 卡 1000067：real `L=10 R=-1` / sim `L=67 R=-5`（误差 61）。
        ByBase[1000067] = static (src, dst, card) =>
        {
            int v = src.Config.CardOtherParams(card.Id).At(0);
            src.ModifyAnima(AnimaOf(src, card));               // IL_0098 先灵气
            if (src.Config.CardLevel(card.Id) <= LevelJinDan)  // IL_011f 后加攻
                src.ModifyBuffValue(BuffType.XiaCiGongJiDuoGong, v);
            else
                src.ModifyBuffValue(BuffType.JiaGong, v);
        };

        // ── Card_193 浮光掠影：随机给对方挂负面；对方负面层数够则再行动 ──
        // 原码：
        //     int nextRandomValue = src.GetNextRandomValue();          // 取 **src** 的队列（并消耗卦象）
        //     for (int i = 0; i < nextRandomValue; i++)
        //     {
        //         int nextParam = dst.GetNextParam();                  // ⚠ 取的是 **dst（对方）** 的队列！
        //         if (nextParam != -1) dst.ModifyBuffValue((BuffType)nextParam, 1);
        //     }
        //     … 对方负面状态层数 ≥ otherParams[2] → 再行动
        // ⚠ 抽取器这里丢了两样：① 循环（`reason=loop`，只挂了一次）；② **取的是谁的队列**
        //   （`nextparam` 一律按 src 实现）。两台游标推进速度不同，取到的值就不一样。
        //   全库只有 2 处用 `dst.GetNextParam()`（本卡与 Card_4000085），故手工注册。
        // 注：yisim 把这段建模成「从 6 个具名 debuff 里随机挑」，**与反编译不符**（原码是裸强转），
        //   此处以反码为准。
        ByBase[193] = static (src, dst, card) =>
        {
            var o = src.Config.CardOtherParams(card.Id);
            int times = src.GetNextRandomValue(ParamRequest.Range(193, o.At(0), o.At(1)));
            for (int i = 0; i < times; i++)
            {
                int p = dst.NextParam(ParamRequest.FromDebuffPool(193, ParamDebuffPool.Six));
                if (p != -1) dst.ModifyBuffValue((BuffType)p, 1);
            }
            src.CurrentCardActionAgain = dst.GetDebuffCount() >= o.At(2);
        };

        // ── Card_14 混元五行：**五行全激活才生效** —— 加攻 otherParams[0] + 护体 otherParams[1] ──
        // 卡面：「若已激活全部[五行灵]，获得{4}层[加攻]和{4}层[护体]」——**两条都在条件里**。
        // ⚠ 抽取器只留下了无条件的 `护体` 那条（带条件的 `加攻` 被 `reason=cond` 丢掉），
        //   而**护体也必须带这个条件**。实测 B5 卡 14：real `L=4 R=16`（= 零效果基线 ——
        //   单卡隔离盘面里五行根本没激活，两条都不该发），sim `L=52 R=16`
        //   （护体 4 × 出 4 次 = 吸收 16 下普攻 → trace 里 L 只挨了 16 下而不是 32 下）。
        //   诊断靠 trace 的挨打次数反推，比读状态机的 IL 标签可靠。
        ByBase[14] = static (src, dst, card) =>
        {
            IReadOnlyList<int> p = src.Config.CardOtherParams(card.Id);
            if (WuXingFunctions.GetWuXingActiveCount(src) != 5) return;   // 五行没全激活 → **什么都不做**
            src.ModifyBuffValue(BuffType.JiaGong, p.At(0));
            src.ModifyBuffValue(BuffType.HuTi, p.At(1));
        };

        // ── Card_159 玄冥云烟：重复 otherParams[0] 次，每次 `switch (GetNextParam())` **三选一** ──
        // 原码：
        //     for (int i = 0; i < card_.cardConfig.otherParams[0]; i++)   // = 2
        //         switch (src.GetNextParam())
        //         {
        //         case 0: dst.ModifyHp(-6); src.ModifyHp(6); break;      // 吸取对方 6 生命
        //         case 1: dst.ModifyBuffValue(BuffType.XuRuo, 2); break;  // 2 层虚弱
        //         case 2: src.ModifyBuffValue(BuffType.HuTi, 1); break;   // 1 层护体
        //         }
        // ⚠ **必须手工注册**：抽取器不支持 `switch` 分派 —— 它把三个 case 拉平成了**顺序语句**，
        //   只保留循环次数的守卫，于是**三种效果每次都全上**（吸取 6×2 血 + 2×2 虚弱 + 2×1 护体）。
        //   实测 B5 卡 159（real `L=-2 R=22` / sim `L=42 R=-2`，误差 68）。
        //   同类形态全库只有 3 处（本卡、Card_6000014、以及卡类外一处），不值得改抽取器。
        // 🔑 定位方式：yisim 的 `for (i<2) { switch (game.random_int(3)) { … } }` 一眼看出是分派，
        //   反编译印证 `switch (src.GetNextParam())`。两条来源一致。
        ByBase[159] = static (src, dst, card) =>
        {
            int times = src.Config.CardOtherParams(card.Id).At(0);
            for (int i = 0; i < times; i++)
            {
                switch (src.NextParam(ParamRequest.Choice(159, 3)))
                {
                    case 0:
                        dst.ModifyHp(-6);
                        src.ModifyHp(6);
                        break;
                    case 1:
                        dst.ModifyBuffValue(BuffType.XuRuo, 2);
                        break;
                    case 2:
                        src.ModifyBuffValue(BuffType.HuTi, 1);
                        break;
                }
            }
        };
    }
}
