using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>仙魔策略的开战与出牌效果；参数来自本机配置，开关与附魔位置来自本局私有数据。</summary>
public static class XianMoStrategyFunctions
{
    public static int P(Combatant c, int id, int index = 0) => c.Config.XianMoOtherParams(id).At(index);
    public static bool Active(Combatant c, int id) => c.HasXianMoStrategy(id) && c.XianMoSwitchActive(id);
    public static bool Effective(Combatant c, int id) => c.HasXianMoStrategy(id) && c.CurrentRound >= c.Config.XianMoEffectRound(id);
    private static void Buff(Combatant c, int id, BuffType buff, int index = 0, bool literal = false)
    {
        if (c.HasXianMoStrategy(id)) c.ModifyBuffValue(buff, literal ? 1 : P(c, id, index));
    }
    private static void Grow(Combatant c, int value) { c.ModifyMaxHp(value); c.ModifyHp(value); }
    private static void UpgradeFirst(Combatant c, Func<int, bool> predicate)
    {
        for (int i = 0; i < c.Board.Count; i++)
            if (predicate(c.Board[i].Id) && c.Config.CanUpgrade(c.Board[i].Id))
            { c.ReplaceCard(i, c.Config.BuildCard(c.Board[i].Id + 10000)); break; }
    }
    public static void OnBattleStart(Combatant c)
    {
        var d = c.Opponent;
        // 游戏的顺序：双方共享开战效果 → 龙族 → 普攻 → 各角色策略 → 高阶策略。
        if (Active(c, 1001)) { c.ModifyAnima(P(c, 1001)); d.ModifyAnima(P(c, 1001)); }
        if (Active(c, 1002)) { c.ModifyBuffValue(BuffType.JiaGong, P(c, 1002)); d.ModifyBuffValue(BuffType.JiaGong, P(c, 1002)); }
        if (Active(c, 1003))
        {
            int a = c.Hp * P(c, 1003) / 100, b = d.Hp * P(c, 1003) / 100;
            Grow(c, a); Grow(d, b);
        }
        if (Active(c, 1004)) { c.ModifyBuffValue(BuffType.NeiShang, P(c, 1004)); d.ModifyBuffValue(BuffType.NeiShang, P(c, 1004)); }
        if (Active(c, 1005)) { c.ModifyBuffValue(BuffType.BenHuiHeWuFaZaiCiXingDong, P(c, 1005)); d.ModifyBuffValue(BuffType.BenHuiHeWuFaZaiCiXingDong, P(c, 1005)); }
        if (Active(c, 1006))
        {
            int a = c.Hp * P(c, 1006) / 100, b = d.Hp * P(c, 1006) / 100;
            c.ModifyHp(-a); c.ModifyMaxHp(-a); d.ModifyHp(-b); d.ModifyMaxHp(-b);
        }
        if (Active(c, 1007)) { c.ModifyBuffValue(BuffType.HuaLongDianJing, P(c, 1007)); d.ModifyBuffValue(BuffType.HuaLongDianJing, P(c, 1007)); }
        if (Active(c, 1008)) { c.ModifyBuffValue(BuffType.WanMoShiXinQu, P(c, 1008)); d.ModifyBuffValue(BuffType.WanMoShiXinQu, P(c, 1008)); }
        Buff(c, 3001, BuffType.LongWeiZhenShe);
        Buff(c, 3002, BuffType.HuTi);
        Buff(c, 3004, BuffType.LongYingQianDun);
        Buff(c, 3011, BuffType.XiaCiJiaLingQiDuoJia);
        Buff(c, 5004, BuffType.PuGongJiaShenFa, literal: true);
        Buff(c, 5005, BuffType.PuGongFuZhiShangYiZhangPai);
        Buff(c, 5007, BuffType.XiaCiPuGongHuTiXiaoHao);
        Buff(c, 5008, BuffType.XiaCiPuGongJiaGongXiaoHao);
        Buff(c, 7005, BuffType.LianYun); Buff(c, 7005, BuffType.YunHai, 1);
        Buff(c, 7017, BuffType.JianZhenHuTi);
        if (c.HasXianMoStrategy(7021) && c.Board.Count >= 8)
        {
            int id = c.Board[7].Id;
            if (CardTypes.BaseId(id) == 11 && c.Config.CanUpgrade(id)) c.ReplaceCard(7, c.Config.BuildCard(id + 10000));
            else if (CardTypes.BaseId(id) == 0) c.ReplaceCard(7, c.Config.BuildCard(11));
        }
        if (c.HasXianMoStrategy(7024)) c.ModifyBuffValue(BuffType.GuaXiang, c.GetLastRoundPermanentBuffValue(BuffType.BaoCunGuaXiang));
        if (c.HasXianMoStrategy(7028))
        {
            int[] talents = [10109, 20109, 30109, 40109, 50109];
            BuffType[] elements = [BuffType.JiHuoShuiLing, BuffType.JiHuoMuLing, BuffType.JiHuoHuoLing, BuffType.JiHuoTuLing, BuffType.JiHuoJinLing];
            for (int i = 0; i < talents.Length; i++) if (c.HasTalent(talents[i]))
            { c.ModifyBuffValue(elements[i], 1); c.ModifyBuffValue(elements[(i + 1) % 5], 1); }
        }
        if (c.HasXianMoStrategy(7098)) { c.ModifyBuffValue(BuffType.JiHuoHuoLing, 1); d.ModifyHp(-P(c, 7098)); d.ModifyMaxHp(-P(c, 7098)); }
        if (c.HasXianMoStrategy(7066)) { c.ModifyBuffValue(BuffType.JiHuoMuLing, 1); c.ModifyAnima(P(c, 7066)); }
        if (c.HasXianMoStrategy(7095)) { c.ModifyBuffValue(BuffType.JiHuoTuLing, 1); c.ModifyDef(P(c, 7095)); }
        if (c.HasXianMoStrategy(7096)) { c.ModifyBuffValue(BuffType.JiHuoJinLing, 1); c.ModifyBuffValue(BuffType.FengRui, P(c, 7096)); }
        if (Active(c, 7031)) c.ModifyBuffValue(BuffType.LingZhenHuiXiang, 1);
        if (c.HasXianMoStrategy(7036)) { if (c.XianMoSwitchActive(7036)) c.ModifyBuffValue(BuffType.BiXie, P(c, 7036)); c.ModifyHp(P(c, 7036, 1)); }
        Buff(c, 7053, BuffType.YanQi, literal: true);
        if (c.HasXianMoStrategy(7054)) UpgradeFirst(c, id => c.Config.CardName(id).Contains('雷') || c.Config.CardDesc(id).Contains("再次行动"));
        Buff(c, 7056, BuffType.QiXingLianZhu);
        if (c.HasXianMoStrategy(7058)) UpgradeFirst(c, id => c.Config.CardName(id).Contains("木灵"));
        Buff(c, 7059, BuffType.FengLingZhanYi);
        if (c.HasXianMoStrategy(7063)) d.ModifyBuffValue(BuffType.DengYanCuiXin, P(c, 7063));
        Buff(c, 7064, BuffType.ZhuQueZhiLei, literal: true);
        Buff(c, 7067, BuffType.JianQi);
        if (c.HasXianMoStrategy(7069))
        {
            int count = c.LastRoundHandCards.Count(id => CardTypes.IsYunJian(c, id)) + c.Board.Count(card => CardTypes.IsYunJian(c, card.Id));
            c.ModifyAnima(Math.Min(count / 2, P(c, 7069)));
        }
        Buff(c, 7077, BuffType.WuJingGuaYan);
        if (c.HasXianMoStrategy(7078))
        {
            c.ModifyBuffValue(BuffType.XingLi, 1);
            if (c.LastRoundExp >= d.LastRoundExp + P(c, 7078)) c.ModifyBuffValue(BuffType.XingLi, 1);
        }
        Buff(c, 7079, BuffType.XingYuan);
        if (c.HasXianMoStrategy(7083)) Grow(c, P(c, 7083) * WuXingFunctions.GetWuXingCountInDeck(c));
        Buff(c, 7087, BuffType.XiaCiQiShiDuoJia);
        Buff(c, 7091, BuffType.TianYanFengLingDuanQu);
        Buff(c, 7093, BuffType.QiXingJieMing);
        if (Active(c, 7094)) { c.ModifyBuffValue(BuffType.JiaGong, P(c, 7094)); c.ModifyBuffValue(BuffType.NeiShang, P(c, 7094, 1)); }
        Buff(c, 7099, BuffType.CuJuFeiXi);
        if (c.HasXianMoStrategy(7100)) UpgradeFirst(c, id => c.Config.CardName(id).Contains("火灵"));
        if (c.HasXianMoStrategy(7103) && P(c, 7103) > 0) Grow(c, c.Hp / P(c, 7103));
        Buff(c, 7104, BuffType.JieQuanShi, literal: true);
        Buff(c, 7105, BuffType.Min);
        Buff(c, 7109, BuffType.WuYouLingNiang, 1);
        Buff(c, 23066, BuffType.JiHuoMuLing, literal: true);
        if (c.HasXianMoStrategy(7112)) c.ModifyMaxHp(P(c, 7112));
        Buff(c, 7120, BuffType.QiShiShangXian);
        if (c.HasXianMoStrategy(7122)) UpgradeFirst(c, id => c.Config.CardName(id).Contains("水灵"));
        if (c.HasXianMoStrategy(7123) && c.Life <= P(c, 7123)) Grow(c, c.Hp * P(c, 7123, 1) / 100);
        Buff(c, 7127, BuffType.KeYinJingLei, literal: true);
        if (c.HasXianMoStrategy(7128)) UpgradeFirst(c, id => c.Config.CardName(id).Contains("星弈") || c.Config.CardName(id).Contains('卦'));
        if (c.HasXianMoStrategy(7129)) UpgradeFirst(c, id => c.Config.CardDesc(id).Contains("灵气") || c.Config.CardDesc(id).Contains("卦象"));
        if (Active(c, 7135)) { c.ModifyBuffValue(BuffType.GongMingMingXinRuXuan, 1); d.ModifyBuffValue(BuffType.GongMingMingXinRuXuan, 1); }
        Buff(c, 7136, BuffType.KuangJian, literal: true);
        if (c.HasXianMoStrategy(11002)) { c.ModifyBuffValue(BuffType.GuaXiang, 1); for (int g = 2; g < 8; g++) GridMarkFunctions.AddXingWei(c, g); }
        if (c.HasXianMoStrategy(11004)) c.ModifyTiPo(P(c, 11004, 2));
        if (c.HasXianMoStrategy(15018) && c.LastRoundHandCardCount > 0) Grow(c, c.Hp * P(c, 15018) / 100);
        Buff(c, 26001, BuffType.HunDunQianYingHuiHeJiShu, 1);
    }

    public static void OnPlayCard(Combatant c, int id, int grid, bool temporary)
    {
        var d = c.Opponent;
        string name = c.Config.CardName(id);
        bool normal = CardTypes.BaseId(id) is 0 or 286;
        if (normal)
        {
            if (c.HasBuff(BuffType.PuGongJiaShenFa)) c.ModifyBuffValue(BuffType.ShenFa, 10);
            foreach (var pair in new[] { (BuffType.XiaCiPuGongHuTiXiaoHao, BuffType.HuTi), (BuffType.XiaCiPuGongJiaGongXiaoHao, BuffType.JiaGong) })
                if (c.HasBuff(pair.Item1)) { c.ModifyBuffValue(pair.Item2, c.GetBuffValue(pair.Item1)); c.RemoveBuff(pair.Item1); c.CurrentCardSkip = true; }
        }
        if (c.HasXianMoStrategy(7010) && CardTypes.IsYunJian(c, id)) c.ModifyBuffValue(BuffType.YunHai, 1);
        // 游戏用同一个 buff，仙命钩子和仙魔钩子都会消耗它，必须保留两次独立检查。
        if (c.HasBuff(BuffType.JianZhenHuTi) && CardTypes.IsJianZhen(c, id))
        { c.ModifyBuffValue(BuffType.JianZhenHuTi, -1); c.ModifyDef(P(c, 7017, 1)); c.ModifyBuffValue(BuffType.HuTi, P(c, 7017, 2)); }
        if (c.HasXianMoStrategy(7022) && grid == 7) d.ModifyBuffValue(BuffType.NeiShang, P(c, 7022));
        if (c.HasXianMoStrategy(7026) && name.Contains("金灵")) c.ModifyAnima(1);
        if (c.HasXianMoStrategy(7026) && name.Contains("水灵")) c.ModifyBuffValue(BuffType.FengRui, 1);
        if (c.HasXianMoStrategy(7052) && CardTypes.IsKuangJian(c, id)) c.ModifyAnima(1);
        if (c.HasXianMoStrategy(7037)) Damage(c, c.Config.CardHpCost(id) * P(c, 7037) / 100);
        if (c.HasXianMoStrategy(7050) && CardTypes.IsYunJian(c, id)) c.ModifyBuffValue(BuffType.ShuiYueJianZhen, 1);
        if (c.HasBuff(BuffType.QiXingLianZhu) && CardTypes.IsXingYi(c, id))
        { c.ModifyBuffValue(BuffType.QiXingLianZhu, -1); GridMarkFunctions.AddXingWei(c, GridFunctions.GetNextGrid(c, grid)); }
        if (c.HasXianMoStrategy(7060) && CardTypes.BaseId(id) == 381) c.ModifyAnima(1);
        if (c.HasXianMoStrategy(7089) && name.Contains('掌')) c.ModifyBuffValue(BuffType.ShenFa, P(c, 7089));
        if (c.HasXianMoStrategy(7097) && name.Contains("土灵")) c.ModifyBuffValue(BuffType.FengRui, P(c, 7097));
        if (c.HasXianMoStrategy(7130) && id == 19) { c.ModifyBuffValue(BuffType.YunHai, P(c, 7130)); c.ModifyBuffValue(BuffType.LianYun, P(c, 7130, 1)); }
        if (c.HasXianMoStrategy(7132) && CardTypes.IsJianZhen(c, id)) c.ModifyBuffValue(BuffType.YunHai, 1);
        if (!temporary && c.HasXianMoStrategy(5001) && !c.Config.CardHidden(id) && c.Config.CardLevel(id) == 1 && grid == c.XianMoGrid(5001)) c.ModifyBuffValue(BuffType.ExActionAgain, 1);
        if (!temporary && !c.HadUsed(grid))
            for (int strategy = 13001; strategy <= 13019; strategy++)
                if (c.HasXianMoStrategy(strategy) && grid == c.XianMoGrid(strategy)) Enchant(c, strategy, name, grid);
        if (c.HasXianMoStrategy(23045) && id == 19) c.ModifyAnima(P(c, 23045));
        if (Effective(c, 23079) && CardTypes.BaseId(id) == 22)
        { c.ModifyBuffValue(BuffType.HuTi, P(c, 23079)); c.ModifyBuffValue(BuffType.TempHuTi, P(c, 23079)); }
    }
    private static void Enchant(Combatant c, int id, string name, int grid)
    {
        var d = c.Opponent;
        switch (id)
        {
            case 13001: c.ModifyBuffValue(BuffType.XiaCiGongJiShiJiaNeiShang, P(c, id)); break;
            case 13002:
                int taken = 0, count = P(c, id, 1);
                for (int n = 0; n < count; n++)
                { int value = c.NextParam(ParamRequest.OwnDebuff(id)); if (value != -1) { c.ModifyBuffValue((BuffType)value, -1); taken++; } }
                if (taken < count) c.ModifyHp((count - taken) * P(c, id));
                break;
            // 13003 是执行器里的首次升级；13006 是出牌后的攻击。
            case 13004: int hp = c.GetBuffValue(BuffType.LoseHpCount) * P(c, id) / 100; if (hp > 0) Grow(c, hp); break;
            case 13005: c.ModifyDef(P(c, id) * WuXingFunctions.GetWuXingCountInDeck(c)); break;
            case 13007: d.ModifyBuffValue(BuffType.XiaCiPaiJiangJi, 1); break;
            case 13008: c.ModifyBuffValue(BuffType.HuTi, P(c, id)); c.ModifyBuffValue(BuffType.TempHuTi, P(c, id)); break;
            case 13009: c.ModifyDef(P(c, id)); c.ModifyBuffValue(BuffType.ShuiYueJianZhen, P(c, id, 1)); break;
            case 13010:
                for (int n = 0; n < P(c, id); n++) { grid = GridFunctions.GetNextGrid(c, grid); GridMarkFunctions.AddXingWei(c, grid); }
                break;
            case 13011:
                if (name.Contains("金灵")) c.ModifyBuffValue(BuffType.FengRui, P(c, id));
                if (name.Contains("水灵") || name.Contains("木灵")) c.ModifyAnima(P(c, id, 1));
                if (name.Contains("火灵") || name.Contains("土灵")) c.ModifyDef(P(c, id, 2));
                break;
            case 13012: c.ModifyBuffValue(BuffType.QiRuoXuanHe, P(c, id)); break;
            case 13013: c.ModifyBuffValue(BuffType.GuaXiang, P(c, id)); Grow(c, c.GetBuffValue(BuffType.GuaXiang) * P(c, id, 1)); break;
            case 13014: c.ModifyTiPo(P(c, id)); if (P(c, id, 1) > 0) c.ModifyDef(c.GetBuffValue(BuffType.TiPo) / P(c, id, 1)); break;
            case 13015: d.ModifyHp(-P(c, id)); d.ModifyMaxHp(-P(c, id)); d.ModifyBuffValue(BuffType.ShiYongXiaZhangPaiHouXiaoHao, 1); break;
            case 13016: c.ModifyBuffValue(BuffType.JianYi, P(c, id)); if (P(c, id, 1) > 0) Damage(c, c.GetBuffValue(BuffType.JianYi) / P(c, id, 1)); break;
            case 13017: d.ModifyBuffValue(BuffType.NeiShang, P(c, id)); c.ModifyDef(d.GetDebuffCount()); break;
            case 13018:
                if (name.Contains("水灵")) c.ModifyBuffValue(BuffType.BoLan, 1);
                if (name.Contains("木灵") && c.GetBuffValue(BuffType.JiaGong) > 0) { c.ModifyBuffValue(BuffType.JiaGong, -1); c.ModifyBuffValue(BuffType.MuCi, 1); }
                break;
            case 13019: c.ModifyHp(c.GetDebuffCount() + P(c, id)); break;
        }
    }
    private static void Damage(Combatant c, int value)
    { if (value > 0) CombatMath.ApplyDamage(c, c.Opponent, DamageInfo.Create(c, DamageType.Damage, value, skipWoundCheck: true)); }
}
