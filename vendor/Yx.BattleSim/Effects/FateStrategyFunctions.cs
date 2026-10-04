using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 仙命（P3）：忠实移植自游戏 FateStrategyFunctions。<see cref="OnBattleStart"/> 是开局按仙命写 buff/血/防
/// 的一长串（原为带动画 await 的异步，headless 丢掉 await 化为顺序执行）；<see cref="IsSwitchActive"/> 为开关判定。
///
/// 依赖：src.Config.FateOtherParams(id) 取数值；牌桌/上回合数据经 Combatant 的中性钩子
/// （LastRoundPermanentBuff / WuXingCountInDeck / LastRoundExp / LastRoundHandCardCount / FateSwitchActive）。
/// 依赖牌桌 / 上一轮数据的仙命（120/327/334/404/384/417）都已接入（上一轮手牌等由 SideInput 喂入）。
/// </summary>
public static class FateStrategyFunctions
{
    /// <summary>原码 UpgradeCardInBattleDeck(1, cond)：按格序升级第一张「满足条件且可升级」的牌。</summary>
    private static void UpgradeFirst(Combatant src, Func<int, bool> cond)
    {
        for (int i = 0; i < src.Board.Count; i++)
        {
            int id = src.Board[i].Id;
            if (cond(id) && src.Config.CanUpgrade(id))
            {
                src.ReplaceCard(i, src.Config.BuildCard(id + 10000));
                return;
            }
        }
    }

    /// <summary>开关是否激活（对应 tempDatas[id]==0）。</summary>
    public static bool IsSwitchActive(Combatant src, int strategyId) => src.FateSwitchActive(strategyId);

    /// <summary>开局仙命效果。忠实顺序移植（await 动画已剥）。</summary>
    public static void OnBattleStart(Combatant src)
    {
        IBattleConfig cfg = src.Config;
        Combatant opp = src.Opponent;

        if (src.HasFateStrategy(89))
        {
            src.ModifyBuffValue(BuffType.LianYun, cfg.FateOtherParams(89).At(0));
            src.ModifyBuffValue(BuffType.YunHai, cfg.FateOtherParams(89).At(1));
        }
        if (src.HasFateStrategy(126))
        {
            int v = src.LastRoundPermanentBuff(BuffType.BaoCunGuaXiang);
            if (v > 0) src.ModifyBuffValue(BuffType.GuaXiang, v);
        }
        if (src.HasFateStrategy(161))
            src.ModifyBuffValue(BuffType.Min, cfg.FateOtherParams(161).At(0));
        if (src.HasFateStrategy(140))
        {
            int n = cfg.FateOtherParams(140).At(0) == 0 ? 0 : src.Hp / cfg.FateOtherParams(140).At(0);
            src.ModifyMaxHp(n);
            src.ModifyHp(n);
        }
        if (src.HasFateStrategy(143))
        {
            src.ModifyBuffValue(BuffType.JiHuoTuLing, 1);
            src.ModifyDef(cfg.FateOtherParams(143).At(0));
        }
        if (src.HasFateStrategy(27) && src.LastRoundHandCardCount > 0)
        {
            int n = src.Hp * cfg.FateOtherParams(27).At(0) / 100;
            src.ModifyMaxHp(n);
            src.ModifyHp(n);
        }
        if (src.HasFateStrategy(333))
        {
            src.ModifyBuffValue(BuffType.JiHuoJinLing, 1);
            src.ModifyBuffValue(BuffType.FengRui, cfg.FateOtherParams(333).At(0));
        }
        // 298：护身法宝上回合值 → 护体（keyed on 永久 buff，非 HasFateStrategy）。
        {
            int v = src.LastRoundPermanentBuff(BuffType.HuShenFaBao);
            if (v > 0) src.ModifyBuffValue(BuffType.HuTi, v);
        }
        if (src.HasFateStrategy(138))
        {
            src.ModifyBuffValue(BuffType.JiHuoMuLing, 1);
            src.ModifyAnima(cfg.FateOtherParams(138).At(0));
        }
        // 仙命 120：第 8 格是卡 11 则升级它，是空格则放入卡 11。早先因「要牌桌改写」跳过。
        if (src.HasFateStrategy(120) && src.Board.Count >= 8)
        {
            int id7 = src.Board[7].Id;
            if (CardTypes.BaseId(id7) == 11 && cfg.CanUpgrade(id7)) src.ReplaceCard(7, cfg.BuildCard(id7 + 10000));
            else if (CardTypes.BaseId(id7) == 0) src.ReplaceCard(7, cfg.BuildCard(11));
        }
        // 仙命 131：按 x0109 天赋的档位激活一对五行（原码 FateStrategyFunctions.OnBattleStart 里就是 HasFateStrategy(131)）。
        // ⚠ 更正：早先把门改成了「共鸣 38」—— 那是 OnBattleStarted 里**另一处**同形状代码（现在在 BattleStartFunctions 里），
        //   两处都存在；改门会让仙命 131 失效、并在补上共鸣 38 后重复激活。
        if (src.HasFateStrategy(131))
        {
            if (src.HasTalent(10109)) { src.ModifyBuffValue(BuffType.JiHuoShuiLing, 1); src.ModifyBuffValue(BuffType.JiHuoMuLing, 1); }
            if (src.HasTalent(20109)) { src.ModifyBuffValue(BuffType.JiHuoMuLing, 1); src.ModifyBuffValue(BuffType.JiHuoHuoLing, 1); }
            if (src.HasTalent(30109)) { src.ModifyBuffValue(BuffType.JiHuoHuoLing, 1); src.ModifyBuffValue(BuffType.JiHuoTuLing, 1); }
            if (src.HasTalent(40109)) { src.ModifyBuffValue(BuffType.JiHuoTuLing, 1); src.ModifyBuffValue(BuffType.JiHuoJinLing, 1); }
            if (src.HasTalent(50109)) { src.ModifyBuffValue(BuffType.JiHuoJinLing, 1); src.ModifyBuffValue(BuffType.JiHuoShuiLing, 1); }
        }
        if (src.HasFateStrategy(146))
        {
            src.ModifyBuffValue(BuffType.JiHuoHuoLing, 1);
            int n = cfg.FateOtherParams(146).At(0);
            opp.ModifyHp(-n);
            opp.ModifyMaxHp(-n);
        }
        if (src.HasFateStrategy(109))
            src.ModifyBuffValue(BuffType.JianZhenHuTi, cfg.FateOtherParams(109).At(0));
        if (src.HasFateStrategy(135) && IsSwitchActive(src, 135))
            src.ModifyBuffValue(BuffType.LingZhenHuiXiang, 1);
        if (src.HasFateStrategy(151))
        {
            if (IsSwitchActive(src, 151))
                src.ModifyBuffValue(BuffType.BiXie, cfg.FateOtherParams(151).At(0));
            src.ModifyHp(cfg.FateOtherParams(151).At(1));
        }
        if (src.HasFateStrategy(164) && IsSwitchActive(src, 164))
        {
            src.ModifyBuffValue(BuffType.GongMingMingXinRuXuan, 1);
            opp.ModifyBuffValue(BuffType.GongMingMingXinRuXuan, 1);
        }
        if (src.HasFateStrategy(322)) src.ModifyBuffValue(BuffType.KuangJian, 1);
        if (src.HasFateStrategy(326)) src.ModifyBuffValue(BuffType.YanQi, 1);
        // 327 / 334 / 404：升级牌桌牌 → P5 牌桌数据，跳过。
        // 仙命 327 / 334：开战升级一张「雷 / 描述带再次行动」/「木灵」牌。早先因「要牌桌升级」跳过。
        if (src.HasFateStrategy(327))
            UpgradeFirst(src, id => cfg.CardName(id).Contains("雷") || cfg.CardDesc(id).Contains("再次行动"));
        if (src.HasFateStrategy(334))
            UpgradeFirst(src, id => cfg.CardName(id).Contains("木灵"));
        if (src.HasFateStrategy(331))
            src.ModifyBuffValue(BuffType.QiXingLianZhu, cfg.FateOtherParams(331).At(0));
        if (src.HasFateStrategy(431))
            src.ModifyBuffValue(BuffType.TianYanFengLingDuanQu, cfg.FateOtherParams(431).At(0));
        if (src.HasFateStrategy(336))
            src.ModifyBuffValue(BuffType.FengLingZhanYi, cfg.FateOtherParams(336).At(0));
        if (src.HasFateStrategy(340))
            src.ModifyMaxHp(cfg.FateOtherParams(340).At(0));
        if (src.HasFateStrategy(342))
            opp.ModifyBuffValue(BuffType.DengYanCuiXin, cfg.FateOtherParams(342).At(0));
        if (src.HasFateStrategy(344)) src.ModifyBuffValue(BuffType.ZhuQueZhiLei, 1);
        if (src.HasFateStrategy(379))
            src.ModifyBuffValue(BuffType.JianQi, cfg.FateOtherParams(379).At(0));
        // 384：上一轮手牌 + 已用牌（= 盘面）里的云剑数 / 2 → 灵气，上限 o[0]（原码 190334）。
        if (src.HasFateStrategy(384))
        {
            int yun = 0;
            foreach (int id in src.LastRoundHandCards) if (CardTypes.IsYunJian(src, id)) yun++;
            for (int j = 0; j < src.Board.Count; j++) if (CardTypes.IsYunJian(src, src.Board[j].Id)) yun++;
            int n = Math.Min(yun / 2, cfg.FateOtherParams(384).At(0));
            if (n > 0) src.ModifyAnima(n);
        }
        if (src.HasFateStrategy(395))
            src.ModifyBuffValue(BuffType.WuJingGuaYan, cfg.FateOtherParams(395).At(0));
        if (src.HasFateStrategy(396)) src.ModifyBuffValue(BuffType.KeYinJingLei, 1);
        if (src.HasFateStrategy(397))
        {
            src.ModifyBuffValue(BuffType.XingLi, 1);
            if (src.LastRoundExp >= opp.LastRoundExp + cfg.FateOtherParams(397).At(0))
                src.ModifyBuffValue(BuffType.XingLi, 1);
        }
        if (src.HasFateStrategy(402))
            src.ModifyBuffValue(BuffType.XingYuan, cfg.FateOtherParams(402).At(0));
        // 仙命 404：开战升级一张「卦」牌。
        if (src.HasFateStrategy(404))
            UpgradeFirst(src, id => cfg.CardName(id).Contains("卦"));
        if (src.HasFateStrategy(407))
            src.ModifyBuffValue(BuffType.WuYouLingNiang, cfg.FateOtherParams(407).At(1));
        if (src.HasFateStrategy(409))
        {
            // 原码 GetWuXingCountInDeck()：卡组里五行种类数。早先读一个恒为 0 的占位字段，仙命 409 从没生效（b6_185 / 186 逐步对拍开局即分叉）。
            int n = cfg.FateOtherParams(409).At(0) * WuXingFunctions.GetWuXingCountInDeck(src);
            src.ModifyMaxHp(n);
            src.ModifyHp(n);
        }
        if (src.HasFateStrategy(412)) src.ModifyBuffValue(BuffType.JiHuoMuLing, 1);
        if (src.HasFateStrategy(416))
            src.ModifyBuffValue(BuffType.CuJuFeiXi, cfg.FateOtherParams(416).At(0));
        if (src.HasFateStrategy(423))
            src.ModifyBuffValue(BuffType.XiaCiQiShiDuoJia, cfg.FateOtherParams(423).At(0));
        if (src.HasFateStrategy(428))
            src.ModifyBuffValue(BuffType.QiShiShangXian, cfg.FateOtherParams(428).At(0));
        if (src.HasFateStrategy(430)) src.ModifyBuffValue(BuffType.JieQuanShi, 1);
        if (src.HasFateStrategy(436))
            src.ModifyBuffValue(BuffType.QiXingJieMing, cfg.FateOtherParams(436).At(0));
        if (src.HasFateStrategy(437) && IsSwitchActive(src, 437))
        {
            src.ModifyBuffValue(BuffType.JiaGong, cfg.FateOtherParams(437).At(0));
            src.ModifyBuffValue(BuffType.NeiShang, cfg.FateOtherParams(437).At(1));
        }
    }

    /// <summary>
    /// 仙命的「每次出牌」钩子：忠实移植 <c>FateStrategyFunctions.OnPlayCard</c>（原码 190459 状态机），
    /// 由 <see cref="CardHookFunctions.OnBeforeExecuted"/> 在「相邻前效」之后调用（原码 22369）。
    /// ⚠ 早先 sim **整段缺失** —— 下面二十来条仙命的出牌效果全部静默失效（剑阵护体 JianZhenHuTi、七星连珠
    ///   QiXingLianZhu 两个 buff 也因此只写不读）。
    /// ⚠ 原码判 `cardItem.cardInfo.id == 19`（整 id）与 `GetBaseCardId(id) == 0` 两种写法并存，照抄不合并。
    /// </summary>
    public static void OnPlayCard(Combatant src, int cardId, int grid)
    {
        var cfg = src.Config;
        var foe = src.Opponent;
        int baseId = CardTypes.BaseId(cardId);
        string name = cfg.CardName(cardId);

        if (src.HasFateStrategy(97) && CardTypes.IsYunJian(src, cardId)) src.ModifyBuffValue(BuffType.YunHai, 1);
        if (src.HasFateStrategy(325) && CardTypes.IsKuangJian(src, cardId)) src.ModifyAnima(1);
        if (src.HasFateStrategy(100) && cardId == 19) src.ModifyAnima(cfg.FateOtherParams(100).At(0));
        if (src.HasFateStrategy(101) && cardId == 19) src.ModifyBuffValue(BuffType.JianQi, cfg.FateOtherParams(101).At(0));
        if (src.HasFateStrategy(102) && cardId == 19) src.RemoveAllDebuff(cfg.FateOtherParams(102).At(0));
        if (src.HasFateStrategy(103) && cardId == 19) src.ModifyBuffValue(BuffType.ShuiYueJianZhen, cfg.FateOtherParams(103).At(0));
        if (src.HasFateStrategy(324) && cardId == 19)
        {
            src.ModifyBuffValue(BuffType.YunHai, cfg.FateOtherParams(324).At(0));
            src.ModifyBuffValue(BuffType.LianYun, 1);
        }
        // 仙命 121：第 8 格（下标 7）出牌给对手挂内伤。
        if (src.HasFateStrategy(121) && grid == 7) foe.ModifyBuffValue(BuffType.NeiShang, cfg.FateOtherParams(121).At(0));
        // 仙命 153：耗生命的牌按耗量的 o[0]% 对对手造成伤害。
        if (src.HasFateStrategy(153))
        {
            int hc = cfg.CardHpCost(cardId);
            if (hc > 0)
            {
                int dmg = hc * cfg.FateOtherParams(153).At(0) / 100;
                if (dmg > 0) CombatMath.ApplyDamage(src, foe, DamageInfo.Create(src, DamageType.Damage, dmg, skipWoundCheck: true));
            }
        }
        if (src.HasFateStrategy(32) && baseId == 0)
        {
            foe.ModifyBuffValue(BuffType.JiaGong, -cfg.FateOtherParams(32).At(0));
            foe.ModifyBuffValue(BuffType.HuTi, -cfg.FateOtherParams(32).At(0));
        }
        if (src.HasFateStrategy(33) && baseId == 0 && !src.HadUsed(grid))
            src.ModifyBuffValue(BuffType.HuTi, cfg.FateOtherParams(33).At(0));
        if (src.HasFateStrategy(36) && baseId == 0) src.ModifyBuffValue(BuffType.JiaGong, cfg.FateOtherParams(36).At(0));
        // 剑阵护体（仙命 109 施加）：使用剑阵牌时扣一层，加防 o[1]、护体 o[2]（取仙命 109 的配置）。
        if (src.HasBuff(BuffType.JianZhenHuTi) && CardTypes.IsJianZhen(src, cardId))
        {
            src.ModifyBuffValue(BuffType.JianZhenHuTi, -1);
            src.ModifyDef(cfg.FateOtherParams(109).At(1));
            src.ModifyBuffValue(BuffType.HuTi, cfg.FateOtherParams(109).At(2));
        }
        if (src.HasFateStrategy(128) && name.Contains("金灵")) src.ModifyAnima(1);
        if (src.HasFateStrategy(128) && name.Contains("水灵")) src.ModifyBuffValue(BuffType.FengRui, 1);
        if (src.HasFateStrategy(320) && CardTypes.IsYunJian(src, cardId)) src.ModifyBuffValue(BuffType.ShuiYueJianZhen, 1);
        if (src.HasFateStrategy(330) && baseId == 22)
        {
            int d = cfg.FateOtherParams(330).At(0);
            src.ModifyBuffValue(BuffType.HuTi, d);
            src.ModifyBuffValue(BuffType.TempHuTi, d);
        }
        // 七星连珠（仙命 331 施加）：使用星弈牌时扣一层，后一格成为星位。
        if (src.HasBuff(BuffType.QiXingLianZhu) && CardTypes.IsXingYi(src, cardId))
        {
            src.ModifyBuffValue(BuffType.QiXingLianZhu, -1);
            GridMarkFunctions.AddXingWei(src, GridFunctions.GetNextGrid(src, grid));
        }
        if (src.HasFateStrategy(337) && baseId == 381) src.ModifyAnima(1);
        if (src.HasFateStrategy(345) && baseId == 7000069)
        {
            int n = src.Anima * cfg.FateOtherParams(345).At(0);
            if (n > 0)
            {
                foe.ModifyHp(-n);
                foe.ModifyMaxHp(-n);
            }
        }
        if (src.HasFateStrategy(380) && CardTypes.IsJianZhen(src, cardId)) src.ModifyBuffValue(BuffType.YunHai, 1);
        if (src.HasFateStrategy(427) && name.Contains("掌")) src.ModifyBuffValue(BuffType.ShenFa, cfg.FateOtherParams(427).At(0));
    }
}
