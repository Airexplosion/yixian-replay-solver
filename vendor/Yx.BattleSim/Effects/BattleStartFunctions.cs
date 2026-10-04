using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 开战编排：忠实移植 <c>BattleCharacter.OnBattleStarted</c>（原码 3546，<c>__CG_OnBattleStarted_d__40</c>）。
///
/// ⚠ 原码是**先手方完整跑完整个 OnBattleStarted，再轮到后手方**（BattleExecuter 15361–15362），
///   不是「双方各跑刻印、再各跑仙命、再各跑[开局]」。早先 sim 按后者分阶段交替，且阶段顺序也反了。
/// ⚠ 单方内部的顺序照 goto 链（= IL 偏移序）：
///   头部（星位布置 / 三条初始化 / 死战之志 / 角色 4000005 架势 / 天赋 259）→ 天赋开战循环（IL_03a4）
///   → 天赋 198 · 仙命 338 的卡 215（IL_1e9c）→ 共鸣（IL_1eb2）→ **仙命开战**（IL_2776）
///   → 上回合永久 buff（草药 / 契约 / 开战升级…，IL_27d2 起）→ **逐格[开局]**（IL_4a52）
///   → 卡 348 / 378 / 1030076 的开战计数 → **刻印开战**（IL_4a52 末）→ 远古（未移植）。
/// 对局数据（上一轮手牌 / 命元 / 永久 buff、天赋自带数据、共鸣永久标记…）由 <see cref="SideInput"/> 喂入（覆盖账 83）。
///   （上一轮手牌 / 已用牌）、共鸣 8 / 21 / 35 与「逆境契约强化」（上一轮 life）、远古 YuanGuManager。
/// </summary>
internal static class BattleStartFunctions
{
    public static void OnBattleStarted(Combatant c)
    {
        var cfg = c.Config;
        var foe = c.Opponent;

        // ── 头部 ──
        int szzz = c.GetLastRoundPermanentBuffValue(BuffType.SiZhanZhiZhi);
        if (szzz > 0) c.ModifyBuffValue(BuffType.SiZhanZhiZhi, szzz);
        CardOpeningFunctions.OnBattleStartHead(c);
        // 角色 4000005：开战带架势（仙命 335 → 棍，否则拳）。
        if (c.CharacterId == 4000005)
        {
            if (c.HasFateStrategy(335)) c.ModifyBuffValue(BuffType.GunJiaShi, 1);
            else c.ModifyBuffValue(BuffType.QuanJiaShi, 1);
        }
        // 天赋 259：升级第一张「稀有度 0、可升级、不是 0 号」的牌。
        if (c.HasTalent(259))
        {
            for (int j = 0; j < c.Board.Count; j++)
            {
                int id = c.Board[j].Id;
                if (cfg.CardRarity(id) == 0 && cfg.CanUpgrade(id) && id != 0)
                {
                    Upgrade(c, j);
                    break;
                }
            }
        }

        // ── 天赋开战循环（按 talent % 10000 分派）──
        if (c.Talents is { Count: > 0 } talents)
            foreach (int t in talents) TalentOnBattleStart(c, t);

        // ── 天赋 198 / 仙命 338：卡 215 两侧空格填 216 / 217，余下的次数升级 215 本身 ──
        if (c.HasTalent(198) || c.HasFateStrategy(338))
        {
            for (int i = 0; i < c.Board.Count; i++)
            {
                if (CardTypes.BaseId(c.Board[i].Id) != 215) continue;
                int upgrade = 2;
                int prev = GridFunctions.GetPreviousGrid(c, i);
                if (CardTypes.BaseId(c.Board[prev].Id) == 0) { c.ReplaceCard(prev, cfg.BuildCard(216)); upgrade--; }
                int next = GridFunctions.GetNextGrid(c, i);
                if (CardTypes.BaseId(c.Board[next].Id) == 0) { c.ReplaceCard(next, cfg.BuildCard(217)); upgrade--; }
                for (int j = 0; j < upgrade; j++)
                    if (cfg.CanUpgrade(c.Board[i].Id)) Upgrade(c, i);
            }
        }

        // ── 共鸣（IL_1eb2）──
        ResonanceOnBattleStart(c);

        // ── 仙命开战（IL_2776）──
        FateStrategyFunctions.OnBattleStart(c);

        // ── 上回合永久 buff（IL_27d2 起；键就是 BuffType 的整数值）──
        LastRoundPermanentOnBattleStart(c);

        // ── 逐格[开局]（IL_4a52）──
        CardOpeningFunctions.TriggerAll(c);

        // ── 卡 348 / 378（双方牌组里**原档**张数之和 × o[0]）/ 1030076 系（自己的）开战计数 ──
        // ⚠ 原码用 CheckCardInDeck(整 id) —— 只数 0 档的 348 / 378，照抄。早先 sim 整段缺失（施加点没有，只有读取点）。
        int n348 = CountExact(c, 348) + CountExact(foe, 348);
        if (n348 > 0) c.ModifyBuffValue(BuffType.MengKunXian, n348 * cfg.CardOtherParams(348).At(0));
        int n378 = CountExact(c, 378) + CountExact(foe, 378);
        if (n378 > 0) c.ModifyBuffValue(BuffType.JiaLingQiJianBan, n378 * cfg.CardOtherParams(378).At(0));
        int nKj = CountExact(c, 1030076) + CountExact(c, 1040076);
        if (nKj > 0) c.ModifyBuffValue(BuffType.ShengJiXiaCiKuangJian, nKj);

        // ── 刻印开战 ──
        KeYinCardFunctions.OnBattleStart(c);
    }

    /// <summary>天赋开战循环里单个天赋（原码 3836 的 switch）。</summary>
    private static void TalentOnBattleStart(Combatant c, int current)
    {
        var cfg = c.Config;
        var foe = c.Opponent;
        var op = cfg.TalentOtherParams(current);
        switch (current % 10000)
        {
            case 33: GridMarkFunctions.AddXingWei(c, 6); break;
            case 249:
                for (int l = 0; l < 7; l++)
                    if (!GridMarkFunctions.IsXingWei(c, l)) GridMarkFunctions.AddXingWei(c, l);
                break;
            case 106: GridMarkFunctions.AddXingWei(c, 6); break;
            case 13: c.ModifyAnima(op.At(0)); break;
            case 3: c.ModifyDef(op.At(0)); break;
            case 16: c.ModifyBuffValue(BuffType.JianYi, op.At(1)); break;
            case 25: c.ModifyBuffValue(BuffType.WuShiFangYu, c.HasTalent(222) ? 9 : op.At(0)); break;
            case 26: c.ModifyBuffValue(BuffType.HuTi, op.At(0)); break;
            case 30: c.ModifyBuffValue(BuffType.GuaXiang, op.At(0)); break;
            case 31: c.ModifyBuffValue(BuffType.XingLi, op.At(1)); break;
            case 103: foe.ModifyBuffValue(BuffType.BeiXingShi, op.At(0)); break;
            case 52:   // 首格（共鸣 28）或第 8 格是空格 → 放入卡 11
                if (c.HasTalentResonance(28) && c.Board.Count > 0 && CardTypes.BaseId(c.Board[0].Id) == 0)
                    c.ReplaceCard(0, cfg.BuildCard(11));
                else if (c.Board.Count >= 8 && CardTypes.BaseId(c.Board[7].Id) == 0)
                    c.ReplaceCard(7, cfg.BuildCard(11));
                break;
            case 70: c.ModifyBuffValue(BuffType.KuangJian, op.At(1)); break;
            case 82: c.ModifyBuffValue(BuffType.XiaZhangPaiJiHuoWuXing, 1); break;
            case 88: c.ModifyBuffValue(BuffType.JiHuoTuLing, 1); c.ModifyDef(op.At(0)); break;
            case 99: c.ModifyBuffValue(BuffType.JiHuoShuiLing, 1); c.ModifyAnima(op.At(0)); break;
            case 253: c.ModifyBuffValue(BuffType.JiHuoHuoLing, 1); foe.ModifyMaxHp(-op.At(0)); break;
            case 254: c.ModifyBuffValue(BuffType.JiHuoJinLing, 1); c.ModifyBuffValue(BuffType.FengRui, op.At(0)); break;
            case 255: c.ModifyBuffValue(BuffType.JiHuoMuLing, 1); c.ModifyBuffValue(BuffType.JiaGong, op.At(0)); break;
            case 110:   // x0109 各档：激活一种五行（档位 → 金 / 水 / 木 / 火 / 土）
                if (c.HasTalent(10109)) c.ModifyBuffValue(BuffType.JiHuoJinLing, 1);
                if (c.HasTalent(20109)) c.ModifyBuffValue(BuffType.JiHuoShuiLing, 1);
                if (c.HasTalent(30109)) c.ModifyBuffValue(BuffType.JiHuoMuLing, 1);
                if (c.HasTalent(40109)) c.ModifyBuffValue(BuffType.JiHuoHuoLing, 1);
                if (c.HasTalent(50109)) c.ModifyBuffValue(BuffType.JiHuoTuLing, 1);
                break;
            case 127: c.ModifyBuffValue(BuffType.XiangShengXiangCheng, op.At(1)); break;
            case 134: c.ModifyBuffValue(BuffType.HuTi, 1); break;
            case 139: c.ModifyBuffValue(BuffType.YanHunGuiFu, 1); break;
            case 140:
                c.ModifyBuffValue(BuffType.JiRanZhouYin, 1);
                if (c.IsTalentResonanceEffective(129)) c.ModifyBuffValue(BuffType.JiRanZhouYin, 1);
                break;
            case 145: c.ModifyBuffValue(BuffType.QiShi, op.At(0)); c.ModifyBuffValue(BuffType.QiShiShangXian, op.At(1)); break;
            case 147: c.ModifyBuffValue(BuffType.ShenFa, op.At(0)); break;
            case 150: c.ModifyBuffValue(BuffType.NeiShang, op.At(0)); c.ModifyBuffValue(BuffType.HuiFu, op.At(1)); break;
            case 171: c.ModifyBuffValue(BuffType.JiaGong, op.At(1)); c.ModifyBuffValue(BuffType.WaiShang, op.At(1)); break;
            case 176:
            {
                int d = op.At(1);
                int h = d == 0 ? 0 : c.GetBuffValue(BuffType.TiPo) / d;
                if (h > 0) c.ModifyHp(h);
                break;
            }
            case 179: c.ModifyBuffValue(BuffType.Min, op.At(1)); break;
            case 183:
            {
                int n = op.At(0);
                if (c.HasFateStrategy(166)) n++;
                c.ModifyTiPo(n);
                break;
            }
            case 125:
                if (c.HasTalentResonance(127))
                {
                    int d = cfg.ResonanceOtherParams(127).At(0);
                    int n = d == 0 ? 0 : c.Hp / d;
                    if (c.IsTalentResonanceEffective(127)) n *= 2;
                    c.ModifyMaxHp(n);
                    c.ModifyHp(n);
                }
                // 先天满元：按此刻生命判门（共鸣 127 加血之后），优先升级一张
                // 基础档的专属牌；没有专属牌时再升级第一张其他可升级的基础档牌。
                if (c.Hp >= op.At(0))
                {
                    int preferred = -1;
                    for (int i = 0; i < c.Board.Count; i++)
                    {
                        int id = c.Board[i].Id;
                        if (id is 38 or 18 or 21 or 135 or 426
                            && cfg.CardRarity(id) == 0 && cfg.CanUpgrade(id))
                        {
                            preferred = i;
                            break;
                        }
                    }
                    if (preferred >= 0) Upgrade(c, preferred);
                    else UpgradeWhere(c, 1, id => cfg.CardRarity(id) == 0);
                }
                break;
            // 天赋 199（五行玉屏）：私有天赋数据里这个天赋选的第一张牌，按牌名激活五行；仙命 309 再激活第二张（原码 3924）。
            case 199:
                if (c.PrivateTalentDatas.TryGetValue(current, out var yp) && yp.Count > 0)
                {
                    WuXingFunctions.ActiveWuXingInName(c, cfg.CardName(yp[0]));
                    if (c.HasFateStrategy(309) && yp.Count > 1) WuXingFunctions.ActiveWuXingInName(c, cfg.CardName(yp[1]));
                }
                break;
            case 203: c.ModifyBuffValue(BuffType.JiHuoMuLing, 1); break;
            // 天赋 220：上一轮手牌 + 已用牌（= 盘面）里的云剑数 / 2 回灵气，上限 o[0]（天赋 222 时为 5）（原码 IL_1a4a）。
            case 220:
            {
                int yun = 0;
                foreach (int id in c.LastRoundHandCards) if (CardTypes.IsYunJian(c, id)) yun++;
                for (int j = 0; j < c.Board.Count; j++) if (CardTypes.IsYunJian(c, c.Board[j].Id)) yun++;
                int n = yun / 2;
                int cap = c.HasTalent(222) ? 5 : op.At(0);
                if (n > cap) n = cap;
                if (n > 0) c.ModifyAnima(n);
                break;
            }
            case 236:
            {
                int d = op.At(0);
                int n = d == 0 ? 0 : c.GetLastRoundPermanentBuffValue(BuffType.TianJiShi) / d;
                if (n > 0) c.ModifyBuffValue(BuffType.JiaGong, n);
                break;
            }
            case 237:
            {
                int d = op.At(0);
                int n = d == 0 ? 0 : c.GetLastRoundPermanentBuffValue(BuffType.TianJiShi) / d;
                if (n > 0) c.ModifyBuffValue(BuffType.HuTi, n);
                break;
            }
            case 248: c.ModifyBuffValue(BuffType.LianYun, op.At(0)); c.ModifyBuffValue(BuffType.YunHai, op.At(0)); break;
        }
    }

    /// <summary>共鸣的开战效果（原码 4883 起）：58 / 20 / 24 不看生效回合；其余要「当前回合 ≥ 生效回合」。</summary>
    private static void ResonanceOnBattleStart(Combatant c)
    {
        int sel = c.SelectedResonance;
        if (sel <= 0) return;
        var cfg = c.Config;
        var op = cfg.ResonanceOtherParams(sel);
        int effectRound = cfg.ResonanceGate(sel).EffectRound;
        int round = c.CurrentRound;
        if (c.HasTalentResonance(58)) c.ModifyTiPo(round < effectRound ? op.At(0) : op.At(1));
        if (c.HasTalentResonance(20)) c.ModifyMaxHp(op.At(0));
        if (c.HasTalentResonance(24)) UpgradeWhere(c, round >= effectRound ? 2 : 1, id => cfg.CardName(id).Contains("雷"));
        if (round < effectRound) return;
        var meta = cfg as JsonBattleConfig;
        switch (sel)
        {
            case 2: c.ModifyBuffValue(BuffType.LianYun, op.At(0)); break;
            // 8 / 21 / 35：上一轮命元（lastRoundData.life = SideInput.Life）≤ o[0] 才生效（原码 4920 起）。
            case 8: if (c.Life <= op.At(0)) c.ModifyDef(op.At(1)); break;
            case 21: if (c.Life <= op.At(0)) UpgradeWhereCard(c, op.At(1), card => CardTypes.LevelOf(c, card) == 2); break;   // Level.ZhuJi
            case 35:
                if (c.Life <= op.At(0))
                    foreach (string el in new[] { "木灵", "火灵", "土灵", "金灵", "水灵" })
                        UpgradeWhere(c, 1, id => cfg.CardName(id).Contains(el));
                break;
            case 9: UpgradeWhere(c, op.At(0), id => (meta?.Card(id)?.Anima ?? 0) == -1); break;
            case 26: UpgradeWhereCard(c, 1, card => cfg.CardName(card.Id).Contains("卦") && CardTypes.LevelOf(c, card) <= 4); break;
            case 31: GridMarkFunctions.AddXingWei(c, 3); GridMarkFunctions.AddXingWei(c, 4); break;
            case 38:
                if (c.HasTalent(10109)) { c.ModifyBuffValue(BuffType.JiHuoShuiLing, 1); c.ModifyBuffValue(BuffType.JiHuoMuLing, 1); }
                if (c.HasTalent(20109)) { c.ModifyBuffValue(BuffType.JiHuoMuLing, 1); c.ModifyBuffValue(BuffType.JiHuoHuoLing, 1); }
                if (c.HasTalent(30109)) { c.ModifyBuffValue(BuffType.JiHuoHuoLing, 1); c.ModifyBuffValue(BuffType.JiHuoTuLing, 1); }
                if (c.HasTalent(40109)) { c.ModifyBuffValue(BuffType.JiHuoTuLing, 1); c.ModifyBuffValue(BuffType.JiHuoJinLing, 1); }
                if (c.HasTalent(50109)) { c.ModifyBuffValue(BuffType.JiHuoJinLing, 1); c.ModifyBuffValue(BuffType.JiHuoShuiLing, 1); }
                break;
            case 39:
                UpgradeWhere(c, op.At(0), id =>
                {
                    int b = CardTypes.BaseId(id);
                    return b == 7000016 || b == 7000029 || b == 7000036 || b == 7000031 || b == 7000023;
                });
                break;
            case 40: UpgradeWhere(c, 1, id => cfg.CardName(id).Contains("水灵")); break;
            case 43: c.ModifyBuffValue(BuffType.JiHuoMuLing, 1); break;
            case 45: c.ModifyBuffValue(BuffType.HeBaHuang, op.At(1)); break;
            case 56: if (round >= 11) UpgradeWhere(c, 1, id => CardTypes.BaseId(id) == 73); break;
            case 66: UpgradeWhere(c, 1, id => cfg.CardRarity(id) == 1 && cfg.CardName(id).Contains("剑")); break;
            case 74: c.Opponent.ModifyHp(-op.At(0)); c.Opponent.ModifyMaxHp(-op.At(0)); break;
            case 76: c.ModifyBuffValue(BuffType.FengRui, op.At(0)); break;
            case 80: UpgradeWhere(c, 1, id => (meta?.Card(id)?.Physique ?? 0) > 0); break;
            case 92: UpgradeWhereCard(c, op.At(0), card => CardTypes.LevelOf(c, card) == 1); break;
            case 98: c.ModifyBuffValue(BuffType.JiHuoHuoLing, 1); break;
            // 99：上一轮手牌数 ≥ o[0] → 升级一张基础 id 9 的牌。
            case 99: if (c.LastRoundHandCardCount >= op.At(0)) UpgradeWhere(c, 1, id => CardTypes.BaseId(id) == 9); break;
            // 116：共鸣永久标记位（publicData.resonanceTalentFlags[116]）：第 1 位 → 加攻 2，否则第 0 位 → 加攻 1。
            case 116:
            {
                c.ResonancePermanentFlags.TryGetValue(116, out int f);
                if ((f & 2) != 0) c.ModifyBuffValue(BuffType.JiaGong, 2);
                else if ((f & 1) != 0) c.ModifyBuffValue(BuffType.JiaGong, 1);
                break;
            }
            case 122:
            {
                int v = c.GetLastRoundPermanentBuffValue(BuffType.BaoCunGuaXiang);
                if (v > 0) c.ModifyBuffValue(BuffType.GuaXiang, v);
                break;
            }
            case 128:
                UpgradeWhere(c, 1, id => CardTypes.BaseId(id) == 38);
                // 上一轮手牌里有 38 / 10038 / 20038 → 激活土灵、金灵。
                foreach (int h in c.LastRoundHandCards)
                    if (h == 38 || h == 10038 || h == 20038)
                    {
                        c.ModifyBuffValue(BuffType.JiHuoTuLing, 1);
                        c.ModifyBuffValue(BuffType.JiHuoJinLing, 1);
                        break;
                    }
                break;
            case 133: UpgradeWhere(c, op.At(0), id => cfg.CardName(id).Contains("崩拳")); break;
        }
    }

    /// <summary>上回合永久 buff 的开战效果（原码 IL_27d2 起；草药 / 契约 / 开战升级 / 开战负面…）。</summary>
    private static void LastRoundPermanentOnBattleStart(Combatant c)
    {
        var cfg = c.Config;
        var foe = c.Opponent;
        int P(BuffType b) => c.GetLastRoundPermanentBuffValue(b);

        // 草药（键 = BuffType 整数值 10008…10020、20045）。FeiXiaoLingZhi（10012）只刷图标。
        if (P(BuffType.GuiYuanCao) is int gy and > 0) { c.ModifyMaxHp(gy); c.ModifyHp(gy); }
        if (P(BuffType.JinSuiCao) is int js and > 0)
            CombatMath.ApplyDamage(c, foe, DamageInfo.Create(c, DamageType.ReflectDamage, js, skipWoundCheck: true));
        if (P(BuffType.YuGanCao) is int yg and > 0) c.ModifyBuffValue(BuffType.HuiFu, yg);
        if (P(BuffType.ShenLiCao) is int sl and > 0) c.ModifyBuffValue(BuffType.JiaGong, sl);
        if (P(BuffType.ChuanChangZiJue) is int cc and > 0) foe.ModifyBuffValue(BuffType.NeiShang, cc);
        if (P(BuffType.GuiYanCao) is int gyc and > 0) c.ModifyDef(gyc);
        if (P(BuffType.HuoSuoLan) is int hs and > 0) foe.ModifyMaxHp(-hs);
        if (P(BuffType.ShiLiCao) is int slc and > 0) foe.ModifyBuffValue(BuffType.JianGong, slc);
        if (P(BuffType.QingGanJu) is int qg and > 0) c.ModifyBuffValue(BuffType.BiXie, qg);
        if (P(BuffType.YingXiaoLingZhi) is int yx and > 0) { c.ModifyHp(-yx); c.ModifyBuffValue(BuffType.ExActionAgain, 1); }
        if (c.LastRoundPermanentBuffs.ContainsKey((BuffType)20045)) c.ModifyBuffValue(BuffType.ExActionAgain, 1);

        // 契约类。
        if (P(BuffType.SuShaQiYue) is int ss and > 0)
        {
            c.ModifyHp(-ss); c.ModifyMaxHp(-ss);
            foe.ModifyHp(-ss); foe.ModifyMaxHp(-ss);
        }
        if (P(BuffType.ShengJiQiYue) is int sj and > 0)
        {
            c.ModifyMaxHp(sj); c.ModifyHp(sj);
            foe.ModifyMaxHp(sj); foe.ModifyHp(sj);
        }
        // 保留契约·均衡：按上一轮手牌数的一半升级稀有度 0 的牌。
        if (P(BuffType.BaoLiuQiYueJunHeng) > 0)
            UpgradeWhere(c, c.LastRoundHandCardCount / 2, id => cfg.CardRarity(id) == 0);
        if (P(BuffType.ZhanDouKaiShiShengJiPai) is int kp and > 0) UpgradeWhere(c, kp, id => cfg.CardRarity(id) == 0);
        if (P(BuffType.KaiJvShengJiYiJiPai) > 0) UpgradeWhere(c, 1, id => cfg.CardRarity(id) == 0);
        if (P(BuffType.ZhanDouShengJiErJiPai) is int ej and > 0) UpgradeWhere(c, ej, id => cfg.CardRarity(id) == 1);
        if (P(BuffType.ZhanDouKaiShiMeiKai) > 0) c.ModifyBuffValue(BuffType.MeiKaiErDu, 1);
        if (P(BuffType.HuTiInRoundX) is int ht and > 0) c.ModifyBuffValue(BuffType.HuTiInRoundXJiShu, ht);
        if (P(BuffType.HuaLongInRoundX) is int hl and > 0) c.ModifyBuffValue(BuffType.HuaLongInRoundXJiShu, hl);
        if (P(BuffType.JiangJiInRoundX) is int jj and > 0) c.ModifyBuffValue(BuffType.JiangJiInRoundXJiShu, jj);
        if (P(BuffType.ZhanDouKaiShiXuRuo) is int xr and > 0) c.ModifyBuffValue(BuffType.XuRuo, xr);
        if (P(BuffType.ZhanDouKaiShiNeiShang) is int ns and > 0) c.ModifyBuffValue(BuffType.NeiShang, ns);
        if (P(BuffType.ZhanDouKaiShiWaiShang) is int ws and > 0) c.ModifyBuffValue(BuffType.WaiShang, ws);
        if (P(BuffType.ZhanDouKaiShiPoZhan) is int pz and > 0) c.ModifyBuffValue(BuffType.PoZhan, pz);
        // 逆境契约·强化：己方上一轮命元 < 对手上一轮命元 → 按永久值依次升级可升级的牌（原码 5202 / IL_40cf）。
        if (P(BuffType.NiJingQiYueQiangHua) is int nj and > 0 && c.Life < foe.Life)
            UpgradeWhere(c, nj, _ => true);
        if (P(BuffType.ShenLiQiYue) is int slq and > 0)
        {
            c.ModifyBuffValue(BuffType.JiaGong, slq);
            foe.ModifyBuffValue(BuffType.JiaGong, slq);
        }
        if (P(BuffType.MengJinZhiJuan) is int mj and > 0) c.ModifyHp(-mj);
        if (P(BuffType.ZhanDouKaiShiJianShengMing) is int jsm and > 0) c.ModifyHp(-jsm);
        if (P(BuffType.ZhanDouKaiShiJiaShengMing) is int zsm and > 0) { c.ModifyMaxHp(zsm); c.ModifyHp(zsm); }
        if (P(BuffType.AiCao) is int ac and > 0) c.ModifyHp(ac);
    }

    /// <summary>原码 UpgradeCardInBattleDeck(count, cond)：按格序升级前 count 张「满足条件且可升级」的牌。</summary>
    private static void UpgradeWhere(Combatant c, int count, Func<int, bool> cond)
    {
        for (int i = 0; i < c.Board.Count && count > 0; i++)
        {
            int id = c.Board[i].Id;
            if (cond(id) && c.Config.CanUpgrade(id))
            {
                Upgrade(c, i);
                count--;
            }
        }
    }

    /// <summary>
    /// 同 <see cref="UpgradeWhere"/>，但条件按**牌对象**判 —— 境界会被卡 19 那类「按天赋改牌面」的效果改写，
    /// 只看 id 会读成卡表的静态境界（卡 19 静态是炼气，带天赋后是筑基/金丹/元婴/化神）。
    /// </summary>
    private static void UpgradeWhereCard(Combatant c, int count, Func<BattleCard, bool> cond)
    {
        for (int i = 0; i < c.Board.Count && count > 0; i++)
        {
            if (cond(c.Board[i]) && c.Config.CanUpgrade(c.Board[i].Id))
            {
                Upgrade(c, i);
                count--;
            }
        }
    }

    private static void Upgrade(Combatant c, int grid)
        => c.ReplaceCard(grid, c.Config.BuildCard(c.Board[grid].Id + 10000));

    private static int CountExact(Combatant c, int id)
    {
        int n = 0;
        foreach (var b in c.Board) if (b.Id == id) n++;
        return n;
    }
}
