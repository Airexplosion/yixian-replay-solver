using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// **开局效果**：忠实移植 <c>BattleCharacter.OnBattleStarted()</c> 里逐格跑的那一段
/// （反编译 <c>TriggerOpening(int grid, int triggerGrid = -1, bool judge = false, int rarityCap = 2)</c>，12401；
/// 战斗开始时的调用点 4801，状态机 <c>__CG_OnBattleStarted_d__40</c>）。
///
/// 时机：**炼化 / 仙命之后、第一张牌之前**，按格位 0..N 顺序各跑一次。
/// 参数：<c>grid</c> = 本格；<c>triggerGrid</c> 缺省 = grid（"对方哪一格的牌受影响"就是本格）。
///
/// 为什么要单列出来：这张表按 <c>cardBaseId</c> 分派 **22 条**，不抽出来的话这些卡的开局效果**静默全丢**。
/// 实测最典型的是卡 114 粽子试炼 —— 它的开局把**对方血压到 1**，
/// 于是真实对局是「R 从 1 血开始，L 攒够 8 灵气后打出本卡（伤害 = R 的 maxHp = 100）→ R = 1-100 = -99」，
/// 而 sim 少了开局这一下，R 从 100 起手 → 只算出 0，两侧全错。
///
/// ⬜ 未移植（都需要本层没有的设施，见覆盖账）：
///   · 11000018 / 369 的 **<c>LevelDown()</c>** 分支（要稀有度变体表；对方牌 rarity ≥ 1 时降一档）；
///   · 389 的 <c>AddXingWei(前/后格)</c> + 给空格填卡 4000038（要**星位**与格位改写）；
///   · 369 不可降级时的 <c>AddMengEJieSkipPos</c>（属"出牌前跳过链"，整条未做）；
///   · 头部 <c>if (rarity &gt; rarityCap) num = cardBaseId + 10000 * rarityCap;</c> 的稀有度截断
///     （只影响 `cardConfigDict[num]` 取哪一档，otherParams 不随稀有度变，对本层无差）。
/// </summary>
internal static class CardOpeningFunctions
{
    /// <summary>
    /// 开战**头部**初始化（原码 OnBattleStarted 的 default 段：星位布置 + 三条无条件初始化）。
    /// 逐格[开局]见 <see cref="TriggerAll"/>；整体编排见 <see cref="BattleStartFunctions.OnBattleStarted"/>。
    /// </summary>
    public static void OnBattleStartHead(Combatant self)
    {
        // ── OnBattleStarted 里**先于 TriggerOpening** 的那一段星位布置（原码 3599-3621）──
        //     battleCharacter.AddXingWei(2, showText: false);
        //     battleCharacter.AddXingWei(5, showText: false);
        //     for (int i = 0; i < m_TempBattleCardItems.Count; i++)
        //     {
        //         int id = m_TempBattleCardItems[i].cardConfig.id;
        //         if (id == 4000079 || id == 4010079 || id == 4020079 || id == 4030079 || id == 4040079)
        //         {
        //             if (!IsXingWei(GetPreviousGrid(this, i))) AddXingWei(GetPreviousGrid(this, i), false);
        //             if (!IsXingWei(GetNextGrid(this, i)))     AddXingWei(GetNextGrid(this, i), false);
        //         }
        //     }
        // ⚠ **格 2 / 格 5 是无条件标成星位的**，跟带了什么牌无关 —— 之前整段没搬，
        //   于是「摆在格 2 或格 5 的牌吃不到 [星位] 加星力」这个规则在 sim 里完全不存在。
        //   （B5 单卡盘面把牌放在格 0/1，所以这条对 B5 指标**看不出来**；
        //     但真实盘面 8 格都会摆牌，它对「摆牌搜索」是实打实的一条。）
        // ⚠ 卡面「[被动]：[相邻]格是[星位]」（卡 4000079 梦•斗转星移）就是第二个循环：
        //   它按**这张牌所在的格**给左右邻格置位，且置位前先查 `!IsXingWei` —— 查这一次不是为了省事，
        //   是因为 `AddXingWei` 对**已是星位**的格会改成 `ModifyAnima(1)`（星位聚灵），不查就白送灵气。
        // ⚠ 顺序：这两段在 `TriggerOpening`(4801) **之前**，不能挪到逐格 Trigger 之后。
        GridMarkFunctions.AddXingWei(self, 2);
        GridMarkFunctions.AddXingWei(self, 5);
        for (int i = 0; i < self.Board.Count; i++)
        {
            int id = self.Board[i].Id;
            if (id != 4000079 && id != 4010079 && id != 4020079 && id != 4030079 && id != 4040079) continue;
            int prev = GridFunctions.GetPreviousGrid(self, i);
            int next = GridFunctions.GetNextGrid(self, i);
            if (!GridMarkFunctions.IsXingWei(self, prev)) GridMarkFunctions.AddXingWei(self, prev);
            if (!GridMarkFunctions.IsXingWei(self, next)) GridMarkFunctions.AddXingWei(self, next);
        }

        // ── 开局的三条**无条件**初始化（原码 3622-3624，紧跟上面的星位布置）──
        //     SetBuffValue(BuffType.QiShiShangXian, 6);
        //     SetBuffValue(BuffType.LastTurnStartHp, battleTempData.hp);
        //     SetBuffValue(BuffType.JiLuZhanDouZuiGaoShengMing, battleTempData.hp);
        // ⚠ **`QiShiShangXian` 缺这一条是致命的**：上限 buff 默认 0，而 `CombatantBuffs` 里的截断逻辑是
        //   `if (气势 > 上限) { 上限以上的部分截掉 + 转成加防 + 生气凌人追加伤害 }` ——
        //   上限恒为 0 时，**气势永远涨不起来**（每加 1 点立刻被截成 0 并变成 +1 防），
        //   整条气势线（崩灭心法 / 气盛凌人 / 刻印109 / 仙命428 等）全部走样。
        // ⚠ 顺序：原码里这三条在 `TriggerOpening`(4801) **之前**、星位布置之后。
        // ⚠ `LastTurnStartHp` / `JiLuZhanDouZuiGaoShengMing` 都是「战斗最高生命 / 上回合起始血」的记录起点，
        //   后面对血量做比较的牌（如按损失生命计数的那些）都读它们。
        // ⚠ 放在这里安全：此时 `QiShi = 0 <= 6`，截断分支不会触发（`SetBuffValue` 会走一遍 buff 钩子）。
        self.SetBuffValue(BuffType.QiShiShangXian, 6);
        self.SetBuffValue(BuffType.LastTurnStartHp, self.Hp);
        self.SetBuffValue(BuffType.JiLuZhanDouZuiGaoShengMing, self.Hp);
    }

    /// <summary>
    /// 逐格[开局]（原码 OnBattleStarted 的 IL_4a52 循环：`if (TriggerOpening(i, -1, judge: true)) TriggerOpening(i);`）。
    /// ⚠ 在开战流程里位于 天赋 / 共鸣 / 仙命 / 上回合永久 buff **之后**、刻印开战**之前**（见 BattleStartFunctions）。
    /// </summary>
    public static void TriggerAll(Combatant self)
    {
        // 逐格：只跑真正摆了牌的格（空格 = card 0 = 普攻，baseId 0 落 default，无效果）。
        for (int grid = 0; grid < self.Board.Count; grid++)
            Trigger(self, grid);
    }

    /// <summary>单格触发[开局]（原码 TriggerOpening(grid)）。跳过链里「星弈•跳 / 洞烛机先」也会逐格调用。</summary>
    /// <param name="triggerGrid">触发者所在格（原码 TriggerOpening 的第二参，缺省 = 本格）：厄劫缠身读对方这一格、治孤按它找相邻格。
    /// 天星•牵引替后面的牌触发时传的是牵引自己的格子。</param>
    internal static void Trigger(Combatant self, int grid, int triggerGrid = -1)
    {
        if (triggerGrid < 0) triggerGrid = grid;
        BattleCard card = self.Board[grid];
        Combatant foe = self.Opponent;
        IReadOnlyList<int> p = self.Config.CardOtherParams(card.Id);

        switch (CardTypes.BaseId(card.Id))
        {
            // -3：对方血量上限清空 + 挂 10 层「无法加生命」。rare baseId，保留原样。
            case -3:
                foe.ModifyMaxHp(-foe.MaxHp);
                foe.ModifyBuffValue(BuffType.WuFaJiaShengMing, 10);
                break;

            // 55–58：五行灵阵类，开局直接激活一对五行（相生环上的相邻两灵）。
            case 55:
                self.ModifyBuffValue(BuffType.JiHuoShuiLing, 1);
                self.ModifyBuffValue(BuffType.JiHuoMuLing, 1);
                break;
            case 56:
                self.ModifyBuffValue(BuffType.JiHuoHuoLing, 1);
                self.ModifyBuffValue(BuffType.JiHuoTuLing, 1);
                break;
            case 57:
                self.ModifyBuffValue(BuffType.JiHuoTuLing, 1);
                self.ModifyBuffValue(BuffType.JiHuoJinLing, 1);
                break;
            case 58:
                self.ModifyBuffValue(BuffType.JiHuoJinLing, 1);
                self.ModifyBuffValue(BuffType.JiHuoShuiLing, 1);
                break;

            // 114 粽子试炼：**把对方打剩 1 血**（ModifyHp(1 - 当前血)），自己挂「粽子试炼」。
            // 卡面只说「8回合内加满生命方能通过试炼」，这一下是描述里没写的隐藏开局。
            case 114:
                if (foe.Hp > 1) foe.ModifyHp(1 - foe.Hp);
                self.ModifyBuffValue(BuffType.ZongZiShiLian, 1);
                break;

            // 11000001：对方直接少 otherParams[1] 点生命。
            case 11000001:
                foe.ModifyHp(-p.At(1));
                break;

            // 11000005：自身加 otherParams[1] 点生命及上限。
            case 11000005:
                self.ModifyMaxHp(p.At(1));
                self.ModifyHp(p.At(1));
                break;

            // 11000009 / 7000079：自身回 otherParams[0] / [1] 点灵气。
            case 11000009:
                self.ModifyAnima(p.At(0));
                break;
            case 7000079:
                self.ModifyAnima(p.At(1));
                break;

            // 11000013：自身挂 otherParams[2] 层「避邪」。
            case 11000013:
                self.ModifyBuffValue(BuffType.BiXie, p.At(2));
                break;

            // 11000014：自身加 otherParams[2] 点防御。
            case 11000014:
                self.ModifyDef(p.At(2));
                break;

            // 11000022：[下次攻击随方] otherParams[1] 层。
            case 11000022:
                self.ModifyBuffValue(BuffType.XiaCiGongJiSuiFang, p.At(1));
                break;

            // 11000023：双方各掉 otherParams[1] 点血（伤敌亦伤己）。
            case 11000023:
                foe.ModifyHp(-p.At(1));
                self.ModifyHp(-p.At(1));
                break;

            // 11000024：对方挂 otherParams[1] 层内伤。
            case 11000024:
                foe.ModifyBuffValue(BuffType.NeiShang, p.At(1));
                break;

            // 203：自身回 otherParams[1] 灵气 + 加 otherParams[2] 层水势。
            case 203:
                self.ModifyAnima(p.At(1));
                self.ModifyBuffValue(BuffType.ShuiShi, p.At(2));
                break;

            // 181 幽冥化生壶：开局给**对方**挂 2 层「炼妖壶」，叠上出牌时的 otherParams[0]（=4）共 6 层。
            case 181:
                foe.ModifyBuffValue(BuffType.LianYaoHu, 2);
                break;

            // 315：激活火灵 + 金灵。
            case 315:
                self.ModifyBuffValue(BuffType.JiHuoHuoLing, 1);
                self.ModifyBuffValue(BuffType.JiHuoJinLing, 1);
                break;

            // 11000018 厄劫缠身：[开局] **降级对方的同格牌**；不可降级则对对方造成 otherParams[1] 反伤。
            // 原码 TriggerOpening case 11000018（12486）：
            //     CardItem ci = defaultOpponentTarget.m_BattleDeck[triggerGrid];
            //     if (ci.cardConfig.rarity >= 1 && ci.cardConfig.id != 19) ci.LevelDown();
            //     else ApplyDamage(defaultOpponentTarget, DamageInfo.Create(this, ReflectDamage,
            //                        cardConfigDict[num].otherParams[1], skipWoundCheck: true));
            // ⚠ 判据与卡体里的「可降级」完全一致（`rarity >= 1 && id != 19`）—— 普攻 rarity=0 **不可降级**，
            //   走 else 分支吃反伤（这正是「普攻要不要吃这一下」的关键区别）。
            case 11000018:
            {
                if (triggerGrid >= foe.Board.Count) break;
                BattleCard oppCard = foe.Board[triggerGrid];
                if (oppCard.Id != 19 && foe.Config.CardRarity(oppCard.Id) >= 1)
                    foe.ReplaceCard(triggerGrid, foe.Config.BuildCard(oppCard.Id - 10000));
                else
                {
                    int ekDmg = p.At(1);
                    if (ekDmg > 0)
                        CombatMath.ApplyDamage(self, foe, DamageInfo.Create(self, DamageType.ReflectDamage, ekDmg, skipWoundCheck: true));
                }
                break;
            }

            // ⬜ 369 梦•厄劫缠身：同型，但「不可降级」那一支走的是 `AddMengEJieSkipPos(triggerGrid)`
            //   （把这一格加入**跳过表**，出牌循环到它时跳过一次）—— sim 还没有「跳过指定格」的机制，
            //   而 `BattleDeck.Item.Skip` 是**整场出局**语义，两者不同，不能拿来顶替。

            // 389 星弈•治孤：[开局] **相邻两格成为星位**；**空格则放入「星弈•飞」**（按本卡稀有度）。
            // 原码 TriggerOpening 的 case 389（12585）：
            //     int previousGrid = GetPreviousGrid(this, triggerGrid);
            //     int nextGrid     = GetNextGrid(this, triggerGrid);
            //     AddXingWei(previousGrid); AddXingWei(nextGrid);
            //     int cardId = 4000038 + 10000 * cardConfigDict[baseId].rarity;
            //     if (GetBaseCardId(m_BattleDeck[previousGrid].cardConfig.id) == 0) { InitData(cardId, …); }
            //     if (GetBaseCardId(m_BattleDeck[nextGrid].cardConfig.id)     == 0) { InitData(cardId, …); }
            // ⚠ 这条**改写棋盘**（空格填牌），所以必须在牌组构建**之前**跑 —— 见 BattleResolver 里
            //   OnBattleStart 的调用顺序（ToCombatant 已建好 Board/Deck，Deck 按 Slot 惰性取牌，故能看见替换）。
            // 卡面后半句「[星位]：施加 2 层[虚弱]和 2 层[破绽]」在卡体 ops 里，门就是 `CheckXingWei(自己所在格)`
            //   —— 所以把自己的格子也算进去很关键：相邻两格置位后，本卡**不一定**在星位上。
            case 389:
            {
                int prevG = GridFunctions.GetPreviousGrid(self, triggerGrid);
                int nextG = GridFunctions.GetNextGrid(self, triggerGrid);
                GridMarkFunctions.AddXingWei(self, prevG);
                GridMarkFunctions.AddXingWei(self, nextG);
                int flyId = 4000038 + 10000 * self.Config.CardRarity(card.Id);
                if (CardTypes.BaseId(self.Board[prevG].Id) == 0) self.ReplaceCard(prevG, self.Config.BuildCard(flyId));
                if (CardTypes.BaseId(self.Board[nextG].Id) == 0) self.ReplaceCard(nextG, self.Config.BuildCard(flyId));
                break;
            }

            // 372：一次激活全部五行。
            case 372:
                self.ModifyBuffValue(BuffType.JiHuoMuLing, 1);
                self.ModifyBuffValue(BuffType.JiHuoHuoLing, 1);
                self.ModifyBuffValue(BuffType.JiHuoTuLing, 1);
                self.ModifyBuffValue(BuffType.JiHuoJinLing, 1);
                self.ModifyBuffValue(BuffType.JiHuoShuiLing, 1);
                break;

            // 1000086：**只在高境界成立**（level >= YuanYing=4），挂 otherParams[1] 层云海；
            // 低境界时原码直接 return false（整个开局不触发）。
            case 1000086:
                if (self.Config.CardLevel(card.Id) >= LevelYuanYing)
                    self.ModifyBuffValue(BuffType.YunHai, p.At(1));
                break;
        }
    }

    /// <summary>
    /// 这一格的牌**有没有[开局]效果** = 原码 <c>TriggerOpening(grid, grid, judge: true)</c> 的返回值。
    /// judge 模式下各 case 什么都不做，只看落没落进 switch：命中任一 case → true，<c>default</c> → false；
    /// 唯一例外是 1000086 低境界（&lt; 元婴）时 <c>return false</c>。
    /// ⚠ 列表以**原码**的 case 为准（含 sim 尚未移植效果的 369），不要按上面 Trigger 的 switch 抄。
    /// </summary>
    internal static bool HasOpening(Combatant self, int grid)
    {
        int id = self.Board[grid].Id;
        switch (CardTypes.BaseId(id))
        {
            case -3: case 55: case 56: case 57: case 58: case 114:
            case 11000001: case 11000005: case 11000009: case 11000013: case 11000014:
            case 11000018: case 11000022: case 11000023: case 11000024:
            case 203: case 181: case 315: case 7000079: case 369: case 372: case 389:
                return true;
            case 1000086:
                return self.Config.CardLevel(id) >= LevelYuanYing;
            default:
                return false;
        }
    }

    /// <summary>Level 枚举里的 YuanYing（4）；卡 1000086 的开局门槛。</summary>
    private const int LevelYuanYing = 4;
}
