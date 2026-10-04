using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 出牌前的两个钩子（批次 D 欠的「三个相邻钩子」里的两个），忠实移植自
/// <c>CardActionBase.CheckAdjacentEffectsBeforeCard</c>（d__16）与 <c>OnBeforeExecuted</c>（d__17，946 行状态机
/// / 458 行有效逻辑）。出牌顺序：<see cref="CheckAdjacentEffectsBeforeCard"/> → <see cref="OnBeforeExecuted"/> → 卡效果 → 攻击。
///
/// 覆盖：相邻格特殊牌触发的五行激活/星力/必顶世上击、每张牌的**天赋循环**（57/60/62/69/102·149/246/247/250…）、
/// 刻印·共鸣的一次性加成、下张牌类 buff 的兑现（再行动/狂剑/崩拳…）、**五行阵系列**（金→锋锐 木→回血[刻印79 吸血]
/// 土→加防 火→打血 水→加灵气）、崩拳系列十余种、云剑系列、浑元逆克阵等。
///
/// ⚠ 已知未移植（都在条件里用到未抽的卡表字段或在别处，已就地标注）：
///   · 卡表 `subcategory`（天宗/仙宗）、`level`（化神）、`hpCost`、`desc`、`cardType`
///   · `IsLianBeng`（连崩）、`IsHouZhao`/`CheckXingWei`（后招/星位）
///   · `cardItem.skip = true`（跳牌：影响牌组是否前进）
/// </summary>
public static class CardHookFunctions
{
    private static string CardNameAt(Combatant src, BattleCard? card)
        => card is null ? "" : (card.Name ?? src.Config.CardName(card.Id));

    /// <summary>
    /// 原码 <c>CardActionBase.CheckAdjacentEffects</c>（d__15，主循环 15592 在付费之前调用）：
    /// 上一格是 10000080 系（0/1/2 档）或左右相邻是 10030080 / 10040080 → 本张牌算作崩拳。
    /// </summary>
    public static void CheckAdjacentEffects(Combatant src)
    {
        int grid = src.CurrentCardGrid;
        if (grid < 0) return;
        int prevId = GridFunctions.PreviousGridCard(src, grid)?.Id ?? 0;
        int nextId = GridFunctions.NextGridCard(src, grid)?.Id ?? 0;
        if (prevId == 10000080 || prevId == 10010080 || prevId == 10020080)
            src.ModifyBuffValue(BuffType.XiaZhangPaiSuanBengQuan, 1);
        foreach (int id in new[] { prevId, nextId })
            if (id == 10030080 || id == 10040080)
                src.ModifyBuffValue(BuffType.XiaZhangPaiSuanBengQuan, 1);
    }

    /// <summary>CheckAdjacentEffectsBeforeCard：看左右相邻格的牌是不是特殊牌，是则触发对应效果。</summary>
    public static void CheckAdjacentEffectsBeforeCard(Combatant src)
    {
        int grid = src.CurrentCardGrid;
        if (grid < 0) return;
        int prevId = GridFunctions.PreviousGridCard(src, grid)?.Id ?? 0;
        int nextId = GridFunctions.NextGridCard(src, grid)?.Id ?? 0;
        string cardName = src.CurrentCardName;

        foreach (int id in new[] { prevId, nextId })
        {
            // 五行聚灵牌 → 按本牌牌名激活五行。
            if (id == 7000075 || id == 7010075 || id == 7020075 || id == 7030075 || id == 7040075)
                WuXingFunctions.ActiveWuXingInName(src, cardName);
            // 星力/必顶世上击。
            if (id == 4030079 || id == 4040079)
            {
                src.ModifyBuffValue(BuffType.XingLi, 1);
                if (id == 4040079 && cardName.Contains("星弈")) src.ModifyBuffValue(BuffType.XingLi, 1);
            }
            if (id == 1020067 || id == 1030067 || id == 1040067)
                src.ModifyBuffValue(BuffType.BiDingShiZuoJiShang, 1);
        }
        // 上一格是崩拳牌 → 本张算崩拳；左右有崩拳牌 → 同理。
        if (prevId == 10000080 || prevId == 10010080 || prevId == 10020080)
            src.ModifyBuffValue(BuffType.XiaZhangPaiSuanBengQuan, 1);
        foreach (int id in new[] { prevId, nextId })
            if (id == 10030080 || id == 10040080)
                src.ModifyBuffValue(BuffType.XiaZhangPaiSuanBengQuan, 1);
    }

    /// <summary>
    /// OnBeforeExecuted：每次执行牌效果之前的一长串钩子（原码 <c>__CG_OnBeforeExecuted_d__17</c>，21855）。
    /// ⚠ **顺序照原码 goto 链**（DFS 导出，= IL 偏移序）：层数记账 → 相邻前效 → 仙命出牌钩子 → 404 / 云剑 / … / 崩拳块
    ///   → 下张激活五行 / 天赋200 / 五行灵阵 → 奇策在后 → 幻琴拙剑 / 梦狂舞曲 / 梦天髓 → 天赋循环 → 刻印161 / 一串共鸣
    ///   / 下张再行动 / 汲然咒印 / 使用下张牌后消耗 / [耗尽] / 上回合永久 buff / 混元逆克阵 → 共鸣36 → 星元
    ///   → 刻印出牌前钩子 → 清实际伤害 / 受伤计数。早先按文件序散搬，五行灵阵与天赋循环等整体错位。
    /// </summary>
    public static void OnBeforeExecuted(Combatant src, Combatant dst)
    {
        int grid = src.CurrentCardGrid;
        var cfg = src.Config;
        int cardId = src.CurrentCardId;
        int baseId = src.CurrentCardBaseId;
        string cardName = src.CurrentCardName;
        bool isTempCard = CardEffects.TriggerDepth > 0;

        // ── default：万能剑 / 下张牌算作狂剑 层数记账 → 相邻前效 → 仙命出牌钩子 ──
        if (src.HasBuff(BuffType.WanNengJian)) src.ModifyBuffValue(BuffType.WanNengJianShengXiaoCengJi, 1);
        if (src.HasBuff(BuffType.XiaZhangPaiSuanZuoKuangJian))
            src.ModifyBuffValue(BuffType.XiaZhangPaiSuanZuoKuangJianCengJi, 1);
        CheckAdjacentEffectsBeforeCard(src);
        FateStrategyFunctions.OnPlayCard(src, cardId, grid);

        // ── IL_016c ──
        if (src.Subs.HasCardInDeck(src, 404)) src.ModifyDef(src.GetBuffValue(BuffType.JiaGong));
        if (CardTypes.IsYunJian(src, cardId))
        {
            if (src.HasBuff(BuffType.YunJianRouXin)) src.ModifyHp(src.GetBuffValue(BuffType.YunJianRouXin));
            if (src.HasBuff(BuffType.LingMaoTaYun)) src.ModifyAnima(src.GetBuffValue(BuffType.LingMaoTaYun));
            if (src.HasTalent(15)) src.ModifyAnima(1);
            if (src.HasBuff(BuffType.KeYinTaLingYun))
            {
                src.ModifyBuffValue(BuffType.KeYinTaLingYun, -1);
                src.ModifyAnima(1);
            }
            if (src.HasBuff(BuffType.YunJianZhiXin))
            {
                src.ModifyBuffValue(BuffType.YunJianZhiXin, -1);
                src.ModifyBuffValue(BuffType.ExActionAgain, 1);
            }
            if (src.IsTalentResonanceEffective(64)) src.ModifyBuffValue(BuffType.ShuiYueJianZhen, 1);
        }
        if (src.HasBuff(BuffType.ShiYongJianZhenJiaYunHai) && CardTypes.IsJianZhen(src, cardId))
            src.ModifyBuffValue(BuffType.YunHai, src.GetBuffValue(BuffType.ShiYongJianZhenJiaYunHai));
        // 梦•万法：剑牌加剑气（原码 IsJian —— 含「19 号 + 天赋 30096 不算」的例外）。
        if (src.HasBuff(BuffType.MengWanFa) && CardTypes.IsJian(src, cardId))
            src.ModifyBuffValue(BuffType.JianQi, src.GetBuffValue(BuffType.MengWanFa));
        // 下张狂剑再行动（五个条件：本牌本就会再行动 / 本回合再行动次数已满时不消耗）。
        if (src.HasBuff(BuffType.XiaZhangKuangJianZaiCiXingDong) && CardTypes.IsKuangJian(src, cardId)
            && src.GetBuffValue(BuffType.ExActionAgain) <= 0 && !src.CurrentCardActionAgain
            && src.GetBuffValue(BuffType.ZaiCiXingDong) < src.ActionAgainPerRound)
        {
            src.ModifyBuffValue(BuffType.XiaZhangKuangJianZaiCiXingDong, -1);
            src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        }
        // 剑水宗·天 / 仙：**必须带 subcategory 门槛**（早先门槛丢了 → 每张牌都回血，卡 89 误差来源）。
        int subForZong = (src.Config as Config.JsonBattleConfig)?.Card(cardId)?.Subcategory ?? 0;
        if (src.HasBuff(BuffType.JianShuiZongTian) && subForZong == SubcategoryTianZong)
            src.ModifyAnima(src.GetBuffValue(BuffType.JianShuiZongTian));
        if (src.HasBuff(BuffType.JianShuiZongXian) && subForZong == SubcategoryXianZong)
            src.ModifyHp(src.GetBuffValue(BuffType.JianShuiZongXian));
        // 崩灭心法（10000044）：非临时牌、本牌耗生命 → 加气势。
        if (src.HasBuff(BuffType.BengMeiXinFa) && !isTempCard && cfg.CardHpCost(cardId) > 0)
            src.ModifyBuffValue(BuffType.QiShi, src.GetBuffValue(BuffType.BengMeiXinFa));
        if (src.HasBuff(BuffType.XiaZhangBengQuanJiaTiPo))
        {
            src.ModifyTiPo(src.GetBuffValue(BuffType.XiaZhangBengQuanJiaTiPo));
            src.RemoveBuff(BuffType.XiaZhangBengQuanJiaTiPo);
        }
        // ── 崩拳块（只对崩拳牌；连崩牌不消耗这些「下张崩拳」层）──
        if (CardTypes.IsBengQuan(src, cardId))
        {
            bool isLianBeng = CardTypes.IsLianBeng(cardId);
            if (src.IsTalentResonanceEffective(79)) src.ModifyHp(cfg.ResonanceOtherParams(79).At(0), canRevive: true);
            if (src.HasBuff(BuffType.BengQuanFeng))
            {
                src.ModifyDef(src.GetBuffValue(BuffType.BengQuanFeng));
                if (!isLianBeng) src.RemoveBuff(BuffType.BengQuanFeng);
            }
            if (src.HasBuff(BuffType.BengQuanHan))
            {
                src.ModifyBuffValue(BuffType.QiShi, src.GetBuffValue(BuffType.BengQuanHan));
                if (!isLianBeng) src.RemoveBuff(BuffType.BengQuanHan);
            }
            if (src.HasBuff(BuffType.BengQuanChan))
            {
                dst.ModifyBuffValue(BuffType.WaiShang, src.GetBuffValue(BuffType.BengQuanChan));
                if (!isLianBeng) src.RemoveBuff(BuffType.BengQuanChan);
            }
            if (src.HasBuff(BuffType.BengQuanTu))
            {
                src.ModifyBuffValue(BuffType.SuiFang, src.GetBuffValue(BuffType.BengQuanTu));
                if (!isLianBeng) src.ModifyBuffValue(BuffType.BengQuanTu, -1);
            }
            if (src.HasBuff(BuffType.BengQuanJieMai))
            {
                int count = src.GetBuffValue(BuffType.BengQuanJieMai);
                for (int i = 0; i < count; i++)
                {
                    int p = src.NextParam(ParamRequest.OwnDebuff(ParamSite.BengQuanJieMai));
                    if (p != -1)
                    {
                        src.ModifyBuffValue((BuffType)p, -1);
                        dst.ModifyBuffValue((BuffType)p, 1);
                    }
                }
                if (!isLianBeng) src.RemoveBuff(BuffType.BengQuanJieMai);
            }
            // IL_076d：存劲 / 山击 / 惊触 / 缠Plus / 下张崩拳追加攻击 / 梦连崩 / 伏虎 —— 两个入口都在崩拳门内。
            if (src.HasBuff(BuffType.BengQuanCunJin))
            {
                src.SetBuffValue(BuffType.QiShiBeiLv, 2);
                if (!isLianBeng) src.ModifyBuffValue(BuffType.BengQuanCunJin, -1);
            }
            if (src.HasBuff(BuffType.BengQuanShanJi))
            {
                src.ModifyBuffValue(BuffType.ShenFa, src.GetBuffValue(BuffType.BengQuanShanJi));
                if (!isLianBeng) src.RemoveBuff(BuffType.BengQuanShanJi);
            }
            if (src.HasBuff(BuffType.BengQuanJingChu))
            {
                src.ModifyBuffValue(BuffType.ChuFaJingChu, src.GetBuffValue(BuffType.BengQuanJingChu));
                if (!isLianBeng) src.RemoveBuff(BuffType.BengQuanJingChu);
            }
            if (src.HasBuff(BuffType.ChanPlus))
            {
                src.ModifyBuffValue(BuffType.ChanPlusChuFa, src.GetBuffValue(BuffType.ChanPlus));
                if (!isLianBeng) src.RemoveBuff(BuffType.ChanPlus);
            }
            if (src.HasBuff(BuffType.XiaZhangBengQuanZhuiJiaGongJi))
            {
                src.ModifyBuffValue(BuffType.XiaZhangBengQuanZhuiJiaGongJiYiChuFa, src.GetBuffValue(BuffType.XiaZhangBengQuanZhuiJiaGongJi));
                if (!isLianBeng) src.RemoveBuff(BuffType.XiaZhangBengQuanZhuiJiaGongJi);
            }
            if (src.HasBuff(BuffType.MengLianBeng))
            {
                src.ModifyBuffValue(BuffType.MengLianBengChuFa, src.GetBuffValue(BuffType.MengLianBeng));
                if (!isLianBeng) src.RemoveBuff(BuffType.MengLianBeng);
            }
            if (src.HasBuff(BuffType.BengQuanFuHu))
            {
                src.ModifyBuffValue(BuffType.ChuFaFuHu, src.GetBuffValue(BuffType.BengQuanFuHu));
                if (!isLianBeng) src.RemoveBuff(BuffType.BengQuanFuHu);
            }
        }

        // ── IL_0962 下张牌激活五行（按本牌牌名激活，激活了才扣层）/ 天赋200 ──
        if (src.HasBuff(BuffType.XiaZhangPaiJiHuoWuXing))
        {
            if (WuXingFunctions.ActiveWuXingInName(src, cardName))
                src.ModifyBuffValue(BuffType.XiaZhangPaiJiHuoWuXing, -1);
        }
        if (src.HasTalent(200) && !GridFunctions.IsWuXingCard(cardName)) src.ModifyBuffValue(BuffType.JiHuoMuLing, 1);
        // ── 五行灵阵（原码顺序：土 → 水 → 火 → 木 → 木灵疗愈 → 金 → 水木双华）──
        // 阵名对应的元素牌，或（天赋130 / 仙命133 的「相生灵阵」时）该阵**生出的**元素牌。
        bool xiangShengLingZhen = src.HasTalent(130) || src.HasFateStrategy(133);
        bool IsLingZhen(string self, string gen)
            => cardName.Contains(self) || (xiangShengLingZhen && cardName.Contains(gen));
        if (src.HasBuff(BuffType.TuLingZhen) && IsLingZhen("土灵", "金灵"))
            src.ModifyDef(src.GetBuffValue(BuffType.TuLingZhen));
        if (src.HasBuff(BuffType.ShuiLingZhen) && IsLingZhen("水灵", "木灵"))
            src.ModifyAnima(src.GetBuffValue(BuffType.ShuiLingZhen));
        if (src.HasBuff(BuffType.HuoLingZhen) && IsLingZhen("火灵", "土灵"))
        {
            int n = src.GetBuffValue(BuffType.HuoLingZhen);
            dst.ModifyHp(-n);
            dst.ModifyMaxHp(-n);
        }
        if (src.HasBuff(BuffType.MuLingZhen) && IsLingZhen("木灵", "火灵"))
        {
            if (src.Subs.HasKeYinType(src, 79))
            {
                int n = src.GetBuffValue(BuffType.MuLingZhen) + src.GetBuffValue(BuffType.JiaGong);
                dst.ModifyHp(-n);
                src.ModifyHp(n);
            }
            else src.ModifyHp(src.GetBuffValue(BuffType.MuLingZhen));
        }
        // 木灵疗愈阵：门是「木灵 或（**只看天赋130**）火灵」—— 不含仙命133（早先误用了相生灵阵的合并门）。
        if (src.HasBuff(BuffType.MuLingLiaoYuZhen)
            && (cardName.Contains("木灵") || (src.HasTalent(130) && cardName.Contains("火灵"))))
            src.ModifyHp(src.GetBuffValue(BuffType.JiaGong) * src.GetBuffValue(BuffType.MuLingLiaoYuZhen));
        if (src.HasBuff(BuffType.JinLingZhen) && IsLingZhen("金灵", "水灵"))
            src.ModifyBuffValue(BuffType.FengRui, src.GetBuffValue(BuffType.JinLingZhen));
        // 水木双华阵：水灵 **或** 木灵（无相生条件；早先误套了相生灵阵的门，木灵牌不触发）。
        if (src.HasBuff(BuffType.ShuiMuShuangHuaZhen) && (cardName.Contains("水灵") || cardName.Contains("木灵")))
            src.ModifyAnima(src.GetBuffValue(BuffType.ShuiMuShuangHuaZhen));

        // ── IL_117e 奇策在后：只对后招牌 ──
        if (src.HasBuff(BuffType.QiCeZaiHou) && CardTypes.IsHouZhao(src, cardId))
        {
            src.ModifyAnima(src.GetBuffValue(BuffType.QiCeZaiHou));
            src.ModifyBuffValue(BuffType.XingLi, src.GetBuffValue(BuffType.QiCeZaiHou));
            src.ModifyBuffValue(BuffType.GuaXiang, src.GetBuffValue(BuffType.QiCeZaiHou));
        }
        // ── IL_126e 幻琴拙剑再行动 / 梦狂舞曲 / 梦天髓·地境界 / 梦天髓 ──
        // 幻琴拙剑：「剑」牌且**不是**云剑 / 狂剑 / 灵剑 / 剑阵（且不是天赋 30096 下的 19 号），外加两道次数门。
        if (src.HasBuff(BuffType.HuanQinZhuoJianZaiCiXingDong) && cardName.Contains("剑")
            && !CardTypes.IsYunJian(src, cardId) && !CardTypes.IsKuangJian(src, cardId)
            && !CardTypes.IsLingJian(src, cardId) && !CardTypes.IsJianZhen(src, cardId)
            && (cardId != 19 || !src.HasTalent(30096))
            && src.GetBuffValue(BuffType.ExActionAgain) <= 0
            && src.GetBuffValue(BuffType.ZaiCiXingDong) < src.ActionAgainPerRound)
        {
            src.ModifyBuffValue(BuffType.HuanQinZhuoJianZaiCiXingDong, -1);
            src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        }
        if (src.HasBuff(BuffType.MengKuangWuQu) && src.GetBuffValue(BuffType.ExActionAgain) <= 0
            && src.GetBuffValue(BuffType.ZaiCiXingDong) < src.ActionAgainPerRound)
        {
            src.ModifyBuffValue(BuffType.MengKuangWuQu, -1);
            src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        }
        if (src.HasBuff(BuffType.MengTianSuiDiJingJie) && (cardName.Contains("灵印") || cardName.Contains("灵阵")))
        {
            src.ModifyBuffValue(BuffType.MengTianSuiDiJingJie, -1);
            src.ModifyBuffValue(BuffType.ShenFa, 10);
        }
        if (src.HasBuff(BuffType.MengTianSui) && (cardName.Contains("灵印") || cardName.Contains("灵阵")))
            src.ModifyBuffValue(BuffType.ShenFa, 10);

        // ── 天赋循环：按 talent%10000 分派 ──
        if (src.Talents is { Count: > 0 } talents)
        {
            foreach (int current in talents)
            {
                var op = cfg.TalentOtherParams(current);
                switch (current % 10000)
                {
                    case 57:   // 首格（非临时牌）加防 + 水月剑阵
                        if (grid == 0 && !isTempCard) { src.ModifyDef(op.At(0)); src.ModifyBuffValue(BuffType.ShuiYueJianZhen, 1); }
                        continue;
                    case 60:   // 首格加上限
                        if (grid == 0) src.ModifyMaxHp(op.At(0));
                        continue;
                    case 62:   // 星弈牌加卦象
                        if (cardName.Contains("星弈")) src.ModifyBuffValue(BuffType.GuaXiang, op.At(0));
                        continue;
                    case 69:   // 云剑加防（共鸣64 +1）
                        if (CardTypes.IsYunJian(src, cardId))
                        {
                            int n = op.At(0);
                            if (src.HasTalentResonance(64)) n++;
                            src.ModifyDef(n);
                        }
                        continue;
                    case 101:  // 与上一张用掉的五行相生 → 灵气 o[0]、生命及上限 o[1]；随后水灵牌 20 的再行动判定
                        if (src.HasBuff(BuffType.UsedWuXing)
                            && WuXingFunctions.IsXiangSheng((BuffType)src.GetBuffValue(BuffType.UsedWuXing), cardName, src.HasTalent(137)))
                        {
                            src.ModifyAnima(op.At(0));
                            src.ModifyMaxHp(op.At(1));
                            src.ModifyHp(op.At(1));
                        }
                        if (baseId == 20 && src.HasBuff(BuffType.YiYongGuoShuiLingPai))
                            src.ModifyBuffValue(BuffType.ExActionAgain, 1);
                        continue;
                    case 102:  // 与上一张用掉的五行相生 → 按稀有度档：加防 / 灵气 / 生命及上限 / 锋锐
                        if (src.HasBuff(BuffType.UsedWuXing)
                            && WuXingFunctions.IsXiangSheng((BuffType)src.GetBuffValue(BuffType.UsedWuXing), cardName, src.HasTalent(137)))
                        {
                            switch (current)
                            {
                                case 102: src.ModifyDef(op.At(0)); break;
                                case 10102: src.ModifyAnima(op.At(0)); break;
                                case 20102: src.ModifyMaxHp(op.At(0)); src.ModifyHp(op.At(0)); break;
                                case 30102: src.ModifyBuffValue(BuffType.FengRui, op.At(0)); break;
                            }
                        }
                        continue;
                    case 149:  // 首格（非临时牌）加体魄 + 回血。⚠ 早先误与 102 合并。
                        if (grid == 0 && !isTempCard) { src.ModifyTiPo(op.At(0)); src.ModifyHp(op.At(1)); }
                        continue;
                    case 246:  // 剑阵：直接打伤
                        if (CardTypes.IsJianZhen(src, cardId))
                            CombatMath.ApplyDamage(src, dst, DamageInfo.Create(src, DamageType.Damage, op.At(0), skipWoundCheck: true));
                        continue;
                    case 247:  // 灵剑：随防
                        if (CardTypes.IsLingJian(src, cardId)) src.ModifyBuffValue(BuffType.SuiFang, 1);
                        continue;
                    case 250:  // 雷牌：本轮无视防御
                        if (cardName.Contains("雷")) src.ModifyBuffValue(BuffType.BenLunWuShiFangYu, 1);
                        continue;
                    case 263:  // 灵印牌：灵气 / 生命 / 防 / 锋锐 各 +1，对方上限 -1
                        if (cardName.Contains("灵印"))
                        {
                            src.ModifyAnima(1);
                            src.ModifyHp(1);
                            src.ModifyDef(1);
                            src.ModifyBuffValue(BuffType.FengRui, 1);
                            dst.ModifyMaxHp(-1);
                        }
                        continue;
                    default: continue;
                }
            }
        }

        // ── 天赋循环之后（原码 22176 起）──
        // 刻印161：首格（非临时牌）加体魄/身法，并按参数逐个挂 buff。
        if (src.Subs.HasKeYinType(src, 161) && grid == 0 && !isTempCard)
        {
            src.ModifyTiPo(src.Subs.KeYinOtherparam(src, 161, 0));
            src.ModifyBuffValue(BuffType.ShenFa, src.Subs.KeYinOtherparam(src, 161, 1));
            int cnt = src.Subs.KeYinOtherparam(src, 161, 2);
            for (int i = 0; i < cnt; i++)
            {
                int p = src.NextParam(ParamRequest.FromDebuffPool(ParamSite.KeYin161, ParamDebuffPool.KeYin161));
                if (p != -1) src.ModifyBuffValue((BuffType)p, 1);
            }
        }
        // 共鸣89：首格（非临时牌）回灵气。
        if (src.IsTalentResonanceEffective(89) && grid == 0 && !isTempCard)
            src.ModifyAnima(cfg.ResonanceOtherParams(89).At(0));
        // 共鸣15：普攻按回合回灵气。
        if (src.HasTalentResonance(15) && cardId == 19)
        {
            int n = 0;
            if (src.CurrentRound >= 7) n = cfg.ResonanceOtherParams(15).At(0);
            if (src.CurrentRound >= 16) n = cfg.ResonanceOtherParams(15).At(1);
            if (n > 0) src.ModifyAnima(n);
        }
        // 共鸣18：剑阵首张回灵气（一次性）。
        if (src.IsTalentResonanceEffective(18) && CardTypes.IsJianZhen(src, cardId)
            && !src.CheckTalentResonanceTempFlag(18))
        {
            src.SetTalentResonanceTempFlag(18, true);
            src.ModifyAnima(1);
        }
        // 共鸣117：化神境界（Level.HuaShen = 5）的牌回 1 灵气。早先缺卡表 level 判定而缺失。
        if (src.HasTalentResonance(117) && cfg.CardLevel(cardId) == LevelHuaShen && !src.CheckTalentResonanceTempFlag(117))
            src.ModifyAnima(1);
        // 共鸣62：灵剑回灵气（一次性）。
        if (src.IsTalentResonanceEffective(62) && !src.CheckTalentResonanceTempFlag(62)
            && CardTypes.IsLingJian(src, cardId))
        {
            src.ModifyAnima(cfg.ResonanceOtherParams(62).At(0));
            src.SetTalentResonanceTempFlag(62, true);
        }
        // 共鸣22：base 5 牌给对方挂内伤 + 困缚。
        if (src.IsTalentResonanceEffective(22) && baseId == 5)
        {
            dst.ModifyBuffValue(BuffType.NeiShang, 1);
            dst.ModifyBuffValue(BuffType.KunFu, 1);
        }
        // 共鸣71：记录「本张是不是雷牌」供 GetNextRandomValue 用。
        if (src.IsTalentResonanceEffective(71)) src.SetTalentResonanceTempFlag(71, cardName.Contains("雷"));
        // 共鸣44：base 18 牌加随防。
        if (src.IsTalentResonanceEffective(44) && baseId == 18) src.ModifyBuffValue(BuffType.SuiFang, 1);
        // 共鸣126：**消耗牌 / 持续牌**加上限并回血（卡型门槛不能省，早先漏了 → b4_58 每张牌白送 +7）。
        if (src.IsTalentResonanceEffective(126)
            && (cfg.CardIsConsume(cardId) || cfg.CardIsSustain(cardId)))
        {
            int n = cfg.ResonanceOtherParams(126).At(0);
            src.ModifyMaxHp(n);
            src.ModifyHp(n);
        }
        // 下张牌再行动 / 汲然咒印（印牌，非临时牌；出局）/ 使用下张牌后消耗（非临时牌；出局）/ [耗尽]（非临时牌；出局）。
        if (src.HasBuff(BuffType.XiaZhangPaiZaiCiXingDong))
        {
            src.ModifyBuffValue(BuffType.ExActionAgain, 1);
            src.ModifyBuffValue(BuffType.XiaZhangPaiZaiCiXingDong, -1);
        }
        if (src.HasBuff(BuffType.JiRanZhouYin) && cardName.Contains("印") && !isTempCard)
        {
            src.ModifyBuffValue(BuffType.ExActionAgain, 1);
            src.ModifyBuffValue(BuffType.JiRanZhouYin, -1);
            src.CurrentCardSkip = true;
        }
        if (src.HasBuff(BuffType.ShiYongXiaZhangPaiHouXiaoHao) && !isTempCard)
        {
            src.ModifyBuffValue(BuffType.ShiYongXiaZhangPaiHouXiaoHao, -1);
            src.CurrentCardSkip = true;
        }
        if (cfg.CardDesc(cardId).Contains("[耗尽]") && !isTempCard) src.CurrentCardSkip = true;
        // 上回合的永久 buff：本张 baseId 命中则兑现。
        int fuJia = src.GetLastRoundPermanentBuffValue(BuffType.FuJiaZaiCiXingDong);
        if (fuJia > 0 && baseId == fuJia) src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        int fuJiaLq = src.GetLastRoundPermanentBuffValue(BuffType.FuJiaLingQi);
        if (fuJiaLq > 0 && baseId == fuJiaLq) src.ModifyAnima(1);
        // 混元逆克阵：本牌克下一格 → 激活下一格元素 + 回灵气/加防。
        if (src.HasBuff(BuffType.HunYuanNiKeZhen))
        {
            string n2 = CardNameAt(src, GridFunctions.NextGridCard(src, grid));
            if (WuXingFunctions.IsKeZhi(cardName, n2))
            {
                WuXingFunctions.ActiveWuXingInName(src, n2);
                src.ModifyAnima(src.GetBuffValue(BuffType.HunYuanNiKeZhen));
                src.ModifyDef(src.GetBuffValue(BuffType.HunYuanNiKeZhenJiaFang));
            }
        }
        // ── IL_2358 共鸣36：本牌元素已激活则直接打伤（13 回合后加强）──
        if (src.IsTalentResonanceEffective(36) && WuXingFunctions.IsCardNameActived(src, cardName))
        {
            int dmg = cfg.ResonanceOtherParams(36).At(0);
            if (src.CurrentRound >= 13) dmg = cfg.ResonanceOtherParams(36).At(1);
            CombatMath.ApplyDamage(src, src.Opponent, DamageInfo.Create(src, DamageType.Damage, dmg, skipWoundCheck: true));
        }
        // ── IL_245a 星元：本牌所在格是星位（或龙马精神）才消耗，给对手挂内伤 ──
        if (src.GetBuffValue(BuffType.XingYuan) > 0 && GridMarkFunctions.CheckXingWei(src, grid))
        {
            src.ModifyBuffValue(BuffType.XingYuan, -1);
            dst.ModifyBuffValue(BuffType.NeiShang, 1);
        }
        // 刻印出牌前钩子（原码 22521，星元之后）。
        KeYinHookFunctions.OnBeforeCard(src, dst, src.CurrentCardId, grid);

        // 方法末尾（原码 22815）：清掉上一张牌留下的实际伤害 / 受伤计数（出牌后钩子末尾还会再清一次）。
        src.RemoveBuff(BuffType.ActualDamage);
        src.RemoveBuff(BuffType.WoundedCount);
    }

    /// <summary>Level 枚举里的 HuaShen（5）：共鸣 117 的门槛。</summary>
    private const int LevelHuaShen = 5;

    /// <summary>Subcategory 枚举里的 TianZong（天宗牌）。</summary>
    private const int SubcategoryTianZong = 8;

    /// <summary>Subcategory 枚举里的 XianZong（仙宗牌）。</summary>
    private const int SubcategoryXianZong = 9;

    /// <summary>
    /// 忠实移植 <c>CardActionBase.CheckAdjacentEffectsAfterCard</c>（原码 22849，d__18；调用点 OnAfterExecuted 的 IL_1eea）。
    /// 整段只在**非临时牌**时跑（原码 <c>if (!isTempCard)</c>，本 sim 用 TriggerDepth 近似）。
    /// ⚠ 早先 sim 整段缺失：梦•云剑点星（1000069 系）「[相邻]牌使用后造成伤害」、梦•流转、梦•天元都静默失效。
    /// </summary>
    private static void CheckAdjacentEffectsAfterCard(Combatant src, Combatant foe, int cardId, int gridNumber)
    {
        if (CardEffects.TriggerDepth > 0 || gridNumber < 0) return;
        var cfg = src.Config;

        // 扫整副牌：梦•流转（7030092 / 7040092）、流转加防（7000092 系，o[0] 累加）、梦•天元（4030089 / 4040089）。
        bool mengLiuZhuan = false;
        int liuZhuanJiaFang = 0;
        bool mengTianYuan = false;
        foreach (var c in src.Board)
        {
            if (c.Id == 7040092 || c.Id == 7030092) mengLiuZhuan = true;
            if (CardTypes.BaseId(c.Id) == 7000092) liuZhuanJiaFang += cfg.CardOtherParams(c.Id).At(0);
            if (c.Id == 4030089 || c.Id == 4040089) mengTianYuan = true;
        }

        // 云剑点星：左右相邻格是 1000069 / 1010069 / 1020069 → 各造成其 o[0] 伤害；
        //           是 1030069 / 1040069 → 各追加「o[0] + 当前灵气」攻。（先左后右）
        int prevGrid = GridFunctions.GetPreviousGrid(src, gridNumber);
        int nextGrid = GridFunctions.GetNextGrid(src, gridNumber);
        foreach (int g in new[] { prevGrid, nextGrid })
        {
            int id = g >= 0 && g < src.Board.Count ? src.Board[g].Id : 0;
            if (id == 1000069 || id == 1010069 || id == 1020069)
                CombatMath.ApplyDamage(src, foe, DamageInfo.Create(src, DamageType.Damage, cfg.CardOtherParams(id).At(0), skipWoundCheck: true));
            if (id == 1030069 || id == 1040069)
                CombatMath.Attack(src, foe, cfg.CardOtherParams(id).At(0) + src.Anima, 1);
        }

        string curName = cfg.CardName(cardId);
        bool zhiHuo = src.HasTalent(137);
        // 梦•流转：本牌与后一格**不**相生、但与后两格相生，且后一格未出局 → 星弈•断 +1（跳过后一格）。
        if (mengLiuZhuan && nextGrid >= 0 && nextGrid < src.Board.Count)
        {
            int next2 = GridFunctions.GetNextGrid(src, nextGrid);
            string name1 = cfg.CardName(src.Board[nextGrid].Id);
            string name2 = next2 >= 0 && next2 < src.Board.Count ? cfg.CardName(src.Board[next2].Id) : "";
            if (!WuXingFunctions.IsXiangSheng(curName, name1, zhiHuo) && WuXingFunctions.IsXiangSheng(curName, name2, zhiHuo)
                && !src.Deck.IsSkipped(nextGrid))
                src.ModifyBuffValue(BuffType.XingYi_Duan, 1);
        }
        // 流转加防：上一张用掉的五行生出本牌的五行 → 加防。
        if (liuZhuanJiaFang > 0 && src.HasBuff(BuffType.UsedWuXing)
            && WuXingFunctions.IsXiangSheng((BuffType)src.GetBuffValue(BuffType.UsedWuXing), curName, zhiHuo))
            src.ModifyDef(liuZhuanJiaFang);
        // 梦•天元：本牌在星位 → 对方内伤（取 4040089 的 o[0]）。
        if (mengTianYuan && GridMarkFunctions.IsXingWei(src, gridNumber))
            foe.ModifyBuffValue(BuffType.NeiShang, cfg.CardOtherParams(4040089).At(0));
    }

    /// <summary>出牌后钩子里只在**非临时执行**时跑的那段（原码 IL_0415 ~ IL_1a7f，见 <see cref="OnAfterExecuted"/>）。</summary>
    private static void AfterNonTempCard(Combatant src, Combatant foe, Config.IBattleConfig cfg, Config.JsonBattleConfig.CardMeta? meta,
        string cardName, int cardId, int gridNumber)
    {
        // ── IL_0415 云剑•周天（187）/ IL_0513 云剑•猫影（403）：使用云剑牌后追加层数攻 ──
        int yjzt = src.GetBuffValue(BuffType.YunJianZhouTian);
        if (yjzt > 0 && CardTypes.IsYunJian(src, cardId)) CombatMath.Attack(src, foe, yjzt, 1);
        int yjmy = src.GetBuffValue(BuffType.YunJianMaoYing);
        if (yjmy > 0 && CardTypes.IsYunJian(src, cardId)) CombatMath.Attack(src, foe, yjmy, 1);
        // ── IL_0611 周天剑阵（8000008）：使用有攻的牌后追加 o[1] 攻（取 8000008 自己的配置），扣一层 ──
        // 门 `DanKaGongJiJiShu > 0`（本张牌打出过攻击，普攻也算），在它被清理之前。
        if (src.HasBuff(BuffType.ZhouTianJianZhen) && src.GetBuffValue(BuffType.DanKaGongJiJiShu) > 0)
        {
            CombatMath.Attack(src, foe, cfg.CardOtherParams(8000008).At(1), 1);
            src.ModifyBuffValue(BuffType.ZhouTianJianZhen, -1);
        }
        // ── IL_072a 察体（11000022）：下次使用有攻牌后追加层数攻，然后清掉 ──
        if (src.HasBuff(BuffType.ChaTi) && src.GetBuffValue(BuffType.DanKaGongJiJiShu) > 0)
        {
            CombatMath.Attack(src, foe, src.GetBuffValue(BuffType.ChaTi), 1);
            src.RemoveBuff(BuffType.ChaTi);
        }
        // ── IL_0838 下次含攻牌追加攻（刻印 94 施加）：同门，然后清掉。早先缺失 ──
        if (src.HasBuff(BuffType.XiaCiHanGongPaiZhuiJiaGong) && src.GetBuffValue(BuffType.DanKaGongJiJiShu) > 0)
        {
            CombatMath.Attack(src, foe, src.GetBuffValue(BuffType.XiaCiHanGongPaiZhuiJiaGong), 1);
            src.RemoveBuff(BuffType.XiaCiHanGongPaiZhuiJiaGong);
        }
        // ── IL_094a 百兽灵剑阵：每回合首次使用剑阵 / 灵剑 → 灵气 × 层数 的真伤；ShengXiao 是本回合守卫 ──
        int bsljz = src.GetBuffValue(BuffType.BaiShouLingJianZhen);
        if (bsljz > 0 && !src.HasBuff(BuffType.BaiShouLingJianZhenShengXiao)
            && (CardTypes.IsJianZhen(src, cardId) || CardTypes.IsLingJian(src, cardId)))
        {
            int bsljzDmg = src.Anima * bsljz;
            if (bsljzDmg > 0)
            {
                CombatMath.ApplyDamage(src, foe, DamageInfo.Create(src, DamageType.Damage, bsljzDmg, skipWoundCheck: true));
                src.ModifyBuffValue(BuffType.BaiShouLingJianZhenShengXiao, 1);
            }
        }
        // ── IL_0ac7 剑气：打灵剑类牌时把当前剑气层数当额外伤害打出去（不消耗）──
        if (src.HasBuff(BuffType.JianQi))
        {
            bool jianQiHits = CardTypes.IsLingJian(src, cardId)
                || (src.Subs.HasCardInDeck(src, 1000075) && CardTypes.IsJian(src, cardId))
                || (src.HasFateStrategy(379) && CardTypes.IsYunJian(src, cardId))
                || (src.HasFateStrategy(387) && cardId == 19);
            if (jianQiHits)
            {
                int jianQi = src.GetBuffValue(BuffType.JianQi);
                if (jianQi > 0)
                    CombatMath.ApplyDamage(src, foe, DamageInfo.Create(src, DamageType.Damage, jianQi, skipWoundCheck: true));
            }
        }
        // ── IL_0c67 天赋 173 / 刻印 160：使用带体魄的牌后追加攻。早先缺失 ──
        if ((src.HasTalent(173) || src.Subs.HasKeYinType(src, 160)) && (meta?.Physique ?? 0) > 0)
        {
            int tp = 0;
            if (src.HasTalent(173)) tp += cfg.TalentOtherParams(173).At(1);
            if (src.Subs.HasKeYinType(src, 160)) tp += src.Subs.KeYinOtherparam(src, 160, 0);
            CombatMath.Attack(src, foe, tp, 1);
        }
        // ── IL_0dba 共鸣 132：使用「掌」牌后追加攻。早先缺失 ──
        if (src.IsTalentResonanceEffective(132) && cardName.Contains("掌"))
            CombatMath.Attack(src, foe, cfg.ResonanceOtherParams(132).At(0), 1);
        // ── IL_0ec3 灵玄不息：使用描述带负面状态字样的牌后，追加「层数 + 自身负面数/2」攻 ──
        if (src.HasBuff(BuffType.LingXuanBuXi))
        {
            string desc = cfg.CardDesc(cardId);
            if (desc.Contains("负面状态") || desc.Contains("内伤") || desc.Contains("外伤") || desc.Contains("虚弱")
                || desc.Contains("破绽") || desc.Contains("减攻") || desc.Contains("困缚"))
                CombatMath.Attack(src, foe, src.GetBuffValue(BuffType.LingXuanBuXi) + src.GetDebuffCount() / 2, 1);
        }
        // ── IL_1015 天运•避凶（11000007）/ 天运•趋吉（11000008）──
        // 避凶：使用**没有[开局]**的牌后，加防 o[1] + 回血 o[2]（取 11000007 的配置），扣一层。
        // 趋吉：门是 `TriggerOpening(grid)` 的**非 judge** 调用 —— 判断的同时把[开局]真跑一遍；再造成 o[1] 伤害，扣一层。
        if (gridNumber >= 0 && gridNumber < src.Board.Count)
        {
            if (src.HasBuff(BuffType.TianYunBiXiong) && !CardOpeningFunctions.HasOpening(src, gridNumber))
            {
                var ty7 = cfg.CardOtherParams(11000007);
                src.ModifyDef(ty7.At(1));
                src.ModifyHp(ty7.At(2));
                src.ModifyBuffValue(BuffType.TianYunBiXiong, -1);
            }
            if (src.HasBuff(BuffType.TianYunQuJi) && CardOpeningFunctions.HasOpening(src, gridNumber))
            {
                CardOpeningFunctions.Trigger(src, gridNumber);
                CombatMath.ApplyDamage(src, foe, DamageInfo.Create(src, DamageType.Damage,
                    cfg.CardOtherParams(11000008).At(1), skipWoundCheck: true));
                src.ModifyBuffValue(BuffType.TianYunQuJi, -1);
            }
        }
        // 黄雀在后 / IL_12da 灵雀在后：本张牌用了[后招]（ShiYongHouZhao）则追加层数攻（都不消耗）。
        if (src.HasBuff(BuffType.HuangQueZaiHou) && src.HasBuff(BuffType.ShiYongHouZhao))
            CombatMath.Attack(src, foe, src.GetBuffValue(BuffType.HuangQueZaiHou), 1);
        if (src.HasBuff(BuffType.LingQueZaiHou) && src.HasBuff(BuffType.ShiYongHouZhao))
            CombatMath.Attack(src, foe, src.GetBuffValue(BuffType.LingQueZaiHou), 1);
        // ── IL_13ca / IL_14bf 幻•斗转星移（269）：使用星弈牌后追加层数攻；使后一格成为星位（都不消耗）──
        // ⚠ 原码门是 `cardConfig.name.Contains("星弈")`；循环上界照抄字面量 1。
        if (cardName.Contains("星弈"))
        {
            if (src.HasBuff(BuffType.DouZhuanPlus))
                CombatMath.Attack(src, foe, src.GetBuffValue(BuffType.DouZhuanPlus), 1);
            if (src.HasBuff(BuffType.HuanDouZhuanJiaXingWei))
            {
                int nextGrid = gridNumber;
                for (int i = 0; i < 1; i++)
                {
                    nextGrid = GridFunctions.GetNextGrid(src, nextGrid);
                    GridMarkFunctions.AddXingWei(src, nextGrid);
                }
            }
        }
        // ── IL_1599 星焰诀+：使用后招牌回层数灵气；本张确实触发了后招则再加等量星力。早先缺失 ──
        // ⚠ CheckHouZhao 有副作用（挂 ShiYongHouZhao 等），原码这里就是再调一次，照抄。
        if (src.HasBuff(BuffType.XingYanJuePlus) && CardTypes.IsHouZhao(src, cardId))
        {
            int xyj = src.GetBuffValue(BuffType.XingYanJuePlus);
            src.ModifyAnima(xyj);
            if (HouZhaoFunctions.CheckHouZhao(src, cardId, gridNumber)) src.ModifyBuffValue(BuffType.XingLi, xyj);
        }
        // 梦•星罗棋布：本回合首次在**非星位**出牌 → 加层数灵气与星力。早先缺失。
        if (src.HasBuff(BuffType.MengXingLuoQiBu) && src.GetBuffValue(BuffType.MengXingLuoHuaShenChuFaCiShu) == 0
            && gridNumber >= 0 && !GridMarkFunctions.IsXingWei(src, gridNumber))
        {
            src.ModifyBuffValue(BuffType.MengXingLuoHuaShenChuFaCiShu, 1);
            src.ModifyAnima(src.GetBuffValue(BuffType.MengXingLuoQiBu));
            src.ModifyBuffValue(BuffType.XingLi, src.GetBuffValue(BuffType.MengXingLuoQiBu));
        }
        // ── IL_16fc 梦•星罗棋布·地境界：在**非星位**出牌时扣一层、加 1 灵气 + 1 星力（早先漏了非星位的门）──
        if (src.HasBuff(BuffType.MengXingLuoQiBuDiJingJie) && gridNumber >= 0 && !GridMarkFunctions.IsXingWei(src, gridNumber))
        {
            src.ModifyBuffValue(BuffType.MengXingLuoQiBuDiJingJie, -1);
            src.ModifyAnima(1);
            src.ModifyBuffValue(BuffType.XingLi, 1);
        }
        // ── IL_17c5 幻•气沉丹田（278）扣层：本张打出过攻击 → -1 ──
        if (src.HasBuff(BuffType.QiChenDanTianPlus) && src.GetBuffValue(BuffType.DanKaGongJiJiShu) > 0)
            src.ModifyBuffValue(BuffType.QiChenDanTianPlus, -1);
        // 共鸣 5：本回合首次打出攻击后追加攻，并把剑意（封顶 o[2]）记成「恢复剑意」。早先缺失。
        if (src.IsTalentResonanceEffective(5) && !src.CheckTalentResonanceTempFlag(5) && src.GetBuffValue(BuffType.DanKaGongJiJiShu) > 0)
        {
            CombatMath.Attack(src, foe, cfg.ResonanceOtherParams(5).At(1), 1);
            src.SetTalentResonanceTempFlag(5, true);
            if (src.HasBuff(BuffType.JianYi))
            {
                int jy = src.GetBuffValue(BuffType.JianYi);
                if (jy > cfg.ResonanceOtherParams(5).At(2)) jy = cfg.ResonanceOtherParams(5).At(2);
                src.ModifyBuffValue(BuffType.HuiFuJianYi, jy);
            }
        }
        // ── IL_1924 共鸣 14：普攻（id 19）后追加攻 / IL_19d5 天赋 252：雷牌后追加随机攻。早先缺失 ──
        if (src.IsTalentResonanceEffective(14) && cardId == 19)
            CombatMath.Attack(src, foe, cfg.ResonanceOtherParams(14).At(0), 1);
        if (src.HasTalent(252) && cardName.Contains("雷"))
            CombatMath.Attack(src, foe, src.GetNextRandomValue(ParamRequest.Range(ParamSite.Talent252,
                cfg.TalentOtherParams(252).At(0), cfg.TalentOtherParams(252).At(1))), 1);
    }

    /// <summary>
    /// 出牌后钩子：忠实移植 <c>CardActionBase.OnAfterExecuted</c>（原码 23018，d__20），由 <see cref="CardEffects.ExecuteEffect"/>
    /// 在每次执行卡效果之后调用（临时牌也调，按 <see cref="CardEffects.TriggerDepth"/> 识别 isTempCard）。
    /// 「清 ActualDamage / WoundedCount」原码在两处：出牌前钩子末尾（22815，清上一张的残留）与这里末尾（24021–24023，记账后清）。
    /// </summary>
    /// <param name="gridNumber">当前牌所在格（hadUsed 按格记）。</param>
    public static void OnAfterExecuted(Combatant src, Combatant foe, int cardId, int gridNumber)
    {
        // ⚠ **顺序照原码 `__CG_OnAfterExecuted_d__20` 的 goto 链**（DFS 导出，= IL 偏移序），不是反编译的文件序。
        //   早先按文件序散搬，三类错误：
        //   ① 开头那句 `AfterCardAciton += 1` 被挪到了末尾 —— 于是本钩子里所有追加攻击（周天剑阵 / 察体 / 惊触 / 崩拳链…）
        //      都按「本张牌的攻击」吃了本牌专属加成（崩拳•戳、狂剑、雷等的 `!HasBuff(AfterCardAciton)` 门）；
        //   ② 开头一串「本张牌专属」buff 的清除（随方、气势倍率、玄心斩魄、本轮无视防御、无视护体/虚弱/减攻、不耗锋锐、
        //      必定视作击伤）**整段缺失** —— 挂上后永久生效；
        //   ③ 十来条读取侧缺失（见各条注释）。
        bool isTempCard = CardEffects.TriggerDepth > 0;
        var cfg = src.Config;
        var meta = (cfg as Config.JsonBattleConfig)?.Card(cardId);
        string cardName = cfg.CardName(cardId);

        // ── default 段 ──
        src.ModifyBuffValue(BuffType.AfterCardAciton, 1);
        if (src.HasBuff(BuffType.SuiFang)) src.RemoveBuff(BuffType.SuiFang);
        if (src.HasBuff(BuffType.QiShiBeiLv)) src.RemoveBuff(BuffType.QiShiBeiLv);
        if (src.HasBuff(BuffType.XuanXinZhanPo)) src.RemoveBuff(BuffType.XuanXinZhanPo);
        if (src.HasBuff(BuffType.BenLunWuShiFangYu)) src.RemoveBuff(BuffType.BenLunWuShiFangYu);
        if (src.HasBuff(BuffType.WuShiHuTi)) src.RemoveBuff(BuffType.WuShiHuTi);
        if (src.HasBuff(BuffType.WuShiXuRuo)) src.RemoveBuff(BuffType.WuShiXuRuo);
        if (src.HasBuff(BuffType.WuShiJianGong)) src.RemoveBuff(BuffType.WuShiJianGong);
        if (src.HasBuff(BuffType.BuHaoFengRui)) src.RemoveBuff(BuffType.BuHaoFengRui);
        if (src.HasBuff(BuffType.BiDingShiZuoJiShang)) src.RemoveBuff(BuffType.BiDingShiZuoJiShang);
        // 崩拳「一次性加成」的扣层：CalculateAttack 只把消耗量**记**进 XiaoHao*（原码 12726），真正扣在这里；
        // 只记不扣 = 崩拳•戳的 +2 变成每张崩拳都加。连崩牌不扣层（IsLianBeng）。
        if (src.HasBuff(BuffType.XiaoHaoBengQuanChuo))
        {
            if (!CardTypes.IsLianBeng(cardId))
                src.ModifyBuffValue(BuffType.BengQuanChuo, -src.GetBuffValue(BuffType.XiaoHaoBengQuanChuo));
            src.RemoveBuff(BuffType.XiaoHaoBengQuanChuo);
        }
        if (src.HasBuff(BuffType.XiaoHaoBengQuanDuoXing))
        {
            if (!CardTypes.IsLianBeng(cardId))
                src.ModifyBuffValue(BuffType.BengQuanDuoXing, -src.GetBuffValue(BuffType.XiaoHaoBengQuanDuoXing));
            src.RemoveBuff(BuffType.XiaoHaoBengQuanDuoXing);
        }
        // 梦断拳结束：本张牌打中过（CombatMath 置 MengDuanQuanJieShu=1）→ 梦断拳扣一层。
        if (src.HasBuff(BuffType.MengDuanQuanJieShu))
        {
            src.ModifyBuffValue(BuffType.MengDuanQuan, -src.GetBuffValue(BuffType.MengDuanQuanJieShu));
            src.RemoveBuff(BuffType.MengDuanQuanJieShu);
        }
        // 仙命 387：普攻（id 19）把全部灵气转成剑气。早先缺失。
        if (src.HasFateStrategy(387) && cardId == 19)
        {
            int an = src.Anima;
            src.ModifyAnima(-an);
            src.ModifyBuffValue(BuffType.JianQi, an);
        }
        // 溟空剑阵诀（1000051）：使用剑阵牌后追加层数攻。
        // ⚠ 原码 `if (!isTempCard) { 溟空剑阵诀…; goto IL_0415; } goto IL_1a7f;` —— 临时执行（魔化鲛珠 / 天星 / 随机使用…）
        //   **跳过 IL_0415 ~ IL_1a7f 整段**（云剑•周天 / 猫影 / 周天剑阵 / 察体 / 百兽灵剑阵 / 剑气 / 天运 / 黄雀 / 星弈 / 共鸣 5 …），
        //   只接着跑 IL_1a7f（崩拳•惊触）往后。早先只把溟空剑阵诀包进了门，其余临时执行也照打
        //   （B7 r6 b7_312：魔化鲛珠连用 3 次崩拳缠，周天剑阵多打 2 下 = 多 20 伤害）。
        if (!isTempCard)
        {
            int mkjz = src.GetBuffValue(BuffType.MingKongJianZhenJue);
            if (mkjz > 0 && CardTypes.IsJianZhen(src, cardId)) CombatMath.Attack(src, foe, mkjz, 1);
            AfterNonTempCard(src, foe, cfg, meta, cardName, cardId, gridNumber);
        }
        // ── IL_1a7f 崩拳•惊触（10000046）：追加「层数 + 本场失去生命/o[1]」攻，然后清掉 ──
        if (src.HasBuff(BuffType.ChuFaJingChu))
        {
            int bonus = src.GetBuffValue(BuffType.LoseHpCount) / cfg.CardOtherParams(10000046).At(1);
            CombatMath.Attack(src, foe, src.GetBuffValue(BuffType.ChuFaJingChu) + bonus, 1);
            src.RemoveBuff(BuffType.ChuFaJingChu);
        }
        // ── 「崩拳触发」链（IL_1b9a → IL_1c89 → IL_1d30 → IL_1dd7）：施加侧在 OnBeforeExecuted 的 IsBengQuan 门里 ──
        // 幻•崩拳缠（319）：追加「层数」攻 ×2 次。
        if (src.HasBuff(BuffType.ChanPlusChuFa))
        {
            CombatMath.Attack(src, foe, src.GetBuffValue(BuffType.ChanPlusChuFa), 2);
            src.RemoveBuff(BuffType.ChanPlusChuFa);
        }
        // 下张崩拳追加攻击：「层数」攻 ×2 次。
        if (src.HasBuff(BuffType.XiaZhangBengQuanZhuiJiaGongJiYiChuFa))
        {
            CombatMath.Attack(src, foe, src.GetBuffValue(BuffType.XiaZhangBengQuanZhuiJiaGongJiYiChuFa), 2);
            src.RemoveBuff(BuffType.XiaZhangBengQuanZhuiJiaGongJiYiChuFa);
        }
        // 梦•崩拳连崩（10000082 / 10000089）：追加 2 攻 ×「层数」次（参数位置与上面两条相反）。
        if (src.HasBuff(BuffType.MengLianBengChuFa))
        {
            CombatMath.Attack(src, foe, 2, src.GetBuffValue(BuffType.MengLianBengChuFa));
            src.RemoveBuff(BuffType.MengLianBengChuFa);
        }
        // 崩拳•伏虎（337）：造成等同**当前防**的伤害。
        if (src.HasBuff(BuffType.ChuFaFuHu))
        {
            CombatMath.ApplyDamage(src, foe, DamageInfo.Create(src, DamageType.Damage, src.Def, skipWoundCheck: true));
            src.RemoveBuff(BuffType.ChuFaFuHu);
        }
        // ── IL_1eea 相邻后效 / IL_1f55 刻印出牌后钩子 / IL_1fc6 仙命 416 蹴鞠飞袭 ──
        CheckAdjacentEffectsAfterCard(src, foe, cardId, gridNumber);
        KeYinHookFunctions.OnAfterCard(src, foe, cardId, gridNumber);   // 刻印出牌后钩子（原码 IL_1f55）
        if (src.GetBuffValue(BuffType.CuJuFeiXi) > 0 && cardName.Contains("火灵"))
        {
            int cj = src.GetBuffValue(BuffType.CuJuFeiXi);
            src.RemoveBuff(BuffType.CuJuFeiXi);
            CombatMath.Attack(src, foe, cj, 1);
        }

        // ── switch 之外的收尾（原码 23860 起）：牌型记账 ──
        // 这段看起来只是「把这张牌是什么类型记到计数器上」，但**别的卡的条件读的正是这些计数器**
        // （LianYun 26 张、KuangJian 10 张、JianZhenCount / 五行牌使用次数 / 宗门计数…）。
        // 天赋221：带伤且没记过 → 万若游龙生效 + 再行动。
        if (src.HasTalent(221) && src.HasBuff(BuffType.WoundedCount) && !src.HasBuff(BuffType.WanRuoYouLongShengXiao))
        {
            src.ModifyBuffValue(BuffType.WanRuoYouLongShengXiao, 1);
            src.ModifyBuffValue(BuffType.ExActionAgain, 1);
            // 天赋222：本回合再行动次数上限 +1（只记一次）。
            // ⚠ 原码 23864 这段**嵌在天赋 221 的 if 里面**；早先被拉平成并列的 if → 只要有 222，
            //   每张牌打完上限都 +1（b6_119：R 带 222 不带 221，real R=6 / sim R=41）。
            if (src.HasTalent(222) && !src.HasBuff(BuffType.KeYinShanFeng))
            {
                src.ActionAgainPerRound++;
                src.ModifyBuffValue(BuffType.KeYinShanFeng, 1);
            }
        }
        src.RemoveBuff(BuffType.ShiYongHouZhao);
        src.RemoveBuff(BuffType.DanKaGongJiJiShu);
        // ⚠ `BuYueDengYun` 那条**嵌在 `IsYunJian` 里面**（早先被拉平成两个兄弟 if → 每张牌都加攻）。
        if (CardTypes.IsYunJian(src, cardId))
        {
            src.ModifyBuffValue(BuffType.LianYun, 1);
            if (src.HasBuff(BuffType.BuYueDengYun))
                src.ModifyBuffValue(BuffType.JiaGong, src.GetBuffValue(BuffType.BuYueDengYun));
        }
        if (StopLianYun(src, cardId)) src.RemoveBuff(BuffType.LianYun);
        if (src.HasTalentResonance(10))
            src.SetTalentResonanceTempFlag(10, CardTypes.IsLingJian(src, cardId));
        if (CardTypes.IsKuangJian(src, cardId)
            || (KeYinCardFunctions.HasKeYinType(src, 92) && CardTypes.IsLingJian(src, cardId)))
            src.ModifyBuffValue(BuffType.KuangJian, 1);
        if (CardTypes.IsJianZhen(src, cardId)) src.ModifyBuffValue(BuffType.JianZhenCount, 1);
        if (IsJianCard(src, cardId)) src.ModifyBuffValue(BuffType.JiLuYongGuoJianPai, 1);
        // 琴师牌计数（career == QinShi = 3）。早先因为配置没抽 career 而缺失。
        if (cfg.CardCareer(cardId) == 3) src.ModifyBuffValue(BuffType.QinShiPai, 1);
        if (cardName.Contains("蛇")) src.ModifyBuffValue(BuffType.SnakeCardUsed, 1);
        if (CardTypes.IsHouZhao(src, cardId)) src.ModifyBuffValue(BuffType.HouZhaoPaiJiShu, 1);
        if (IsWuXingCardName(cardName))
            src.ModifyBuffValue(BuffType.JiLuWuXingPaiShiYongCiShu, 1);
        if ((meta?.HpCost ?? 0) > 0) src.ModifyBuffValue(BuffType.HaoShengMingDePaiJiShu, 1);
        // 万能剑 / 下张牌算作狂剑的**扣层**：出牌前记账 +1（OnBeforeExecuted），这里 -1；
        //   记账值在 (0, 层数] 之内说明本张牌吃到了效果 → 层数 -1。
        if (src.HasBuff(BuffType.WanNengJian))
        {
            int cj = src.GetBuffValue(BuffType.WanNengJianShengXiaoCengJi);
            if (cj > 0 && cj <= src.GetBuffValue(BuffType.WanNengJian)) src.ModifyBuffValue(BuffType.WanNengJian, -1);
            src.ModifyBuffValue(BuffType.WanNengJianShengXiaoCengJi, -1);
        }
        if (src.HasBuff(BuffType.XiaZhangPaiSuanZuoKuangJian))
        {
            int cj = src.GetBuffValue(BuffType.XiaZhangPaiSuanZuoKuangJianCengJi);
            if (cj > 0 && cj <= src.GetBuffValue(BuffType.XiaZhangPaiSuanZuoKuangJian))
                src.ModifyBuffValue(BuffType.XiaZhangPaiSuanZuoKuangJian, -1);
            src.ModifyBuffValue(BuffType.XiaZhangPaiSuanZuoKuangJianCengJi, -1);
        }
        if (src.HasBuff(BuffType.XiaZhangPaiSuanBengQuan) && !isTempCard) src.RemoveBuff(BuffType.XiaZhangPaiSuanBengQuan);
        // 宗门牌计数（Subcategory：TianZong=8 / XianZong=9）。
        int sub = meta?.Subcategory ?? 0;
        if (sub == 8)
        {
            src.ModifyBuffValue(BuffType.LianXuShiYongTianZong, 1);
            src.ModifyBuffValue(BuffType.ShiYongTianZong, 1);
            if (src.GetBuffValue(BuffType.WanXiangJianShuiZong) > 0)
                src.ModifyBuffValue(BuffType.XianZongJiShu, 1);
        }
        else src.RemoveBuff(BuffType.LianXuShiYongTianZong);
        if (sub == 9)
        {
            src.ModifyBuffValue(BuffType.XianZongJiShu, 1);
            if (src.GetBuffValue(BuffType.WanXiangJianShuiZong) > 0)
                src.ModifyBuffValue(BuffType.ShiYongTianZong, 1);
        }
        // 397 灵源红豆粽 / 399 仙府大白粽 的使用计数。早先缺失。
        if (CardTypes.BaseId(cardId) == 397) src.ModifyBuffValue(BuffType.LingYuanHongDouZongJiShu, 1);
        if (CardTypes.BaseId(cardId) == 399) src.ModifyBuffValue(BuffType.XianFuDsaBaiZongJiShu, 1);
        // 剑意的**消耗**：攻击时只记了「用掉多少」（XiaoHaoJianYi），真正扣在这里。
        if (src.HasBuff(BuffType.XiaoHaoJianYi))
        {
            if (src.HasBuff(BuffType.JianYiLiuZhuan))
            {
                // 剑意流转：这颗「消耗」改扣一层流转，剑意本身不动。
                src.ModifyBuffValue(BuffType.JianYiLiuZhuan, -1);
                src.RemoveBuff(BuffType.XiaoHaoJianYi);
            }
            else
            {
                int jianYiUsed = src.GetBuffValue(BuffType.XiaoHaoJianYi);
                src.ModifyBuffValue(BuffType.JianYi, -jianYiUsed);
                // 共鸣61：每次攻击的剑意消耗**返还一半**（只触发一次）。
                if (src.IsTalentResonanceEffective(61) && !src.CheckTalentResonanceTempFlag(61))
                {
                    src.SetTalentResonanceTempFlag(61, true);
                    src.ModifyBuffValue(BuffType.JianYi, jianYiUsed / 2);
                }
                // 灵犀+：本次消耗转为灵气。
                if (src.HasBuff(BuffType.LingXiPlus))
                {
                    src.ModifyBuffValue(BuffType.LingXiPlus, -1);
                    src.ModifyAnima(jianYiUsed);
                }
                src.RemoveBuff(BuffType.XiaoHaoJianYi);
            }
        }
        // 恢复剑意（共鸣 5 等记下的）：加回剑意（幻•飞崖剑的额外层一并加），然后清掉。早先缺失。
        if (src.HasBuff(BuffType.HuiFuJianYi))
        {
            int hf = src.GetBuffValue(BuffType.HuiFuJianYi);
            if (src.GetBuffValue(BuffType.HuanFeiYaJianDuoJiaJianYi) > 0)
            {
                hf += src.GetBuffValue(BuffType.HuanFeiYaJianDuoJiaJianYi);
                src.RemoveBuff(BuffType.HuanFeiYaJianDuoJiaJianYi);
            }
            if (hf > 0) src.ModifyBuffValue(BuffType.JianYi, hf);
            src.RemoveBuff(BuffType.HuiFuJianYi);
        }
        // 「本张牌不触发剑意」只对本张牌有效（CombatMath 读），出牌后清掉。早先从不清除。
        if (src.HasBuff(BuffType.KaPaiBuChuFaJianYi)) src.RemoveBuff(BuffType.KaPaiBuChuFaJianYi);
        // 甲生名在动：本回合还能再行动却没触发再行动 → 换一次「外行动」。
        int zai = src.GetBuffValue(BuffType.ZaiCiXingDong);
        if (src.HasBuff(BuffType.JiaShengMingZaiDong) && src.HasBuff(BuffType.HuiHeJiaShengMing)
            && src.GetBuffValue(BuffType.ExActionAgain) <= 0 && !src.CurrentCardActionAgain
            && zai < src.ActionAgainPerRound)
        {
            src.ModifyBuffValue(BuffType.JiaShengMingZaiDong, -1);
            src.ModifyBuffValue(BuffType.ExActionAgain, 1);
        }
        // 记录总击伤，然后清零本张牌的实际伤害/受伤计数。
        src.ModifyBuffValue(BuffType.JiLuZongJiShangZhi, src.GetBuffValue(BuffType.ActualDamage));
        src.RemoveBuff(BuffType.ActualDamage);
        src.RemoveBuff(BuffType.WoundedCount);
        // 本格这张牌「已出过」—— 后招等 30+ 处靠它判首次使用。
        src.SetHadUsed(gridNumber, true);
        // 记录本张牌用掉的五行：按牌名写 UsedWuXing，出水灵再记一层。
        if (cardName.Contains("金灵")) src.SetBuffValue(BuffType.UsedWuXing, 237);
        else if (cardName.Contains("木灵")) src.SetBuffValue(BuffType.UsedWuXing, 238);
        else if (cardName.Contains("水灵"))
        {
            src.SetBuffValue(BuffType.UsedWuXing, 239);
            src.ModifyBuffValue(BuffType.YiYongGuoShuiLingPai, 1);
        }
        else if (cardName.Contains("火灵")) src.SetBuffValue(BuffType.UsedWuXing, 240);
        else if (cardName.Contains("土灵")) src.SetBuffValue(BuffType.UsedWuXing, 241);
        else if (src.HasBuff(BuffType.UsedWuXing)) src.RemoveBuff(BuffType.UsedWuXing);
        // 持续牌留下「用过持续牌」计数。
        if (cfg.CardIsSustain(cardId)) src.ModifyBuffValue(BuffType.YongGuoChiXuPai, 1);
        src.ModifyBuffValue(BuffType.UsedCardCount, 1);
        src.RemoveBuff(BuffType.AfterCardAciton);
    }

    /// <summary>
    /// 忠实 <c>BattleCharacter.isStopLianYun</c>：这张牌**是不是要中断[连运]**。
    /// 云剑牌不中断；身上有[云海]则转一层连运（并结算仙命 97/380）也不中断；
    /// 天赋14 / 「共鸣10 + 灵剑」也不中断；其余一律中断（调用方据此 RemoveBuff(LianYun)）。
    /// </summary>
    private static bool StopLianYun(Combatant src, int cardId)
    {
        if (CardTypes.IsYunJian(src, cardId)) return false;
        if (src.HasBuff(BuffType.YunHai))
        {
            src.ModifyBuffValue(BuffType.LianYun, 1);
            src.ModifyBuffValue(BuffType.YunHai, -1);
            if (src.HasFateStrategy(97)) src.ModifyAnima(1);
            if (src.HasFateStrategy(380)) src.ModifyDef(src.Config.FateOtherParams(380).At(0));
            return false;
        }
        if (src.HasTalent(14)) return false;
        if (src.IsTalentResonanceEffective(10) && CardTypes.IsLingJian(src, cardId)) return false;
        return true;
    }

    /// <summary>忠实 <c>BattleCharacter.IsJian</c>：牌名含「剑」，但 19 号牌带天赋 30096 时不算。</summary>
    private static bool IsJianCard(Combatant src, int cardId)
        => src.Config.CardName(cardId).Contains("剑") && (cardId != 19 || !src.HasTalent(30096));

    /// <summary>牌名是否是五行牌（原码用 5 个 `name.Contains(...)` 的 Any）。</summary>
    private static bool IsWuXingCardName(string name)
        => name.Contains("金灵") || name.Contains("水灵") || name.Contains("木灵")
           || name.Contains("火灵") || name.Contains("土灵");
}
