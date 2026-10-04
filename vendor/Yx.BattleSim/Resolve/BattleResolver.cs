using Yx.BattleSim.Combat;
using Yx.BattleSim.Effects;
using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Resolve;

/// <summary>
/// 单场战斗结算器（确定性、无 I/O、无 async、无帧）。同一 <see cref="BattleInput"/> 必得同一结果。
///
/// 回合模型（忠实移植自 BattleExecuter.Execute 的 while(true) 回合循环 + BattleCharacter.ShiftCard）：
///   · **双方轮流，每回合出一张**（firstCharacter 出牌 → OnTurnEnded → 交换 first/second）；
///   · 牌组是**队列**（<see cref="BattleDeck"/>）：出牌取队首、出完塞队尾，因此整体上仍按格位循环；
///     但**持续 / 消耗牌出过一次就出局**（skip），之后每回合被跳过，直到八格全部出局才整体复位成普攻；
///   · **空格 = card 0 = 普通攻击 3 攻**（`GetBaseCardId==0`，游戏 ShiftCard 走完 InitData(0)）；placed 牌按本身；
///   · 打到 **DeathCheck 分出胜负** 或 **达 64 回合(MAX_HUI_HE_COUNT)** 为止；hp/def 跨回合结转。
///   · 先手 = <see cref="BattleInput.FirstPlayerUid"/>（缺省 Left）。
///   · **行动权**（一回合连出多张）已建模：见 <see cref="PlayTurn"/>（ExActionAgain / 卡自带 actionAgain /
///     神法 / 五行天髓诀 / 天髓葫芦 / 天髓宗 / 困符·梦困仙 代价）。
///   · **回合 tick** 已接：回合开始/结束各跑一遍逐 buff 结算（<see cref="TurnTickFunctions"/>）。
///   · **复活** 已接：每次出牌后 / 回合收尾各查一次（<see cref="RevivalFunctions"/>）。
///
/// 每次出牌的数学（加防/CalculateAttack/buff/仙命/炼化/牌型/五行/共鸣）由 P2–P5 的忠实移植承载。
/// ⬜ 仍缺：出牌前的跳过链（星弈断/梦厄劫/冻住极限/顺影击/调制上掌）、炼化牌出牌、三个相邻钩子
///   （CheckAdjacentEffects / BeforeCard / AfterCard）。
/// </summary>
public static class BattleResolver
{
    /// <summary>空格 / 牌组走完 = card 0 = 普通攻击 3 攻（rarity0）。</summary>
    private static readonly BattleCard PuGong = new() { Id = 0, Attack = 3, AttackCount = 1 };
    private const int MaxHuiHe = 64;

    /// <summary>描述里的「[耗尽]」标记：出一次就出局（原码 22262，全池 8 张）。</summary>
    private const string HaoJinMarker = "[耗尽]";

    /// <summary>
    /// 诊断用回调：非空时每出一张牌回调一次（内容为「谁、第几张、出牌前后 hp/def」）。
    /// 只为定位对拍差异，不参与结算、不影响结果——生产路径保持空。
    /// </summary>
    public static Action<string>? Trace;

    /// <summary>
    /// 每回合结束的打点：(回合号, 左, 右)。给摆牌评分算「前置伤害 / 第几回合打死对方」用。
    /// 线程静态 —— 并行求解的每个 walker 各设各的；没人设时只有一个 null 判断的开销。
    /// 回调在战斗中同步调用，读到的 Hp / MaxHp 是那一刻的值。
    /// </summary>
    [ThreadStatic] public static Action<int, Combatant, Combatant>? TurnSink;

    public static BattleOutcome Resolve(BattleInput input)
    {
        // 选牌参数队列**双方共享一条**（游戏挂在 battleExecuter 上）。两侧输入一致时取左的，
        // 左边为空才退回右边 —— 只有一侧采集到参数时也不至于整场取空。
        var paramQueue = new Model.BattleParamQueue(
            input.Left.BattleParams.Count > 0 ? input.Left.BattleParams : input.Right.BattleParams);
        var left = ToCombatant(input.Left, paramQueue);
        var right = ToCombatant(input.Right, paramQueue);
        left.Opponent = right;
        right.Opponent = left;

        // GetCurrentRound ← battleResult.round（本场战斗发生在第几轮），共鸣 effectRound 门槛用。
        left.CurrentRound = input.Round;
        right.CurrentRound = input.Round;

        // 开战：原码先手方完整跑完 OnBattleStarted，再轮到后手方（BattleExecuter 15361–15362）；
        // 单方内部顺序见 BattleStartFunctions（头部 → 天赋 → 共鸣 → 仙命 → 上回合永久 buff → 逐格[开局] → 刻印）。
        Combatant opener = input.FirstPlayerUid is { Length: > 0 } fp0 && fp0 == right.Uid ? right : left;
        BattleStartFunctions.OnBattleStarted(opener);
        StepLog.BehaviorSink?.Invoke("opening", 0, opener);
        BattleStartFunctions.OnBattleStarted(opener == left ? right : left);
        StepLog.BehaviorSink?.Invoke("opening", 1, left);

        // 回合循环：先手 = firstPlayerId（缺省 left）。每「回合」可能出多张（行动权），见 PlayTurn。
        Combatant first = input.FirstPlayerUid is { Length: > 0 } fp && fp == right.Uid ? right : left;
        Combatant second = ReferenceEquals(first, left) ? right : left;
        int steps = 0;
        for (int huiHe = 1; huiHe <= MaxHuiHe; huiHe++)
        {
            steps += PlayTurn(first, second);
            TurnSink?.Invoke(huiHe, left, right);       // 死判之前打点：打死对方的那一回合也算得出来
            StepLog.BehaviorSink?.Invoke("turn", huiHe, left);
            if (DeathRules.DeathCheck(left, right)) break;
            var tmp = first; first = second; second = tmp;
        }

        var outcome = Score(left, right, input, steps);
        StepLog.BehaviorSink?.Invoke("end", steps, left);
        return outcome;
    }

    /// <summary>
    /// 一个回合：先出一张，再按「行动权」判定决定是否连出（忠实移植 BattleExecuter.Execute 内层
    /// do-while 的行动权段 15634–15771）。返回本回合实际出牌张数。
    /// </summary>
    internal static int PlayTurn(Combatant actor, Combatant foe)
    {
        // 回合开始 tick（对应 Execute 里 await firstCharacter.OnTurnStarted()）。
        TurnTickFunctions.OnTurnStarted(actor);
        RevivalFunctions.Check(actor);
        if (DeathRules.DeathCheck(actor, foe)) return 0;

        // 每回合打出一张刻印牌（原码 15400，回合开始 tick 与死亡判定之后、出牌之前）。
        KeYinTurnFunctions.OnTurn(actor, foe);

        // ── 无法行动：本回合**跳过出牌**并消耗一层 ──
        // 对应原码战斗循环里（ShiftCard 之前）：
        //     if (firstCharacter.HasBuff(BuffType.LianXiMuZhuang)) ModifyBuffValue(LianXiMuZhuang, -1);
        //     else if (firstCharacter.HasBuff(BuffType.WuFaXingDong)) { 显示"无法行动"; ModifyBuffValue(WuFaXingDong, -1); … }
        // ⚠ 这条 buff 早先是**只写不读**：卡效果数据能给它加层，战斗循环却从不检查，
        //   于是「令对方无法行动1回合」整条机制静默失效。
        // 实测 B5 卡 11 七星定魂（`对方减4生命 / 令对方无法行动1回合`）：真实 L=19、sim L=7
        //   —— sim 多挨了约 4 回合普攻，正是漏跳的那一回合。
        if (actor.HasBuff(BuffType.LianXiMuZhuang)) actor.ModifyBuffValue(BuffType.LianXiMuZhuang, -1);
        else if (actor.HasBuff(BuffType.WuFaXingDong))
        {
            actor.ModifyBuffValue(BuffType.WuFaXingDong, -1);
            FinishTurn(actor);
            return 0;
        }

        int played = 0;
        if (!PlayNext(actor, foe)) { FinishTurn(actor); return played; }   // 灵气不够：空转攒灵气，本回合结束
        played++;
        RevivalFunctions.Check(actor);
        // 有人倒下 → 原码 `if (DeathCheck()) break;` 跳出行动循环，随后再判一次死亡就**结束战斗**，
        // **不跑 OnTurnEnded**（15826–15831）。早先这里先 FinishTurn：回合末的水势 / 阵法等又打了已死的一方一轮
        // （水灵一系 b5 偏差的来源：17 水灵•春雨 real R=-2、sim R=-14，差的正是死后那一下水势）。
        if (DeathRules.DeathCheck(actor, foe)) return played;

        // battleTempData.actionAgainPerRound（刻印山风等会改它）。
        while (true)
        {
            // 困符/梦转仙：本回合后续「再行动」的收尾豁免。
            bool flag2 = actor.HasBuff(BuffType.Long) || actor.HasBuff(BuffType.WuShiKunXianKunFu)
                         || actor.HasBuff(BuffType.MengZhuanXian);
            if (!flag2 && (actor.HasBuff(BuffType.TianYinKunXianQu) || actor.HasBuff(BuffType.BenHuiHeWuFaZaiCiXingDong)))
            {
                actor.RemoveBuff(BuffType.ExActionAgain);
                FinishTurn(actor);
                return played;
            }

            string cardName = actor.CurrentCardName;
            bool flag3 = actor.CurrentCardActionAgain || actor.HasBuff(BuffType.ExActionAgain)
                         || actor.HasBuff(BuffType.TianSuiKuangWuQu) || actor.HasBuff(BuffType.YingXiaoTu);
            int zai = actor.GetBuffValue(BuffType.ZaiCiXingDong);

            if (!flag3 && actor.HasBuff(BuffType.WuXingTianSuiJue)
                && actor.GetBuffValue(BuffType.BENLUNGONGJICISHU) == 0
                && GridFunctions.IsWuXingCard(cardName) && zai < actor.ActionAgainPerRound)
            {
                flag3 = true;
                actor.ModifyBuffValue(BuffType.WuXingTianSuiJue, -1);
            }
            if (!flag3 && actor.HasBuff(BuffType.TianSuiHuLu) && GridFunctions.IsWuXingCard(cardName)
                && zai < actor.ActionAgainPerRound && WuXingFunctions.IsCardNameActived(actor, cardName))
            {
                flag3 = true;
                actor.ModifyBuffValue(BuffType.TianSuiHuLu, -1);
            }
            int subcat = (actor.Config as Config.JsonBattleConfig)?.Card(actor.CurrentCardId)?.Subcategory ?? 0;
            if (!flag3 && actor.HasBuff(BuffType.TianSuiZong) && (subcat == 8 || subcat == 9) && zai < actor.ActionAgainPerRound)
            {
                // 原码还要求当前牌 subcategory == TianZong(8) || XianZong(9)。
                flag3 = true;
                actor.ModifyBuffValue(BuffType.TianSuiZong, -1);
            }
            int shenFaCost = actor.HasFateStrategy(348) ? 9 : 10;
            if (!flag3 && actor.GetBuffValue(BuffType.ShenFa) >= shenFaCost && zai < actor.ActionAgainPerRound)
            {
                flag3 = true;
                actor.ModifyBuffValue(BuffType.ShenFa, -shenFaCost);
            }

            if (!flag3) { FinishTurn(actor); return played; }
            if (actor.HasBuff(BuffType.MengZhuanXian)) actor.ModifyBuffValue(BuffType.MengZhuanXian, -1);
            if (actor.HasBuff(BuffType.ExActionAgain)) actor.RemoveBuff(BuffType.ExActionAgain);
            if (zai >= actor.ActionAgainPerRound) { FinishTurn(actor); return played; }
            actor.ModifyBuffValue(BuffType.ZaiCiXingDong, 1);

            // 困符：再行动一次要付代价（受伤/挂外伤），可能直接结束本回合。
            if (actor.HasBuff(BuffType.KunFu) && !flag2)
            {
                actor.ModifyBuffValue(BuffType.KunFu, -1);
                bool exempt = actor.Subs.IsTalentResonanceEffective(actor, 136) && actor.HasTalent(206)
                              && actor.HasBuff(BuffType.GunJiaShi);
                if (!exempt)
                {
                    if (foe.HasBuff(BuffType.FuXianGuTeng))
                        actor.ModifyBuffValue(BuffType.WaiShang, foe.GetBuffValue(BuffType.FuXianGuTeng));
                    FinishTurn(actor);
                    return played;
                }
                actor.ModifyHp(-15);
            }
            else if (actor.HasBuff(BuffType.MengKunXian) && !flag2)
            {
                FinishTurn(actor);
                return played;
            }

            // 再行动的落前联动（顺序照原码）。
            if (actor.HasFateStrategy(340)) actor.ModifyHp(actor.Config.FateOtherParams(340).At(1));
            if (actor.HasBuff(BuffType.YingXiaoTu)) actor.ModifyHp(-actor.GetBuffValue(BuffType.YingXiaoTu));
            // 八门金锁阵：反噬「再行动」的一方，伤害取**卡 8000010 的 otherParams[1]**，并消耗一层。
            // ⚠ 早先我取的是 10000147 那张卡（另一个机制），而且**漏了消耗**——buff 永不衰减。
            if (foe.HasBuff(BuffType.BaMenJinSuoZhen))
            {
                CombatMath.ApplyDamage(foe, actor, DamageInfo.Create(foe, DamageType.Damage,
                    foe.Config.CardOtherParams(8000010).At(1), skipWoundCheck: true));
                foe.ModifyBuffValue(BuffType.BaMenJinSuoZhen, -1);
            }
            // 噬仙古藤：对方每次再次行动，**吸取**（对方掉血、持有者回同量）—— 原码 15737 两句都有，早先漏了回血那句（b7_26）。
            if (foe.HasBuff(BuffType.ShiXianGuTeng))
            {
                int drain = foe.GetBuffValue(BuffType.ShiXianGuTeng);
                actor.ModifyHp(-drain);
                foe.ModifyHp(drain);
            }
            if (actor.HasTalent(209) && actor.HasBuff(BuffType.GunJiaShi))
            {
                actor.ModifyAnima(1);
                actor.ModifyHp(2);
            }
            if (actor.Subs.HasKeYinType(actor, 163))
                actor.ModifyAnima(actor.Subs.KeYinOtherparam(actor, 163, 0));
            if (actor.Subs.IsTalentResonanceEffective(actor, 7))
                actor.ModifyBuffValue(BuffType.WuShiFangYu, 1);
            if (actor.HasBuff(BuffType.KeYinHuLingLong))
            {
                actor.ModifyBuffValue(BuffType.KeYinHuLingLong, -1);
                actor.ModifyBuffValue(BuffType.HuTi, 1);
                actor.ModifyDef(5);
            }
            if (actor.HasBuff(BuffType.ZaiCiXingDongJiaFengRui))
                actor.ModifyBuffValue(BuffType.FengRui, actor.GetBuffValue(BuffType.ZaiCiXingDongJiaFengRui));
            if (actor.HasBuff(BuffType.MengHuoZhen))
            {
                int mv = actor.GetBuffValue(BuffType.MengHuoZhen);
                foe.ModifyHp(-mv);
                foe.ModifyMaxHp(-mv);
                actor.ModifyDef(mv);
            }

            // 原码 IL_3b65：`ResurrectionCheckAsync` 后 `while (!DeathCheck())` —— 再行动的代价（影枭兔扣血、
            // 困缚 -15、八门金锁阵…）把人打死时**不再出下一张牌**。早先 sim 直接出牌，死人还多打了一下
            // （99000210 影枭兔：real R=58、sim R=55）。
            RevivalFunctions.Check(actor);
            if (DeathRules.DeathCheck(actor, foe)) return played;

            if (!PlayNext(actor, foe)) { FinishTurn(actor); return played; }   // 灵气不够：空转攒灵气，本回合结束
            played++;
            RevivalFunctions.Check(actor);
            // 有人倒下 → 原码 `if (DeathCheck()) break;` 跳出行动循环，随后再判一次死亡就**结束战斗**，
        // **不跑 OnTurnEnded**（15826–15831）。早先这里先 FinishTurn：回合末的水势 / 阵法等又打了已死的一方一轮
        // （水灵一系 b5 偏差的来源：17 水灵•春雨 real R=-2、sim R=-14，差的正是死后那一下水势）。
        if (DeathRules.DeathCheck(actor, foe)) return played;
        }
    }

    /// <summary>回合收尾：攻数/再行动计数 + 回合结束 tick（对应 Execute 里 OnTurnEnded 那段）。</summary>
    private static void FinishTurn(Combatant actor)
    {
        actor.ModifyBuffValue(BuffType.ZaiCiXingDongCiShu, 1);
        TurnTickFunctions.OnTurnEnded(actor);
        // ⚠ 上限**不**每回合重置：原码只在 CreateTempData 里置 1（开局一次）。「本回合」的加成
        //   （天赋 222 / 刻印 132）是靠同时挂 KeYinShanFeng、回合结束时在 OnTurnEnded 里减回去的；
        //   龙（卡 79）的 +1 则是持续整场。早先这里每回合重置成 1，把龙的加成吃掉了（b6_119）。
        RevivalFunctions.Check(actor);
    }

    /// <summary>
    /// 把输入盘面补成**满格牌组**（游戏 <c>m_BattleDeck</c> 恒为 8 格，空格就是普攻 card 0）。
    /// 必须补满：<see cref="BattleDeck.Item.Slot"/> 指向格位，而相邻格（连音的前一格、相生）
    /// 与运行期换牌（升级/复制/变形）都会读写**空格所在的下标** ——
    /// 短表会让这些格子读成 null、写不进去（实测卡 206 逍遥连音的复制整条静默失效）。
    /// </summary>
    private static List<BattleCard> FullBoard(IReadOnlyList<BattleCard> board, int unlockGrids)
    {
        int n = unlockGrids > 0 ? unlockGrids : BattleDeck.Grids;
        if (board.Count > n) n = board.Count;
        var full = new List<BattleCard>(n);
        for (int i = 0; i < n; i++) full.Add(i < board.Count ? board[i] : PuGong);
        return full;
    }

    /// <summary>取某一格的牌；格位越界（含被 InitData(0) 复位成 -1 的格）一律 = 普攻。</summary>
    private static BattleCard ResolveCard(Combatant actor, BattleDeck.Item item)
        => item.Slot >= 0 && item.Slot < actor.Board.Count ? actor.Board[item.Slot] : PuGong;

    /// <summary>
    /// 本回合出一张牌：<b>取牌 → 结算 → 回队尾</b>。忠实移植原码 15590–15630 那一段
    /// （<c>ShiftCard</c> 取牌、执行、然后 <c>PushCard</c> 塞回队尾）。
    /// </summary>
    /// <remarks>
    /// ⚠ 这里**不能**再按「游标 % 8」发牌：出牌收尾时
    ///     if (cardType == Sustain || cardType == Consume || cardItem.skip) cardItem.skip = true;
    /// 之后 <c>ShiftCard</c> 会把该格跳过去 —— **持续 / 消耗牌整场只出一次**（八格全出局才复位成普攻）。
    /// 漏掉这条会让 442 张持续牌 + 133 张消耗牌无限重出。见 <see cref="BattleDeck"/>。
    /// </remarks>
    /// <returns><c>true</c> = 牌出成了；<c>false</c> = 灵气不够没出成（空转一回合攒灵气，本回合就此结束）。</returns>
    private static bool PlayNext(Combatant actor, Combatant foe)
    {
        BattleDeck.Item item = actor.Deck.ShiftCard();
        // 出牌前跳过链（原码 15450–15590：星弈•断 / 无名白鹿 / 梦厄劫 / 仙命398 / 末格牌 / 星弈•跳 / 洞烛机先 / 瞬影击）。
        // ⚠ 更正旧记录：这里曾写「XingYi_Duan 跳过的牌仍要付耗生命」—— 那是 **338 瞬影击**的卡面，不是 109 的；
        //   XingYi_Duan 在引擎里是纯跳过。见 SkipChain 类注释。
        item = SkipChain.Run(actor, foe, item, it => ResolveCard(actor, it).Id);
        actor.CurrentCardGrid = item.Slot;              // 出牌前钩子/相邻效果要读格位（复位成普攻时为 -1）
        BattleCard card = ResolveCard(actor, item);
        StepLog.BehaviorSink?.Invoke("attempt", card.Id, actor);

        // ── 原码主循环 15592 `CheckAdjacentEffects`（d__15，付费**之前**）：上一格 / 相邻格是崩拳•连环系 → 本张算崩拳 ──
        // 出牌前钩子里的 CheckAdjacentEffectsBeforeCard（d__16）还会再挂一次，两处都照抄（本张出完即清）。
        // 放在付费之前才能让 CostFunctions 里「崩拳」类的耗生命结算认到这张牌。
        CardHookFunctions.CheckAdjacentEffects(actor);

        // ── 灵气代价（原码 CheckAnima → CardActionExecuteResult.AnimaShortage，24234 / 15775）──
        // `int num = cardConfig.anima`：**负=耗、正=回**（343 张 an<0 / 481 张 an>0），
        // 经剑契 / 紫芒星宝 / 刻印47 / 共鸣126 … 抵扣后仍不够 → 出不了这张牌：
        //   `firstCharacter.UnshiftCard(currentCard)` 塞回**队首**，
        //   `else { firstCharacter.ModifyAnima(1); }` **本回合自得 1 点灵气**，然后 break 出回合内循环。
        // 所以付不起不是死锁，而是「原地攒灵气」：下回合取到的还是这张，攒够那天就打出来。
        // 用户 2026-09-22 口述确认：「下一张需要消耗灵气、自己还不够，就停止行动一回合、让自己获得 1 灵气，
        // 直到能打出下一张灵气卡」—— **那 +1 就是这条机制的全部关键**，漏掉它 0 灵气永远涨不上去。
        // ⬜ 未移植的抵扣/替代项：剑契 / 紫芒星宝 / 刻印47 / 共鸣126 / 羁绊153(转血) / 共鸣57+仙命160(脉) /
        //    共鸣43+仙命412(木灵转血) / 天赋222(卡16) / 仙命321 / 天赋142(改为按天赋值回灵气) /
        //    `cardConfig.chargeQi`[蓄灵]（付不起时额外回灵气，配置表尚未抽这个字段）。
        if (!CostFunctions.CheckAnima(actor, card))      // 原码 CheckAnima：减耗链 + 替代支付 + 扣灵气
        {
            // 原码 `case CardActionExecuteResult.AnimaShortage`（15776）整段：
            actor.Deck.UnshiftCard(item);               // 塞回队首：下回合取到的还是这张
            var cfgS = actor.Config;
            // 共鸣 33：空转时加生命及上限（生效且有天赋 142 再加 o[1]）。
            if (actor.HasTalentResonance(33))
            {
                int n = cfgS.ResonanceOtherParams(33).At(0);
                if (actor.IsTalentResonanceEffective(33) && actor.HasTalent(142)) n += cfgS.ResonanceOtherParams(33).At(1);
                actor.ModifyMaxHp(n);
                actor.ModifyHp(n);
            }
            // 天赋 142：空转改为回「天赋 o[0]」灵气 + 「o[1]」卦象（共鸣 33 生效各 +1）；否则回 1 灵气。
            if (actor.HasTalent(142))
            {
                int bonus = actor.IsTalentResonanceEffective(33) ? 1 : 0;
                actor.ModifyAnima(cfgS.TalentOtherParams(142).At(0) + bonus);
                actor.ModifyBuffValue(BuffType.GuaXiang, cfgS.TalentOtherParams(142).At(1) + bonus);
            }
            else actor.ModifyAnima(1);
            if (cfgS.CardChargeQi(card.Id) > 0) actor.ModifyAnima(cfgS.CardChargeQi(card.Id));   // [蓄灵]
            // 对手的共鸣 28：自己空转时挂内伤。
            if (foe.IsTalentResonanceEffective(28))
                actor.ModifyBuffValue(BuffType.NeiShang, cfgS.ResonanceOtherParams(28).At(0));
            return false;
        }

        // ── hpCost：**以血代灵气的代价**（原码 CheckCardCost 在灵气判定**通过之后**扣）──
        //   `int num2 = cardConfig.hpCost;
        //    if (num == Succeed && num2 > 0) … src.ModifyHp(-num2, 0f, canRevive: false, 0, isCost: true);`
        // 全池 **172 张**有（含稀有度变体）；B5 里 163 玄冥剑意诀(hc=6) / 159 玄冥云烟(hc=3) 是两个大头，
        // 这批卡在 B5 合计 729 误差（40 场），不扣就是「白嫖代价、血永远掉不下去」。
        // 整段见 CostFunctions.PayHpCost（天赋174 / 10000005 / 刻印95 的改写 + 七条崩拳结算 + 仙命347）。
        // ⚠ 必须在卡效果**之前** —— 引擎 CheckCardCost 先于 Execute。
        CostFunctions.PayHpCost(actor, foe, card);

        // ── 出牌前的拦截链（炼妖壶 / 连音 / 升级类）──
        // 顺序照原码：CheckCardCost 在 `Execute` **之前**，拦截段在 Execute **之内**、ExecuteEffect 之前。
        // 所以**扣的是原牌的代价**，而换牌只影响随后执行的效果与攻防。换过就要重新取一次牌。
        // ── 出牌前的拦截链（复刻 / 炼妖壶 / 连音 / 升级类）—— 属于原码 `Execute`，在付费（CheckCardCost）**之后** ──
        // ⚠ 复刻早先放在付费之前 → 按变身后的牌付费；原码扣的是 322 自己的代价。
        // 必须在 ExecuteEffect **之前**：原码里它改写的是常驻 CardItem 的 cardInfo.id，
        // 之后执行的是**换过之后**那张牌（连攻/防/otherParams 一起变）。换过就要重新取一次牌。
        //
        // ① **322 逍遥•复刻**（原码 20413，`CardActionBase.Execute` 的 default 段）：
        //      if (GetBaseCardId(cardConfig.id) == 322 && dst.GetBattleDeckIdList().Count > cardItem.gridNumber)
        //      {
        //          dstCardId = dst.GetBattleDeckIdList()[cardItem.gridNumber];   // 对方**同格**的牌
        //          if (GetBaseCardId(dstCardId) == 0) dstCardId = 286 + dstCardId;  // 那边是普攻 → 复刻普攻
        //          …（把它变成 dstCardId）
        //      }
        //    卡面「此牌使用时，**先永久**变为对方的同格牌」—— 所以要写回盘面（`ReplaceCard`），
        //    而不是只改这一回合的临时值。
        // ⚠ 配置里 `286` 就是「普通攻击」(a=3)：所以**对方同格是空格**时，复刻出来的是「打得动 3 攻」的普攻，
        //   而不是空过。B5 盘面右边全空，正是这一支。
        // ⚠ 判据来源：yisim 仲裁 —— `b5_322_0` real 4/4、sim 4/16、**yisim 4/4**；`_1` real 13/-2、sim 4/4、yisim 13/-2。
        if (CardTypes.BaseId(card.Id) == 322 && item.Slot >= 0 && item.Slot < foe.Board.Count)
        {
            int dstId = foe.Board[item.Slot].Id;
            if (CardTypes.BaseId(dstId) == 0) dstId = 286 + dstId;
            card = actor.Config.BuildCard(dstId);
            actor.ReplaceCard(item.Slot, card);          // 「永久」变
        }
        PrePlayFunctions.Apply(actor, foe, card, item.Slot);
        card = ResolveCard(actor, item);

        PlayCard(actor, foe, card);
        // 出牌收尾：持续 / 消耗牌用一次就出局，然后才回到队尾。顺序不能反（MarkUsed 要在 PushCard 前）。
        // 出局判据三条：持续牌 / 消耗牌（原码 15608），以及**描述里带 `[耗尽]` 的牌**（原码 22262
        // `cardActionBase.cardConfig.desc.Contains("[耗尽]")` → `cardItem.skip = true`）。
        // `[耗尽]` 只存在于描述文本里，没有任何结构化字段 —— 全池 8 张（逍遥•无相/无隙/无息/无衡/无回/无律、
        // 死劫 329、仙府大白粽 399）。漏掉的后果：它们会每 8 手重出一次，而真实里出一次就出局。
        // 实测 B5 卡 399：real `L=4 R=7`（= 零效果基线，因为只出一次且 30% 回血被后面的普攻抹平），
        //   而 sim 出 4 次回 120 血 → `L=76`。
        // ⬜ 原码还带 `&& !isTempCard`（临时牌豁免）—— sim 无临时牌概念。
        BattleDeck.MarkUsed(item, actor.Config.CardIsSustain(card.Id)
            || actor.Config.CardIsConsume(card.Id)
            || actor.Config.CardDesc(card.Id).Contains(HaoJinMarker)
            || actor.CurrentCardSkip);                   // 汲然咒印 / 使用下张牌后消耗（出牌前钩子置位）
        // 跳至上张（162 玄冥重现 等挂的 TiaoZhiShangZhang，原码 15618）：这张塞回队首，再把队列右转 N 格
        // （跳过已出局的牌）—— 下一手就是「上一张」还在场的牌。早先 sim 整个没有，b6_169 逐步对拍在此分叉。
        if (actor.HasBuff(BuffType.TiaoZhiShangZhang))
        {
            actor.Deck.UnshiftCard(item);
            actor.Deck.RightMoveCardItems(actor.GetBuffValue(BuffType.TiaoZhiShangZhang), careSkip: true);
            actor.RemoveBuff(BuffType.TiaoZhiShangZhang);
        }
        else actor.Deck.PushCard(item);
        return true;
    }

    /// <summary>出单张牌（回合循环与测试共用）。</summary>
    internal static void PlayCard(Combatant actor, Combatant foe, BattleCard card)
    {
        // 当前牌上下文（P5 牌型谓词）：让加攻/仙命/炼化里的牌型分支按真实牌型点亮。
        CardContext.Set(actor, card);
        // 再行动 = 「输入自带的」或「卡表的无条件 actionAgain」（108 张牌）。缺了后者这些牌会少行动一次。
        actor.CurrentCardActionAgain = card.ActionAgain || actor.Config.CardActionAgain(card.Id);
        // 出牌前钩子要按格判（刻印73 第 7 格、星位、后招的 hadUsed 都读它）。
        int grid = actor.CurrentCardGrid;

        // 出牌前钩子不在这里单独调：每一次 ExecuteEffect（主执行 / 额外执行 / 临时牌）都自带前后钩子。
        actor.CurrentCardSkip = false;

        if (Trace is not null)
        {
            Trace($"{actor.Uid} grid={grid} card={card.Id} a={card.Attack}×{card.AttackCount} d={card.Def}"
                + $" | before {actor.Uid}(hp={actor.Hp},def={actor.Def}) {foe.Uid}(hp={foe.Hp},def={foe.Def})");
        }


        // ── 「下 1 次使用的 X 牌连续生效两次」一族：**先**判门、执行额外那一遍，**再**走下面的主执行 ──
        // ⚠ 时序是关键：这些检查在 `CardActionBase.Execute`（d__11）里位于主 ExecuteEffect（IL_27e5）**之前**。
        //   早先放在主执行之后，于是「施加这个 buff 的那张牌」当场就把自己又放了一遍（111 / 4000041 / 10000060 / 1000061
        //   四张同形状偏差），而引擎里它只作用于**下一张**。挪到前面后四张全部清零（oracle 实测）。
        // ⚠ 顺序照原码 goto 链：梅花粽（20782）→ 梅开二度（20926）→ 狂剑•双龙（21129）→ 雷闪二度（21320）
        //   → 云剑•连雨（21515）→ 崩拳•双影（21090）→ 聚焰（20753）。每条都是「扣一层 + 完整再执行一遍」（含前后钩子）。
        // （刻印·引证 / 灵阵回响在主执行**之后**，见下方。）
        string nm = actor.Config.CardName(card.Id);
        // 梅香玉露粽（111）：下 1 次使用的牌连续生效两次（不看牌型）。
        if (actor.HasBuff(BuffType.MeiHuaZong))
        {
            actor.ModifyBuffValue(BuffType.MeiHuaZong, -1);
            CardEffects.ExecuteEffect(actor, foe, card);
        }
        // 梅开二度（4000041）：同上。
        if (actor.HasBuff(BuffType.MeiKaiErDu))
        {
            actor.ModifyBuffValue(BuffType.MeiKaiErDu, -1);
            CardEffects.ExecuteEffect(actor, foe, card);
        }
        // 狂剑•双龙（1000061）：下 1 次使用的**狂剑**连续生效两次。
        if (actor.HasBuff(BuffType.ShuangLong) && CardTypes.IsKuangJian(actor, card.Id))
        {
            actor.ModifyBuffValue(BuffType.ShuangLong, -1);
            CardEffects.ExecuteEffect(actor, foe, card);
        }
        // 雷闪二度：雷牌。
        if (actor.HasBuff(BuffType.LeiShanErDu) && nm.Contains("雷"))
        {
            actor.ModifyBuffValue(BuffType.LeiShanErDu, -1);
            CardEffects.ExecuteEffect(actor, foe, card);
        }
        // 云剑•连雨：云剑牌。
        if (actor.HasBuff(BuffType.YunJianLianYu) && CardTypes.IsYunJian(actor, card.Id))
        {
            actor.ModifyBuffValue(BuffType.YunJianLianYu, -1);
            CardEffects.ExecuteEffect(actor, foe, card);
        }
        // 崩拳•双影（10000060）：崩拳牌；连崩牌（10000035）不扣层。
        if (actor.HasBuff(BuffType.BengQuanShuangYing) && CardTypes.IsBengQuan(actor, card.Id))
        {
            if (CardTypes.BaseId(card.Id) != 10000035) actor.ModifyBuffValue(BuffType.BengQuanShuangYing, -1);
            CardEffects.ExecuteEffect(actor, foe, card);
        }
        // 聚焰：火灵 / 土灵牌。
        if (actor.HasBuff(BuffType.JvYan) && (nm.Contains("火灵") || nm.Contains("土灵")))
        {
            actor.ModifyBuffValue(BuffType.JvYan, -1);
            CardEffects.ExecuteEffect(actor, foe, card);
        }

        // 出牌 = 该牌的效果（ExecuteEffect）+ 攻击。素卡走默认（加防+攻击），特殊卡走注册的效果（P5）。
        CardEffects.ExecuteEffect(actor, foe, card);

        // ── 主执行**之后**的两条「再执行」（原码 IL_2853 / IL_2958，偏移都在主执行 IL_27e5 之后）──
        // 刻印·引证 KeYinYinZheng：本牌在**星位**格 → 再执行一次（扣一层）。
        // ⚠ 早先与上面那一族一起挪到了主执行之前 —— 这一条原码在后面。
        if (actor.HasBuff(BuffType.KeYinYinZheng) && GridMarkFunctions.IsXingWei(actor, actor.CurrentCardGrid))
        {
            actor.ModifyBuffValue(BuffType.KeYinYinZheng, -1);
            CardEffects.ExecuteEffect(actor, foe, card);
        }
        // 灵阵回响 LingZhenHuiXiang（仙命 135）：灵阵牌以**基础稀有度**（baseId）再完整执行一次（扣一层），然后还原。
        // 原码：cardInfo.id = GetBaseCardId(id) → InitData → ModifyBuffValue(-1) → ExecuteEffect → 还原 InitData(原 id)。
        // 早先整段缺失。
        if (actor.HasBuff(BuffType.LingZhenHuiXiang) && actor.CurrentCardName.Contains("灵阵"))
        {
            BattleCard baseCard = actor.Config.BuildCard(CardTypes.BaseId(card.Id));
            var ctx = CardContext.Capture(actor);
            CardContext.Set(actor, baseCard);
            actor.ModifyBuffValue(BuffType.LingZhenHuiXiang, -1);
            CardEffects.ExecuteEffect(actor, foe, baseCard);
            ctx.Restore(actor);
        }

        if (Trace is not null)
        {
            Trace($"{actor.Uid} grid={grid} card={card.Id}"
                + $" | after  {actor.Uid}(hp={actor.Hp},def={actor.Def}) {foe.Uid}(hp={foe.Hp},def={foe.Def})"
                + $" anima={actor.Anima} again={actor.CurrentCardActionAgain}");
            if (Environment.GetEnvironmentVariable("YX_TRACE_BUFFS") == "1")
                Trace($"      buffs {actor.Uid}: {actor.DumpBuffs()}| {foe.Uid}: {foe.DumpBuffs()}");
        }

        // 注：这里**不清理**当前牌上下文——原码里 cardConfig 是活的配置对象，出牌后的「行动权判定」
        //     仍要读它的 actionAgain / name。下一次 PlayCard 开头会整体覆写。
    }

    private static BattleOutcome Score(Combatant left, Combatant right, BattleInput input, int steps)
    {
        // 结束态判胜负：死战不倒的一方即使 hp<=0 也不算负。
        bool leftDead = left.Hp <= 0 && !DeathRules.CheckSiZhan(left);
        bool rightDead = right.Hp <= 0 && !DeathRules.CheckSiZhan(right);

        string? winnerUid;
        int gap;
        int life;
        if (rightDead && !leftDead)
        {
            winnerUid = left.Uid;
            gap = left.Hp - right.Hp;
            life = DeathRules.CalLifeDamage(left, right, input.Round, input.FastMode, left.Config.FateOtherParams(21).At(0));   // 仙命 21 的减免值（早先没传，恒 0）
        }
        else if (leftDead && !rightDead)
        {
            winnerUid = right.Uid;
            gap = right.Hp - left.Hp;
            life = DeathRules.CalLifeDamage(right, left, input.Round, input.FastMode, right.Config.FateOtherParams(21).At(0));   // 仙命 21 的减免值（早先没传，恒 0）
        }
        else
        {
            // 双亡或都活（未分胜负）：无生命伤害；血差取绝对值供摆牌目标函数参考。
            winnerUid = null;
            gap = Math.Abs(left.Hp - right.Hp);
            life = 0;
        }

        return new BattleOutcome
        {
            WinnerUid = winnerUid,
            LeftHp = left.Hp,
            RightHp = right.Hp,
            EndHpGap = gap,
            LifeDamage = life,
            Steps = steps,
        };
    }

    // 每次取当前默认（TryLoadDefault 自带缓存）：数据目录可能在首场结算前才由宿主指定（SimData.Initialize）。
    private static Config.IBattleConfig DefaultConfig =>
        (Config.IBattleConfig?)Config.JsonBattleConfig.TryLoadDefault() ?? Config.StubBattleConfig.Empty;

    /// <summary>
    /// 造一方的战斗态。<paramref name="paramQueue"/> 是**双方共享**的那条选牌参数队列
    /// （游戏把它挂在 battleExecuter 上，见 <see cref="Model.BattleParamQueue"/>）；
    /// 不传就按本方的 <see cref="SideInput.BattleParams"/> 单开一条（只有单侧取参数时才等价，
    /// 测试里单独造一边时用）。
    /// </summary>
    private static Dictionary<BuffType, int> ToBuffMap(IReadOnlyDictionary<int, int> m)
    {
        var d = new Dictionary<BuffType, int>();
        foreach (var kv in m) d[(BuffType)kv.Key] = kv.Value;
        return d;
    }

    internal static Combatant ToCombatant(SideInput s, Model.BattleParamQueue? paramQueue = null)
    {
        // 开局总血照游戏 BattleCharacter.GetStartMaxHp：
        //   境界 InvalidLevel(0) → 总血 = extraMaxHp（= SideInput.StartHp）
        //   否则              → 总血 = extraMaxHp + LevelConfig[level].baseMaxHp
        // 漏掉这段的后果：采集里凡 level>0 的用例双方起点就对不上（B4 实测 real leftHp 122~160，sim 只有 54~67），
        // 全盘皆错。B2/B3 一直是 InvalidLevel，所以从没暴露过。
        int startHp = s.StartHp + (s.Level == 0 ? 0 : DefaultConfig.LevelBaseMaxHp(s.Level));
        var c = new Combatant(s.FateStrategies, s.Talents)
        {
            Uid = s.Uid,
            Hp = startHp,
            MaxHp = startHp,
            Def = s.StartDef,
            Life = s.Life,
            // 命元的战斗内副本 `characterUI.tempLife`：开局 = `lastRoundData.life`，为 0 则退回 `publicData.life`
            // （原码 164787-164792）。⚠ 早先从不初始化（恒 0），于是「命元 <= 0 即死」那条规则**开局就成立** ——
            // 卡 99000105 裂虚界元枪一出手就把对手 SetHp(0)（real `L=37` / sim `L=91`）。
            TempLife = s.Life,
            UnlockGrids = s.UnlockGrids,
            Board = FullBoard(s.Board, s.UnlockGrids),
            Deck = new BattleDeck(s.UnlockGrids),
            BattleKeYinCards = s.KeYinCards,
            Params = paramQueue ?? new Model.BattleParamQueue(s.BattleParams),
            EstimateParams = s.EstimateParams,
            SelectedResonance = s.Resonance,
            Level = s.Level,
            Config = DefaultConfig,
            CharacterId = s.CharacterId,
            LastRoundExp = s.LastRoundExp,
            Exp = s.LastRoundExp,
            Sect = s.Sect,
            Career = s.Career,
            LastRoundHandCards = s.LastRoundHandCards,
            LastRoundPermanentBuffs = ToBuffMap(s.PermanentBuffs),
            TalentTempDatas = s.TalentTempDatas,
            ResonancePermanentFlags = s.ResonancePermanentFlags,
            TalentDatas = s.TalentDatas,
            PrivateTalentDatas = s.PrivateTalentDatas,
            MaxKeYin = s.MaxKeYin,
        };
        // Game InitData restores carried physique before OnBattleStarted.
        // Initial state uses direct assignment, avoiding gain-physique hooks.
        int initialBody = c.GetLastRoundPermanentBuffValue(BuffType.TiPo);
        if (initialBody > 0) { c.TiPo = initialBody; c.MaxHp += initialBody; c.Hp += initialBody; }
        int initialBodyCap = c.GetLastRoundPermanentBuffValue(BuffType.TiPoShangXian);
        if (initialBodyCap > 0) c.SetBuffValue(BuffType.TiPoShangXian, initialBodyCap);
        for (int i = 0; i < c.Board.Count; i++) c.ReplaceCard(i, c.Board[i]);   // 卡 19 按天赋改牌面
        var used = new int[c.Board.Count];
        for (int i = 0; i < used.Length; i++) used[i] = c.Board[i].Id;
        c.UsedCardsAtStart = used;
        c.Deck.OnResetToPuGong = slot => c.ReplaceCard(slot, PuGong);   // 原码 InitData(0)：该格永久变普攻
        return c;
    }
}
