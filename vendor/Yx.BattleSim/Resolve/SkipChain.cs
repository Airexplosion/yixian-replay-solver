using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;
using Yx.BattleSim.Effects;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Resolve;

/// <summary>
/// **出牌前的跳过链**：忠实移植 <c>BattleExecuter.Execute</c> 主循环里 <c>ShiftCard()</c> 之后、
/// <c>CheckAdjacentEffects / CheckCardCost</c> 之前的那一整段（原码 15450–15590）。
///
/// 结构是一个 <c>while (flag)</c> 大循环：十个分支按固定顺序检查当前牌，**任何一个分支跳过了牌就再来一轮**
/// （新取到的牌可能又命中前面的分支）。
///
/// 「跳过」= 原码 <c>SkipCardAnim</c> = <c>PushCard(当前牌) + ShiftCard()</c>：把当前牌放回队尾、取下一张。
/// ⚠ **纯跳过，不付耗生命** —— 唯一「跳过时照付耗生命并反伤」的是瞬影击那一支，它是在分支里自己写的。
///   （旧注释曾把 338 瞬影击的卡面「此法跳过的牌仍会耗生命」误记到 109 梅菜肉粽 / XingYi_Duan 头上，
///    导致当时按「半成品」判了负结果。XingYi_Duan 在引擎里就是纯跳过。）
/// ⚠ 被跳过的牌**不**标记 skip（持续/消耗牌的出局标记只在真正打出之后才打），照常回到队尾。
/// </summary>
internal static class SkipChain
{
    /// <summary>
    /// 从刚取出的 <paramref name="item"/> 开始跑跳过链，返回最终要出的那张。
    /// <paramref name="idOf"/> 把牌组条目解析成卡 id（格位越界 = 普攻 0）。
    /// </summary>
    public static BattleDeck.Item Run(Combatant fc, Combatant sc, BattleDeck.Item item, Func<BattleDeck.Item, int> idOf)
    {
        var cfg = fc.Config;
        int guard = 0;                                   // 防御性上限：数据脏时不至于死循环
        bool flag = true;
        while (flag && guard++ < 64)
        {
            flag = false;

            // ① 星弈•断 XingYi_Duan：跳过自身下 N 张牌（每跳一张扣一层）。
            while (fc.HasBuff(BuffType.XingYi_Duan))
            {
                item = Skip(fc, item);
                fc.ModifyBuffValue(BuffType.XingYi_Duan, -1);
                flag = true;
            }

            // ② 无名白鹿（99000214，双方任一方牌组里有）：前 N 格的**持续牌**被跳过（N = 双方卡里 o[0] 的最大值）。
            int currentKeYinId = 0;
            foreach (var who in new[] { fc, sc })
            {
                foreach (var c in who.Board)
                {
                    if (CardTypes.BaseId(c.Id) != 99000214) continue;
                    int v = cfg.CardOtherParams(c.Id).At(0);
                    if (v > currentKeYinId) currentKeYinId = v;
                }
            }
            while (currentKeYinId > 0 && item.Slot + 1 <= currentKeYinId && cfg.CardIsSustain(idOf(item)))
            {
                item = Skip(fc, item);
                flag = true;
            }

            // ③ 梦厄劫：被标记的格跳过（并清掉标记）。
            while (fc.HasBuff(BuffType.MengEJie) && item.Slot >= 0 && GridMarkFunctions.IsMengEJieSkipPos(fc, item.Slot))
            {
                GridMarkFunctions.RemoveMengEJieSkipPos(fc, item.Slot);
                item = Skip(fc, item);
                flag = true;
            }

            // ④ 仙命 398（开关生效）：第 5 格（下标 4）跳过；是星弈牌则先回血。
            while (fc.HasFateStrategy(398) && FateStrategyFunctions.IsSwitchActive(fc, 398) && item.Slot == 4)
            {
                if (CardTypes.IsXingYi(fc, idOf(item))) fc.ModifyHp(cfg.FateOtherParams(398).At(0));
                item = Skip(fc, item);
                flag = true;
            }

            // ⑤ 几张「放在最后几格不出」的牌：9000015 / 350（倒数两格内、未用过）、202（最后一格）、
            //    11000017（第 o[0] 格）。跳过时标记 hadUsed。
            int unlock = GridFunctions.UnlockGrids(fc);
            while (CardTypes.BaseId(idOf(item)) == 9000015 && item.Slot + 2 >= unlock && !fc.HadUsed(item.Slot))
            {
                fc.SetHadUsed(item.Slot, true);
                item = Skip(fc, item);
                flag = true;
            }
            while (CardTypes.BaseId(idOf(item)) == 202 && item.Slot + 1 >= unlock)
            {
                fc.SetHadUsed(item.Slot, true);
                item = Skip(fc, item);
                flag = true;
            }
            while (CardTypes.BaseId(idOf(item)) == 350 && item.Slot + 2 >= unlock && !fc.HadUsed(item.Slot))
            {
                fc.SetHadUsed(item.Slot, true);
                item = Skip(fc, item);
                flag = true;
            }
            while (CardTypes.BaseId(idOf(item)) == 11000017 && item.Slot == cfg.CardOtherParams(idOf(item)).At(0) - 1)
            {
                fc.SetHadUsed(item.Slot, true);
                item = Skip(fc, item);
                flag = true;
            }

            // ⑥ 星弈•跳 XingYiTiao：一直跳到下一张星弈牌（最多 8 次），被跳过的牌先触发其[开局]；然后清空层数。
            if (fc.HasBuff(BuffType.XingYiTiao))
            {
                int tiaoCount = 0;
                while (!cfg.CardName(idOf(item)).Contains("星弈") && tiaoCount <= 8)
                {
                    tiaoCount++;
                    if (fc.GetBuffValue(BuffType.XingYiTiao) > 0 && item.Slot >= 0)
                        CardOpeningFunctions.Trigger(fc, item.Slot);
                    item = Skip(fc, item);
                    flag = true;
                }
                fc.ModifyBuffValue(BuffType.XingYiTiao, -fc.GetBuffValue(BuffType.XingYiTiao));
            }

            // ⑦ 洞烛机先 DongZhuJiXian（卡 11000021）：第 o[2] / o[3] 格的牌改为触发[开局]并跳过（每次扣一层）。
            while (fc.HasBuff(BuffType.DongZhuJiXian)
                   && (item.Slot == cfg.CardOtherParams(11000021).At(2) - 1 || item.Slot == cfg.CardOtherParams(11000021).At(3) - 1))
            {
                if (item.Slot >= 0) CardOpeningFunctions.Trigger(fc, item.Slot);
                item = Skip(fc, item);
                fc.ModifyBuffValue(BuffType.DongZhuJiXian, -1);
                flag = true;
            }

            // ⑧ 瞬影击 ShunYingJi（卡 338）：跳过下 N 张**耗生命**的牌 —— 被跳过的牌**照付耗生命**，
            //    并对对方造成「耗生命 × 卡 338 的 o[2]」伤害（每跳一张扣一层）。
            //    ⚠ 338 自己就是耗生命牌（hc=2）：打出一次挂 2 层后，它自己下两次出场都会被这条跳过。
            while (fc.HasBuff(BuffType.ShunYingJi) && cfg.CardHpCost(idOf(item)) > 0)
            {
                int hpCost = cfg.CardHpCost(idOf(item));
                fc.ModifyHp(-hpCost, canRevive: false, isCost: true);
                CombatMath.ApplyDamage(fc, sc, DamageInfo.Create(fc, DamageType.Damage, hpCost * cfg.CardOtherParams(338).At(2)));
                item = Skip(fc, item);
                fc.ModifyBuffValue(BuffType.ShunYingJi, -1);
                flag = true;
            }
        }
        return item;
    }

    /// <summary>原码 SkipCardAnim：当前牌放回队尾，取下一张。</summary>
    private static BattleDeck.Item Skip(Combatant fc, BattleDeck.Item item)
    {
        fc.Deck.PushCard(item);
        return fc.Deck.ShiftCard();
    }
}
