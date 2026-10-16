using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 2026-09-29 游戏更新中，数据驱动抽不对 / 抽不全的卡牌效果；其余走 card_effects 的 ops。
/// 每张都照 <c>Card_&lt;baseId&gt;.OnExecuted</c> 的执行顺序搬（状态机里 await 的攻击段位置已按 IL 路径核对）。
/// 抽取器出错的原因写在每张卡上方，方便日后抽取器升级后判断能不能让位给数据路径。
/// </summary>
public static partial class CardEffects
{
    private static void RegisterNewCards0929()
    {
        // 月旋：条件只在转换前判断一次；数据抽取器给三句重复附条件，清空剑意后会漏加攻。
        ByBase[311] = static (src, dst, card) =>
        {
            int value = src.GetBuffValue(BuffType.JianYi);
            if (value > 0)
            {
                src.ModifyBuffValue(BuffType.JianYi, -value);
                src.ModifyBuffValue(BuffType.JiaGong, value);
            }
            Attack(src, dst, card);
        };

        // 五行护身：防、护体和先机护体之后，激活后一格牌的五行。
        ByBase[197] = static (src, dst, card) =>
        {
            src.ModifyDef(card.Def);
            src.ModifyBuffValue(BuffType.HuTi, src.Config.CardOtherParams(card.Id).At(0));
            if (!src.HadUsed(src.CurrentCardGrid)) src.ModifyBuffValue(BuffType.HuTi, 1);
            WuXingFunctions.ActiveWuXingInName(src,
                GridFunctions.CardNameAt(src, GridFunctions.GetNextGrid(src, src.CurrentCardGrid)));
        };

        // 433 狸之力：生命 +o0，命元（临时生命）+o1。抽取器丢了 `if (!isMirror) ModifyTempLife`（sim 无镜像对局）。
        ByBase[433] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            src.ModifyHp(p.At(0));
            src.ModifyTempLife(p.At(1));
        };

        // 435 逍遥游：攻击 → 本回合再行动次数上限 +1 → 挂一层[刻印·山风削]（回合末把上限扣回来）。
        // 抽取器不认 `battleTempData.actionAgainPerRound++`，整句丢了但仍标 auto。
        ByBase[435] = static (src, dst, card) =>
        {
            Attack(src, dst, card);
            src.ActionAgainPerRound++;
            src.ModifyBuffValue(BuffType.KeYinShanFeng, 1);
        };

        // 436 极•灯焰飞舞：攻击后重复 o0 次，每次施加 o1~o2 层内伤（每次单独取一次随机值）。
        // 抽取器把 for 循环折成了一次。
        ByBase[436] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            Attack(src, dst, card);
            for (int i = 0; i < p.At(0); i++)
            {
                int v = src.GetNextRandomValue(ParamRequest.Range(436, p.At(1), p.At(2)));
                if (v == -1) v = (p.At(1) + p.At(2)) / 2;
                dst.ModifyBuffValue(BuffType.NeiShang, v);
            }
        };

        // 438 极•双鸳逆克：灵气 / 防 / 身法，然后「前一格的牌五行克制后一格 → 激活后一格的五行」。
        // 抽取器只留下前三句，读相邻牌名的判定整段丢了。
        ByBase[438] = static (src, dst, card) =>
        {
            src.ModifyAnima(AnimaOf(src, card));
            src.ModifyDef(card.Def);
            src.ModifyBuffValue(BuffType.ShenFa, src.Config.CardOtherParams(card.Id).At(0));
            int grid = src.CurrentCardGrid;
            string prev = GridFunctions.CardNameAt(src, GridFunctions.GetPreviousGrid(src, grid));
            string next = GridFunctions.CardNameAt(src, GridFunctions.GetNextGrid(src, grid));
            if (WuXingFunctions.IsKeZhi(prev, next)) WuXingFunctions.ActiveWuXingInName(src, next);
        };

        // 455 天髓逆克诀：激活卡组里出现过的每一种五行；卡组恰好两种五行时挂[天髓逆克诀]
        // （之后五行牌与上一张存在克制 → 再行动，见 CardHookFunctions）。抽取器折不了两层 for。
        ByBase[455] = static (src, dst, card) =>
        {
            src.ModifyAnima(AnimaOf(src, card));
            var seen = new List<string>();
            string[] elements = ["金灵", "水灵", "木灵", "火灵", "土灵"];
            for (int g = 0; g < src.Board.Count; g++)
            {
                string name = GridFunctions.CardNameAt(src, g);
                foreach (string e in elements)
                    if (name.Contains(e) && !seen.Contains(e)) seen.Add(e);
            }
            foreach (string e in seen) WuXingFunctions.ActiveWuXingInName(src, e);
            if (WuXingFunctions.GetWuXingCountInDeck(src) == 2 && seen.Count == 2)
                src.ModifyBuffValue(BuffType.TianSuiNiKeJue, 1);
        };

        // 1000105 浮云飘零步：灵气 / 云海，然后按格子奇偶：
        //   奇数格 → 生命 +(o1 + 灵气×o3)，超过上限的部分改为加防；偶数格 → 身法 +o2。
        ByBase[1000105] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            src.ModifyAnima(AnimaOf(src, card));
            src.ModifyBuffValue(BuffType.YunHai, p.At(0));
            if (IsOddGrid(src))
            {
                int heal = p.At(1) + src.Anima * p.At(3);
                int fit = Math.Min(heal, src.MaxHp - src.Hp);
                src.ModifyHp(fit);
                if (heal > fit) src.ModifyDef(heal - fit);
            }
            else src.ModifyBuffValue(BuffType.ShenFa, p.At(2));
        };

        // 4000107 星弈•尖：攻击；奇数格 → 星力 +o0；偶数格 → 把至多 o1 星力转成护体。
        ByBase[4000107] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            Attack(src, dst, card);
            if (IsOddGrid(src)) src.ModifyBuffValue(BuffType.XingLi, p.At(0));
            else
            {
                int n = Math.Min(p.At(1), src.GetBuffValue(BuffType.XingLi));
                if (n > 0)
                {
                    src.ModifyBuffValue(BuffType.XingLi, -n);
                    src.ModifyBuffValue(BuffType.HuTi, n);
                }
            }
        };

        // 4000108 风雷涌动：灵气 → 取一次 def~randomDef；与「雷」牌相邻时用这个值攻击，否则加防 → 10% 再行动。
        // 抽取器把「相邻雷牌改为攻击」丢了，恒加防。
        ByBase[4000108] = static (src, dst, card) =>
        {
            src.ModifyAnima(AnimaOf(src, card));
            int rndDef = src.Config.CardRandomDef(card.Id);
            int v = src.GetNextRandomValue(ParamRequest.Range(4000108, card.Def, Math.Max(card.Def, rndDef)));
            if (v == -1) v = (card.Def + Math.Max(card.Def, rndDef)) / 2;
            int grid = src.CurrentCardGrid;
            string prev = GridFunctions.CardNameAt(src, GridFunctions.GetPreviousGrid(src, grid));
            string next = GridFunctions.CardNameAt(src, GridFunctions.GetNextGrid(src, grid));
            if (prev.Contains('雷') || next.Contains('雷')) CombatMath.Attack(src, dst, v, 1);
            else src.ModifyDef(v);
            if (src.GetNextRandomValue(ParamRequest.Percent(4000108)) < 10) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        };

        // 4000109 魂咒卦诀：卦象 +guaXiang（对方有负面时多 o0）；奇数格施加 o1 内伤，偶数格施加 o2 外伤。
        ByBase[4000109] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            int gx = src.Config.CardGuaXiang(card.Id);
            if (dst.GetDebuffCount() > 0) gx += p.At(0);
            src.ModifyBuffValue(BuffType.GuaXiang, gx);
            if (IsOddGrid(src)) dst.ModifyBuffValue(BuffType.NeiShang, p.At(1));
            else dst.ModifyBuffValue(BuffType.WaiShang, p.At(2));
        };

        // 7000110 双生印：攻击后依次激活后两格牌的五行；这两格按顺序相生 → 加 o1 防。
        // （描述写「后 o[0] 格」，但原码写死取两格：GetNextGrid 两次。）
        ByBase[7000110] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            Attack(src, dst, card);
            int g = GridFunctions.GetNextGrid(src, src.CurrentCardGrid);
            string first = GridFunctions.CardNameAt(src, g);
            WuXingFunctions.ActiveWuXingInName(src, first);
            g = GridFunctions.GetNextGrid(src, g);
            string second = GridFunctions.CardNameAt(src, g);
            WuXingFunctions.ActiveWuXingInName(src, second);
            if (WuXingFunctions.IsXiangSheng(first, second, XiangShengZhiHuo(src))) src.ModifyDef(p.At(1));
        };

        // 7000111 五行阵旗：用手牌里 1 张五行灵阵牌（最高 o0 级）；奇数格 → 自身减 o1 生命并再行动，
        // 偶数格 → 再用手牌里 1 张五行灵印牌。被用的牌自带再行动时给本张加一次再行动。
        // 手牌是玩家预选的参数（GetNextParam），取不到（-1）就跳过。
        ByBase[7000111] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            int id = src.NextParam(ParamRequest.HandPick(7000111, HandPickFilter.WuXingLingZhen, p.At(0)));
            if (id != -1 && TriggerCard(src, dst, id)) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
            if (IsOddGrid(src))
            {
                src.ModifyHp(-p.At(1));
                src.ModifyBuffValue(BuffType.ExActionAgain, 1);
            }
            else
            {
                id = src.NextParam(ParamRequest.HandPick(7000111, HandPickFilter.WuXingLingYin, 0));
                if (id != -1 && TriggerCard(src, dst, id)) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
            }
        };

        // 10000104 镇岳掌：奇数格体魄 +physique，偶数格气势 +o0；然后攻击 attack + 体魄/o1（只打一段）。
        ByBase[10000104] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            if (IsOddGrid(src)) src.ModifyTiPo(PhysiqueOf(src, card));
            else src.ModifyBuffValue(BuffType.QiShi, p.At(0));
            int atk = card.Attack;
            if (p.At(1) > 0) atk += src.GetBuffValue(BuffType.TiPo) / p.At(1);
            CombatMath.Attack(src, dst, atk, 1);
        };

        // 10000106 闪身侧袭：攻击 → 加攻 o0 → 最多耗 o1 灵气（不够的用气势补），每耗 1 点加 o2 身法。
        ByBase[10000106] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            Attack(src, dst, card);
            src.ModifyBuffValue(BuffType.JiaGong, p.At(0));
            int paid = src.TryCostAnima(p.At(1));
            int lack = p.At(1) - paid;
            if (lack > 0)
            {
                int qs = Math.Min(lack, src.GetBuffValue(BuffType.QiShi));
                src.ModifyBuffValue(BuffType.QiShi, -qs);
                paid += qs;
            }
            src.ModifyBuffValue(BuffType.ShenFa, paid * p.At(2));
        };

        // 10000109 修罗玄气斩：一次攻击两段 —— (attack + 灵气)、(attack + 自身负面层数)；
        // 然后本牌造成的实际伤害每 o0 点回 1 生命。抽取器把两段不同值的攻击抽成了一段 attack。
        ByBase[10000109] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            int first = card.Attack + src.Anima;
            int second = card.Attack + src.GetDebuffCount();
            CombatMath.Attack(src, dst, [first, second]);
            if (src.HasBuff(BuffType.ActualDamage) && p.At(0) > 0)
                src.ModifyHp(src.GetBuffValue(BuffType.ActualDamage) / p.At(0));
        };

        // 402 幻•浮云连环步（本次更新改过：旧版挂「云剑•连雨」，新版按格子奇偶分支）：
        //   灵气 / 云海，奇数格挂一层[浮云连环步]（下张云剑连续生效两次），偶数格身法 +o1。
        //   抽取器把奇偶分支整段丢了（新卡对拍 b7_11 抓到）。
        ByBase[402] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            src.ModifyAnima(AnimaOf(src, card));
            src.ModifyBuffValue(BuffType.YunHai, p.At(0));
            if (IsOddGrid(src)) src.ModifyBuffValue(BuffType.FuYunLianHuanBu, 1);
            else src.ModifyBuffValue(BuffType.ShenFa, p.At(1));
        };

        // 335 梦•天命重现（老卡；新卡对拍 b7_33 抓到）：自身减 o0 生命；[先机]（本格第一次出）令对方跳至上一张牌
        //   （原码 dst.RightMoveCardItems(1, CareSkip: true)）；对方已用过牌则再次行动。抽取器丢了先机那句。
        ByBase[335] = static (src, dst, card) =>
        {
            src.ModifyHp(-src.Config.CardOtherParams(card.Id).At(0));
            if (!src.HadUsed(src.CurrentCardGrid)) dst.Deck.RightMoveCardItems(1, careSkip: true);
            src.CurrentCardActionAgain = dst.GetBuffValue(BuffType.UsedCardCount) > 0;
        };

        // 10000074 梦•崩拳突（09-22→09-28 改过的老卡，新卡对拍 b7_257 / 275 / 284 / 292 抓到）：
        //   元婴及以下：有身法或气势 → 本张与下张崩拳 +o0 攻；元婴以上：按 身法 / o0 加攻。
        //   原码写的是 `if (flag | flag2)`（按位或），抽取器不认，低境界这条整句丢了。
        ByBase[10000074] = static (src, dst, card) =>
        {
            var p = src.Config.CardOtherParams(card.Id);
            int ex = 0;
            int shenFa = src.GetBuffValue(BuffType.ShenFa);
            if (src.Config.CardLevel(card.Id) <= LevelYuanYing)
            {
                if (shenFa > 0 || src.GetBuffValue(BuffType.QiShi) > 0) ex = p.At(0);
            }
            else if (shenFa > 0 && p.At(0) > 0) ex = shenFa / p.At(0);
            CombatMath.Attack(src, dst, card.Attack + ex, card.AttackCount);
            src.ModifyBuffValue(BuffType.BengQuanChuo, ex);
        };
    }

    /// <summary>原码 <c>(cardItem.gridNumber + 1) % 2 == 1</c>：卡面的「奇数格」（格子从 1 数）。</summary>
    private static bool IsOddGrid(Combatant src) => (src.CurrentCardGrid + 1) % 2 == 1;

    /// <summary>卡的体魄：输入里带的优先，否则取配置表。</summary>
    private static int PhysiqueOf(Combatant src, BattleCard card)
        => card.Physique != 0 ? card.Physique : (src.Config as JsonBattleConfig)?.Card(card.Id)?.Physique ?? 0;
}
