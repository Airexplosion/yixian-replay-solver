using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 回合 tick（批次 F）：忠实移植 <c>BattleCharacter.OnTurnStarted</c> / <c>OnTurnEnded</c>
/// （原码各在异步状态机里，本文件按**执行顺序**线性搬过来；剥掉 await/浮字/特效/音效与
/// 只服务于视觉节奏的 <c>needDelay</c> 守卫）。
///
/// 调用点：回合开始时对当前方调 <see cref="OnTurnStarted"/>，出牌循环结束后调 <see cref="OnTurnEnded"/>
/// （对应 Execute 里的 `await firstCharacter.OnTurnStarted()` / `OnTurnEnded()`）。
///
/// ⚠ 两处**表现层不可移植**、按最可能的分支实现，已在对应位置标注：
///   · 刻印·调息的「位置/朝向」守卫（Vector2.Distance 与 transform.position）——sim 无坐标；
///   · `needDelay` 这类只控制 await 时长的标志。
/// </summary>
public static class TurnTickFunctions
{
    /// <summary>防御每回合的默认衰减率（原码 6126 `num2 = 0.5f;`）。见 OnTurnStarted 里的那段注释。</summary>
    private const float DefaultDefDecay = 0.5f;

    /// <summary>回合开始：逐 buff 结算（BattleCharacter.OnTurnStarted）。</summary>
    public static void OnTurnStarted(Combatant c)
    {
        var foe = c.Opponent;

        // ⚠ **顺序照原码 `__CG_OnTurnStarted_d__46` 的 goto 链**（= IL 偏移序 = 源码序），不是反编译的文件序。
        //   反编译把各 label 段打散了：文件里「内伤取值」排在很前面（6299），实际却在链的倒数第三段（IL_1a4c）——
        //   断肠曲 / 民心入玄 / 回血 / 女娲石 等都在它**之前**。早先按文件序搬，于是断肠曲当回合加的内伤要到下回合才结算。
        //   链：IL_0105 → 064f 幻音曲 → 0701 断肠曲 → 0794 民心入玄 → 08f2 谢骨阵 → 09e4 破空雕 → 0a8a 走马观花+吞天吃炎兽
        //       → 0bab 地玄龟 → 0c42 下回合加防 → 0cd9 聚灵心法 → 0dab 聚灵阵 → 0e59 蚀灵阵 → 0f29 聚灵椒珠 → 0fbc 梦飞云丹
        //       → 10ed 灵剑心法 → 1180 天罡聚力阵 → 1254 胖仙狸 → 12ef 回复 → 13a1 女娲石 → 1448 梦大还丹 → 15aa 无极卦盘
        //       → 163d 天元无极盘 → 16e2 海潮 → 1776 水势临时加倍 → 1863 回合开始加防 → 18fb 回合开始加云海 → 19b9 万物生长
        //       → 1a4c 风雪之树 + 内伤取值 → 1bd1 内伤结算 + 共鸣83 → 1d08 噬虚灵手 → （switch 外）N 回合后生效 + 刻印。
        //   （核对工具：scratchpad 的 chain_walk.py 沿「段尾顶层 goto」走一遍即得此序。）

        // ── IL_0105 ──
        if (c.HasBuff(BuffType.XiaHuiHeKaiShiQianBuZaiSunShiShengMing))
            c.ModifyBuffValue(BuffType.XiaHuiHeKaiShiQianBuZaiSunShiShengMing, -1);
        // 「本回合已触发」一类标记：**无条件**清（早先误套在上一条 if 里 → 没有那个 buff 时永不重置）。
        if (c.HasBuff(BuffType.WuXingJuLingYiChuFa)) c.RemoveBuff(BuffType.WuXingJuLingYiChuFa);
        if (c.HasBuff(BuffType.ChanXinJuLingYiChuFa)) c.RemoveBuff(BuffType.ChanXinJuLingYiChuFa);
        if (c.HasBuff(BuffType.ReXueHuaQiYiChuFa)) c.RemoveBuff(BuffType.ReXueHuaQiYiChuFa);
        if (c.HasBuff(BuffType.LingQiBengYongQuanShengXiao)) c.RemoveBuff(BuffType.LingQiBengYongQuanShengXiao);
        if (c.HasBuff(BuffType.LingGuaZiYanYiChuFa)) c.RemoveBuff(BuffType.LingGuaZiYanYiChuFa);
        if (c.HasBuff(BuffType.HuiHeJiaShengMing)) c.RemoveBuff(BuffType.HuiHeJiaShengMing);
        if (c.HasBuff(BuffType.TempHuTi))
        {
            c.ModifyBuffValue(BuffType.HuTi, -c.GetBuffValue(BuffType.TempHuTi));
            c.RemoveBuff(BuffType.TempHuTi);
        }

        c.SetBuffValue(BuffType.ThisTurnStartHp, c.Hp);

        // 朱雀之雷：残血时消耗并得护体。阈值取**仙命 344** 的 o[0]（=20；早先误取仙命 82 的参数 =1）。
        if (c.HasBuff(BuffType.ZhuQueZhiLei) && c.Hp <= c.Config.FateOtherParams(344).At(0))
        {
            c.RemoveBuff(BuffType.ZhuQueZhiLei);
            c.ModifyBuffValue(BuffType.HuTi, 1);
        }
        // 共鸣72：残血时一次性回血（按上限百分比）。
        if (c.IsTalentResonanceEffective(72) && !c.CheckTalentResonanceTempFlag(72))
        {
            int pct = c.Config.ResonanceOtherParams(72).At(0);
            if (c.Hp < (long)c.MaxHp * pct / 100)
            {
                c.SetTalentResonanceTempFlag(72, true);
                c.ModifyHp((int)((long)c.MaxHp * c.Config.ResonanceOtherParams(72).At(1) / 100));
            }
        }
        // 对手的灵龟迷踪步 / 梦灵玄：回合开始置「下次生效」标记；对手带刻印 83 则抵消一层（原码不嵌套在迷踪步里）。
        if (foe.HasBuff(BuffType.LingGuiMiZongBu)) foe.ModifyBuffValue(BuffType.LingGuiMiZongBuShengXiao, 1);
        if (foe.HasBuff(BuffType.MengLingXuan)) foe.ModifyBuffValue(BuffType.MengLingXuanBuChuFa, 1);
        if (foe.Subs.HasKeYinType(foe, 83)) foe.ModifyBuffValue(BuffType.LingGuiMiZongBuShengXiao, -1);

        if (c.HasBuff(BuffType.XiaCiChaTi))
        {
            c.ModifyBuffValue(BuffType.ChaTi, c.GetBuffValue(BuffType.XiaCiChaTi));
            c.RemoveBuff(BuffType.XiaCiChaTi);
        }

        // 「回合开始即过期」的一批 buff：每回合减 1。
        Dec(c, BuffType.WanShiRuYi);
        Dec(c, BuffType.ShunYing);
        Dec(c, BuffType.NiShi);
        Dec(c, BuffType.TieGu);
        Dec(c, BuffType.QianDun);
        Dec(c, BuffType.ChaiZhao);
        if (c.HasBuff(BuffType.HuanQiChenJianShang))
        {
            c.ModifyBuffValue(BuffType.HuanQiChenJianShang, -1);
            // 幻气尘减伤到期 → 转成加攻。
            if (!c.HasBuff(BuffType.HuanQiChenJianShang))
                c.ModifyBuffValue(BuffType.HuanQiChenJiaGong, c.Config.CardOtherParams(328).At(1));
        }
        Dec(c, BuffType.MengFanZhenJianGong);

        // ── 防御每回合衰减 ──（原码 `OnTurnStarted` 6126 起，同在 IL_0105 段尾）
        // 🔑 关键在**结构**：原码先无条件 `num2 = 0.5f;`（6126），后面三个 if 只是**覆盖**这个默认值；
        //    `if (def > 0 && num2 > 0f) ModifyDef(-ceil(def * num2))` 是**无条件**跑的。
        //    早先 sim 把整段套进了 `if (HasBuff(DiXuanGui)) { float rate = 0.2f; … }`
        //    → **没有地玄龟就完全不衰减**，防御跨回合无限累积、普攻被吃光，
        //    这正是「sim 的 L 太耐打」那一大片（159/163/81/48/183/47/1000067…）的根因。
        //    实机现象与 yisim 的 `do_def_decay`（默认 `def_decay = 50`）完全一致 —— 是查 yisim 才定位到的。
        float rate = DefaultDefDecay;
        if (c.HasBuff(BuffType.DiXuanGui)) rate = 0.2f;                                   // 地玄龟 → 20%
        if (c.HasBuff(BuffType.ShuiYueJianZhen)) { c.ModifyBuffValue(BuffType.ShuiYueJianZhen, -1); rate = 0f; }  // 水月剑阵 → 0%
        if (c.HasTalentResonance(6) && c.Def <= c.Config.ResonanceOtherParams(6).At(0)) rate = 0f;                // 共鸣6 → 0%
        if (c.HasBuff(BuffType.MengDuanYa)) c.ModifyBuffValue(BuffType.MengDuanYa, -1);   // 梦断崖只消耗，不改倍率
        if (c.Def > 0 && rate > 0f)
        {
            int cut = (int)MathF.Ceiling(c.Def * rate);
            c.ModifyDef(-cut);
            // 断防转生（刻印83 有则回血；坚板防转生 再补一次）。
            if (c.Subs.HasCardInDeck(c, 10030083) || c.Subs.HasCardInDeck(c, 10040083)) c.ModifyHp(cut);
            if (c.GetBuffValue(BuffType.JianBanFangZhuanShengMing) > 0)
            {
                c.ModifyBuffValue(BuffType.JianBanFangZhuanShengMing, -1);
                c.ModifyHp(cut);
            }
        }

        // ── IL_064f 幻音曲：扣血转防 ──
        if (c.HasBuff(BuffType.HuanYinQu))
        {
            c.ModifyHp(-c.GetBuffValue(BuffType.HuanYinQu));
            c.ModifyDef(c.GetBuffValue(BuffType.HuanYinQu));
        }
        // ── IL_0701 断肠曲：加内伤（当回合就在下面 IL_1bd1 结算）──
        if (c.HasBuff(BuffType.DuanChangQu)) c.ModifyBuffValue(BuffType.NeiShang, c.GetBuffValue(BuffType.DuanChangQu));
        // ── IL_0794 民心入玄（卡 81）：层数加到 内伤 / 恢复 上 ──
        // yisim 的 `sim_turn` 也是 `do_meditation_of_xuan()`（加层）→ `do_regen()`（回血）→ `do_internal_injury()`（扣血）。
        if (c.HasBuff(BuffType.MinXinRuXuan))
        {
            int bv = c.GetBuffValue(BuffType.MinXinRuXuan);
            c.ModifyBuffValue(BuffType.NeiShang, bv);
            c.ModifyBuffValue(BuffType.HuiFu, bv);
        }
        // ── IL_08f2 谢骨阵：给对手挂内伤、自身消耗 ──
        if (c.HasBuff(BuffType.XieGuZhen))
        {
            foe.ModifyBuffValue(BuffType.NeiShang, c.Config.CardOtherParams(8000005).At(1));
            c.ModifyBuffValue(BuffType.XieGuZhen, -1);
        }
        // ── IL_09e4 破空雕（99000200）：每回合向对方造成层数点伤害（真伤，无扣层）──
        if (c.HasBuff(BuffType.PoKongDiao))
        {
            CombatMath.ApplyDamage(c, foe, DamageInfo.Create(
                c, DamageType.Damage, c.GetBuffValue(BuffType.PoKongDiao), skipWoundCheck: true));
        }
        // ── IL_0a8a 走马观花（卡 356）：回合开始**掷一次**（GetNextParam，玩家参数队列，非真随机），二选一 ──
        // ⚠ 队列是**共享**的、按消费顺序前移，所以这段的**位置**影响结果 —— 现在与原码同位。
        if (c.HasBuff(BuffType.ZouMaGuanHuaZiShenShouShangHai))
        {
            switch (c.NextParam(ParamRequest.Choice(ParamSite.ZouMaGuanHua, 2)))
            {
                case 0:
                    CombatMath.ApplyDamage(c, foe, DamageInfo.Create(c, DamageType.Damage,
                        c.GetBuffValue(BuffType.ZouMaGuanHuaDuiFangShouShangHai), skipWoundCheck: true));
                    break;
                case 1:
                    CombatMath.ApplyDamage(c, c, DamageInfo.Create(c, DamageType.Damage,
                        c.GetBuffValue(BuffType.ZouMaGuanHuaZiShenShouShangHai), skipWoundCheck: true));
                    break;
            }
        }
        // 吞天吃炎兽：抽对手血（同在 IL_0a8a 段）。
        if (c.HasBuff(BuffType.TunTianChiYanShou))
        {
            foe.ModifyHp(-c.GetBuffValue(BuffType.TunTianChiYanShou));
            c.ModifyHp(c.GetBuffValue(BuffType.TunTianChiYanShou));
        }
        // ── IL_0bab 地玄龟：加防 ──
        if (c.HasBuff(BuffType.DiXuanGui)) c.ModifyDef(c.GetBuffValue(BuffType.DiXuanGui));
        // ── IL_0c42 下回合加防 → 本回合生效并消耗 ──
        if (c.HasBuff(BuffType.XiaHuiHeJiaFang))
        {
            c.ModifyDef(c.GetBuffValue(BuffType.XiaHuiHeJiaFang));
            c.RemoveBuff(BuffType.XiaHuiHeJiaFang);
        }
        // ── IL_0cd9 聚灵心法：按（半数的）灵气管回灵气，奇数时用半点灵气缓冲 ──
        if (c.HasBuff(BuffType.JuLingXinFa))
        {
            int v = c.GetBuffValue(BuffType.JuLingXinFa);
            if (v % 2 == 1)
            {
                if (c.HasBuff(BuffType.BanDianLingQi)) { v++; c.RemoveBuff(BuffType.BanDianLingQi); }
                else { v--; c.ModifyBuffValue(BuffType.BanDianLingQi, 1); }
            }
            c.ModifyAnima(v / 2);
        }
        // ── IL_0dab 聚灵阵 / IL_0e59 蚀灵阵 / IL_0f29 聚灵椒珠 ──
        if (c.HasBuff(BuffType.JuLinZhen))
        {
            c.ModifyAnima(c.Config.CardOtherParams(8000007).At(1));
            c.ModifyBuffValue(BuffType.JuLinZhen, -1);
        }
        if (c.HasBuff(BuffType.ShiLingZhen))
        {
            int p = c.Config.CardOtherParams(208).At(1);
            c.ModifyAnima(p);
            c.ModifyBuffValue(BuffType.QiShi, p);
            c.ModifyBuffValue(BuffType.ShiLingZhen, -1);
        }
        if (c.HasBuff(BuffType.JuLingJiaoZhu)) c.ModifyAnima(c.GetBuffValue(BuffType.JuLingJiaoZhu));
        // ── IL_0fbc 梦飞云丹：对手有防则挂无视防御，否则回灵气 ──
        if (c.HasBuff(BuffType.MengFeiYunDan))
        {
            if (foe.Def > 0) c.ModifyBuffValue(BuffType.WuShiFangYu, c.GetBuffValue(BuffType.MengFeiYunDan));
            else c.ModifyAnima(c.GetBuffValue(BuffType.MengFeiYunDan));
        }
        // ── IL_10ed 灵剑心法 / IL_1180 天罡聚力阵 ──
        if (c.HasBuff(BuffType.LingJianXinFa)) c.ModifyBuffValue(BuffType.JianYi, c.GetBuffValue(BuffType.LingJianXinFa));
        if (c.HasBuff(BuffType.TianGangJuLiZhen))
        {
            c.ModifyBuffValue(BuffType.JiaGong, 1);
            c.ModifyBuffValue(BuffType.TianGangJuLiZhen, -1);
        }
        // ── IL_1254 胖仙狸（99000208）：[持续] 每回合 +生命 ──
        if (c.HasBuff(BuffType.PangXianLi)) c.ModifyHp(c.GetBuffValue(BuffType.PangXianLi));
        // ── IL_12ef 回复（功名铭心入玄 翻倍）──
        if (c.HasBuff(BuffType.HuiFu))
        {
            int heal = c.GetBuffValue(BuffType.HuiFu);
            if (c.HasBuff(BuffType.GongMingMingXinRuXuan)) heal *= 2;
            c.ModifyHp(heal);
        }
        // ── IL_13a1 女娲石 ──
        if (c.HasBuff(BuffType.NvWaShi))
        {
            c.ModifyHp(c.Hp);
            c.ModifyBuffValue(BuffType.NvWaShi, -1);
        }
        // ── IL_1448 / IL_1504 梦大还丹：自身上限 / 生命低于对手时补 ──
        if (c.HasBuff(BuffType.MengDaHuanDan))
        {
            int add = c.GetBuffValue(BuffType.MengDaHuanDan);
            if (c.MaxHp < foe.MaxHp) c.ModifyMaxHp(add);
            if (c.Hp < foe.Hp) c.ModifyHp(add);
        }
        // ── IL_15aa 无极卦盘 / IL_163d 天元无极盘 / IL_16e2 海潮 ──
        if (c.HasBuff(BuffType.WuJiGuaPan)) c.ModifyBuffValue(BuffType.GuaXiang, c.GetBuffValue(BuffType.WuJiGuaPan));
        if (c.HasBuff(BuffType.TianYuanWuJiPan))
        {
            c.ModifyBuffValue(BuffType.GuaXiang, c.GetBuffValue(BuffType.TianYuanWuJiPan));
            c.ModifyBuffValue(BuffType.XingLi, c.GetBuffValue(BuffType.TianYuanWuJiPan));
        }
        if (c.HasBuff(BuffType.HaiChao)) c.ModifyBuffValue(BuffType.ShuiShi, c.GetBuffValue(BuffType.HaiChao));
        // ── IL_1776 水势临时加倍：按当前水势/灵气翻倍并记录，回合末回收 ──
        if (c.HasBuff(BuffType.ShuiShiLinShiJiaBei))
        {
            int mult = c.GetBuffValue(BuffType.ShuiShiLinShiJiaBei);
            int delta = c.GetBuffValue(BuffType.ShuiShi) * (mult - 1);
            int animaGain = c.Anima * mult;
            c.ModifyBuffValue(BuffType.ShuiShi, delta);
            c.ModifyBuffValue(BuffType.JiLuLinShiShuiShi, delta);
            if (animaGain > 0)
            {
                c.ModifyAnima(animaGain);
                c.ModifyBuffValue(BuffType.JiLuLinShiLingQi, animaGain);
            }
            c.RemoveBuff(BuffType.ShuiShiLinShiJiaBei);
        }
        // ── IL_1863 回合开始加防 / IL_18fb 回合开始加云海（封顶）/ IL_19b9 万物生长 ──
        if (c.HasBuff(BuffType.HuiHeKaiSHiJiaFang)) c.ModifyDef(c.GetBuffValue(BuffType.HuiHeKaiSHiJiaFang));
        if (c.HasBuff(BuffType.HuiHeKaiSHiJiaYunHai)
            && c.GetBuffValue(BuffType.YunHai) < c.Config.CardOtherParams(1040083).At(2))
            c.ModifyBuffValue(BuffType.YunHai, c.GetBuffValue(BuffType.HuiHeKaiSHiJiaYunHai));
        if (c.HasBuff(BuffType.WanWuShengZhang))
            c.ModifyBuffValue(BuffType.JiaGong, c.GetBuffValue(BuffType.WanWuShengZhang));

        // ── IL_1a4c 风雪之树：**持有者自己**受「层数 × 自身负面状态数」点伤害 ──
        // 卡 408 把 FengXueZhiShu 挂给**对方**；原码 `defaultOpponentTarget.ApplyDamage(this, Create(this, Damage, n, skipWoundCheck: true))`
        //   —— 施加者是对手、目标是持有者。早先写成持有者打对手，方向反了。
        if (c.HasBuff(BuffType.FengXueZhiShu))
        {
            int dmg = c.GetBuffValue(BuffType.FengXueZhiShu) * c.GetDebuffCount();
            CombatMath.ApplyDamage(foe, c, DamageInfo.Create(c, DamageType.Damage, dmg, skipWoundCheck: true));
        }
        // 内伤取值（同在 IL_1a4c）：功名铭心入玄 翻倍，共鸣136+拳架势 减半，内伤多触发 加次数。
        int neiShangHp = 0, neiShangTimes = 0;
        if (c.HasBuff(BuffType.NeiShang))
        {
            neiShangHp = c.GetBuffValue(BuffType.NeiShang);
            neiShangTimes = 1;
            if (c.HasBuff(BuffType.GongMingMingXinRuXuan)) neiShangHp *= 2;
            if (c.IsTalentResonanceEffective(136) && c.HasTalent(206) && c.HasBuff(BuffType.QuanJiaShi))
                neiShangHp -= neiShangHp / 2;
            if (c.HasBuff(BuffType.NeiShangDuoChuFa))
                neiShangTimes += c.GetBuffValue(BuffType.NeiShangDuoChuFa);
        }
        // ── IL_1bd1 内伤结算（多次）+ 共鸣83：内伤转回血（封顶为本次内伤量）──
        // 两条都只在「有内伤」这条路径上（原码 IL_1a4c 无内伤直接跳 IL_1d08）。
        if (neiShangTimes > 0)
        {
            for (int i = 0; i < neiShangTimes; i++) c.ModifyHp(-neiShangHp);
            if (c.IsTalentResonanceEffective(83))
            {
                int cap = c.Config.ResonanceOtherParams(83).At(0);
                if (neiShangHp < cap) cap = neiShangHp;
                c.ModifyHp(cap, canRevive: true);
            }
        }
        // ── IL_1d08 内伤多触发清除 + 噬虚灵手：抽对手灵气 ──
        if (c.HasBuff(BuffType.NeiShangDuoChuFa)) c.RemoveBuff(BuffType.NeiShangDuoChuFa);
        if (c.HasBuff(BuffType.ShiXuLingShou))
        {
            int take = c.GetBuffValue(BuffType.ShiXuLingShou);
            if (take > foe.Anima) take = foe.Anima;
            foe.ModifyAnima(-take);
            c.ModifyAnima(take);
        }

        // ── switch 之外的收尾（原码 6945 起）：「N 回合后生效」的一批，计数归零才落地 ──
        if (c.HasBuff(BuffType.HuTiInRoundXJiShu))
        {
            c.ModifyBuffValue(BuffType.HuTiInRoundXJiShu, -1);
            if (c.GetBuffValue(BuffType.HuTiInRoundXJiShu) == 0) c.ModifyBuffValue(BuffType.HuTi, 1);
        }
        if (c.HasBuff(BuffType.HuaLongInRoundXJiShu))
        {
            c.ModifyBuffValue(BuffType.HuaLongInRoundXJiShu, -1);
            if (c.GetBuffValue(BuffType.HuaLongInRoundXJiShu) == 0) c.ModifyBuffValue(BuffType.HuaLongDianJing, 1);
        }
        if (c.HasBuff(BuffType.JiangJiInRoundXJiShu))
        {
            c.ModifyBuffValue(BuffType.JiangJiInRoundXJiShu, -1);
            if (c.GetBuffValue(BuffType.JiangJiInRoundXJiShu) == 0)
                foe.ModifyBuffValue(BuffType.XiaCiPaiJiangJi, 1);
        }

        // 刻印 138：回合开始补机关刻文。
        if (c.Subs.HasKeYinType(c, 138))
            c.ModifyBuffValue(BuffType.JiGuanKeWen, c.Subs.KeYinOtherparam(c, 138, 0));
        // 刻印 128 / 129 / 136：逐张炼化卡结算（有灵气门槛 / 消耗机关刻文）。
        // ⚠ 原码（6976 起）是**三个独立的 ForEach**：先把全部 128 过一遍，再 129，再 136 —— 不是逐张交错。
        // ⚠ 门槛都读**这张刻印自己的 o[1]**：128 是「机关刻文 < o[1]」，129 是「机关刻文 >= o[1]」。
        //   早先 128 读成全部 128 刻印的 o[0] 之和、129 读成 o[0]（机关蛇 o=[3,2]：刻纹 2 时该扣没扣，b6_88）。
        if (c.Subs.HasKeYinType(c, 128))
            foreach (int keyin in c.BattleKeYinCards)
                if (keyin % 10000 == 128 && c.Anima > 0 && c.GetBuffValue(BuffType.JiGuanKeWen) < c.Config.KeYinOtherParams(keyin).At(1))
                {
                    c.ModifyAnima(-1);
                    c.ModifyBuffValue(BuffType.JiGuanKeWen, c.Config.KeYinOtherParams(keyin).At(1));
                }
        if (c.Subs.HasKeYinType(c, 129))
            foreach (int keyin in c.BattleKeYinCards)
                if (keyin % 10000 == 129 && c.GetBuffValue(BuffType.JiGuanKeWen) >= c.Config.KeYinOtherParams(keyin).At(1))
                {
                    c.ModifyBuffValue(BuffType.JiGuanKeWen, -c.Config.KeYinOtherParams(keyin).At(1));
                    foe.ModifyBuffValue(BuffType.NeiShang, 1);
                }
        if (c.Subs.HasKeYinType(c, 136))
            foreach (int keyin in c.BattleKeYinCards)
                if (keyin % 10000 == 136 && c.GetBuffValue(BuffType.JiGuanKeWen) >= 1)
                {
                    c.ModifyBuffValue(BuffType.JiGuanKeWen, -1);
                    c.ModifyBuffValue(BuffType.QiShi, 1);
                }
    }

    private static void Dec(Combatant c, BuffType b)
    {
        if (c.HasBuff(b)) c.ModifyBuffValue(b, -1);
    }

    /// <summary>回合结束：逐 buff 结算（BattleCharacter.OnTurnEnded）。</summary>
    /// <remarks>
    /// ⚠ **顺序照原码 `__CG_OnTurnEnded_d__47` 的 goto 链**（= IL 偏移序 = 源码序），不是反编译的文件序。
    ///   链：机关灵文 → 023a 调息 → 03c2 玉灵坊 → 0478 战灵翼 → 0556 刻印127 / 天赋66 → 0760 共鸣3 → 0882 仙命84
    ///       → 09a7 共鸣63 → 0ad2 回春曲 → 0bfd 玄冥回春曲 → 0db3 回生积战坊 → 0fa2 阴雷阵 → 1121 龟甲阵 → 1291 不动金刚阵
    ///       → 143c 刻印63 / 两仪金刚阵 → 163e 琴天霹雳曲 → 17b1 万花迷魂阵 → 18c6 梦狂二 / 百毒不侵 → 1a03 水势 / 土灵阵Plus
    ///       → 1d27 临时水势回收 / 影蜘蛛 → 1ecd 散微幻 → 1f6f 刻印铸城 → 2119 粽子试炼 → 21e4 收尾（减层、清标记）。
    ///   早先按文件序搬：临时水势回收排在最前（原码在水势结算**之后**）、琴天霹雳曲 / 铸城排得过早，
    ///   另有几条整段缺失（仙命84、共鸣63、刻印63、万花迷魂阵的吸血、百毒不侵、粽子试炼的判负）。
    /// </remarks>
    public static void OnTurnEnded(Combatant c)
    {
        var foe = c.Opponent;

        // ── default：机关灵文：耗 1 灵气转机关刻文，然后清掉 ──
        if (c.HasBuff(BuffType.JiGuanLingWen))
        {
            if (c.Anima >= 1)
            {
                c.ModifyAnima(-1);
                c.ModifyBuffValue(BuffType.JiGuanKeWen, c.GetBuffValue(BuffType.JiGuanLingWen));
            }
            c.RemoveBuff(BuffType.JiGuanLingWen);
        }
        // ── IL_023a 刻印·调息：有灵气则耗 1 灵气、加层数点生命及上限；然后清掉 ──
        // ⚠ 早先与「回生积战坊」揉成了一段「先加后扣」，并错用了铸城的门 —— 原码三者是独立的三条。
        if (c.HasBuff(BuffType.KeYinTiaoXi))
        {
            int tiaoXi = c.GetBuffValue(BuffType.KeYinTiaoXi);
            if (c.Anima >= 1)
            {
                c.ModifyAnima(-1);
                c.ModifyMaxHp(tiaoXi);
                c.ModifyHp(tiaoXi);
            }
            c.RemoveBuff(BuffType.KeYinTiaoXi);
        }
        // ── IL_03c2 刻印·玉灵坊：按当前灵气加防，然后清掉 ──
        if (c.HasBuff(BuffType.KeYinYuLingFang))
        {
            int add = c.GetBuffValue(BuffType.KeYinYuLingFang) * c.Anima;
            if (add > 0) c.ModifyDef(add);
            c.RemoveBuff(BuffType.KeYinYuLingFang);
        }
        // ── IL_0478 刻印·战灵翼：花灵气换身法，扣一层 ──
        if (c.HasBuff(BuffType.KeYinZhanLingYi))
        {
            int cost = c.Config.KeYinOtherParams(50164).At(0);
            if (c.Anima >= cost)
            {
                c.ModifyAnima(-cost);
                c.ModifyBuffValue(BuffType.ShenFa, c.Config.KeYinOtherParams(50164).At(1));
            }
            c.ModifyBuffValue(BuffType.KeYinZhanLingYi, -1);
        }
        // ── IL_0556 刻印 127：消耗机关刻文换上限/回血 ──
        foreach (int keyin in c.BattleKeYinCards)
        {
            if (keyin % 10000 == 127 && c.GetBuffValue(BuffType.JiGuanKeWen) > 0)
            {
                c.ModifyBuffValue(BuffType.JiGuanKeWen, -1);
                int add = c.Config.KeYinOtherParams(keyin).At(1);
                c.ModifyMaxHp(add);
                c.ModifyHp(add);
            }
        }
        // 天赋66：本回合未攻击 → 加防（散微幻换避邪是**另一条**，见下方 IL_1ecd；早先误塞在这里）。
        if (c.HasTalent(66) && c.GetBuffValue(BuffType.BENLUNGONGJICISHU) == 0)
            c.ModifyDef(c.Config.TalentOtherParams(66).At(0));
        // ── IL_0760 共鸣3 / IL_0882 仙命84：本回合未攻击 → 加剑意 ──
        if (c.Subs.IsTalentResonanceEffective(c, 3) && c.GetBuffValue(BuffType.BENLUNGONGJICISHU) == 0)
            c.ModifyBuffValue(BuffType.JianYi, c.Config.ResonanceOtherParams(3).At(0));
        if (c.HasFateStrategy(84) && c.GetBuffValue(BuffType.BENLUNGONGJICISHU) == 0)
            c.ModifyBuffValue(BuffType.JianYi, c.Config.FateOtherParams(84).At(0));
        // ── IL_09a7 共鸣63：有防则再加防 ──
        if (c.IsTalentResonanceEffective(63) && c.Def > 0)
            c.ModifyDef(c.Config.ResonanceOtherParams(63).At(0));
        // ── IL_0ad2 回春曲（卡 5000010）：[持续] 每回合末加生命及上限 ──
        // ⚠ 与下面的玄冥回春曲是两个不同的 buff；早先只做了后者，5000010 挂上去的 HuiChunQu 无人读取。
        if (c.HasBuff(BuffType.HuiChunQu))
        {
            c.ModifyMaxHp(c.GetBuffValue(BuffType.HuiChunQu));
            c.ModifyHp(c.GetBuffValue(BuffType.HuiChunQu));
        }
        // ── IL_0bfd 玄冥回春曲：加生命及上限，再扣「回春曲减生命」（代价只在这条路径上）──
        if (c.HasBuff(BuffType.XuanMinHuiChunQu))
        {
            c.ModifyMaxHp(c.GetBuffValue(BuffType.XuanMinHuiChunQu));
            c.ModifyHp(c.GetBuffValue(BuffType.XuanMinHuiChunQu));
            c.ModifyHp(-c.GetBuffValue(BuffType.XuanMinHuiChunQuJianShengMing));
        }
        // ── IL_0db3 回生积战坊：血量上限抬到「当前血 + 层数」，回血后再扣回（净效果只剩上限）──
        if (c.HasBuff(BuffType.HuanShengJiZhanFang))
        {
            int hs = c.GetBuffValue(BuffType.HuanShengJiZhanFang);
            if (c.Hp + hs > c.MaxHp) c.ModifyMaxHp(c.Hp + hs - c.MaxHp);
            c.ModifyHp(hs);
            c.ModifyHp(-hs);
        }

        // ── 阵系列：数值是**对应那张卡的 otherParams[1] 常量**，不是 buff 自身的层数 ──
        // IL_0fa2 阴雷阵：固定伤害（卡 8000001 的 o[1]，不是掷骰），扣一层。
        if (c.HasBuff(BuffType.YinLeiZhen))
        {
            int dmg = c.Config.CardOtherParams(8000001).At(1);
            CombatMath.ApplyDamage(c, foe, DamageInfo.Create(c, DamageType.Damage, dmg, skipWoundCheck: true));
            c.ModifyBuffValue(BuffType.YinLeiZhen, -1);
        }
        // IL_1121 龟甲阵：加防（卡 8000004 的 o[1]），**扣一层**（[持续]4次；早先写成 RemoveBuff）。
        if (c.HasBuff(BuffType.GuiJiaZhen))
        {
            c.ModifyDef(c.Config.CardOtherParams(8000004).At(1));
            c.ModifyBuffValue(BuffType.GuiJiaZhen, -1);
        }
        // IL_1291 不动金刚阵（卡 8000016）：加防、扣一层；本回合未触发再次行动（无 ZaiCiXingDong）再加生命及上限。
        if (c.HasBuff(BuffType.BuDongJinGangZhen))
        {
            int addHp = c.Config.CardOtherParams(8000016).At(1);
            c.ModifyDef(addHp);
            c.ModifyBuffValue(BuffType.BuDongJinGangZhen, -1);
            if (!c.HasBuff(BuffType.ZaiCiXingDong))
            {
                c.ModifyMaxHp(addHp);
                c.ModifyHp(addHp);
            }
        }
        // ── IL_143c 刻印63：本回合未再次行动则加防 ──
        if (!c.HasBuff(BuffType.ZaiCiXingDong) && c.Subs.HasKeYinType(c, 63))
            c.ModifyDef(c.Subs.KeYinOtherparam(c, 63, 0));
        // 两仪金刚阵：加防并加血 = 灵气×o[1] + 卦象×o[2]（卡 194），扣一层；门含「有卦象 或 灵气 > 0」。
        if (c.HasBuff(BuffType.LiangYiJinGangZhen) && (c.HasBuff(BuffType.GuaXiang) || c.Anima > 0))
        {
            int liangYi = c.Anima * c.Config.CardOtherParams(194).At(1)
                        + c.GetBuffValue(BuffType.GuaXiang) * c.Config.CardOtherParams(194).At(2);
            c.ModifyDef(liangYi);
            c.ModifyHp(liangYi);
            c.ModifyBuffValue(BuffType.LiangYiJinGangZhen, -1);
        }
        // ── IL_163e 琴天霹雳曲：掷出（GetNextRandomValue）< 10 时造成层数伤害 ──
        if (c.HasBuff(BuffType.QinTianPiLiQu))
        {
            int roll = c.GetNextRandomValue(ParamRequest.Percent(ParamSite.QinTianPiLiQu));
            int bv = c.GetBuffValue(BuffType.QinTianPiLiQu);
            if (roll < 10)
                CombatMath.ApplyDamage(c, foe, DamageInfo.Create(c, DamageType.Damage, bv, skipWoundCheck: true));
        }
        // ── IL_17b1 万花迷魂阵：给对手挂 1 层减攻、扣一层；然后按对手的减攻层数**吸血** ──
        // ⚠ 后半句（IL_1883：对手掉等量血、自己回等量血）早先整段缺失。
        if (c.HasBuff(BuffType.WanHuaMiHunZhen))
        {
            foe.ModifyBuffValue(BuffType.JianGong, 1);
            c.ModifyBuffValue(BuffType.WanHuaMiHunZhen, -1);
            int jg = foe.GetBuffValue(BuffType.JianGong);
            if (jg > 0)
            {
                foe.ModifyHp(-jg);
                c.ModifyHp(jg);
            }
        }
        // ── IL_18c6 梦狂二：本回合「加生命」累计到阈值时，按层数扣血并把同量转成狂剑 ──
        if (c.HasBuff(BuffType.MengKuangEr)
            && c.GetBuffValue(BuffType.HuiHeJiaShengMing) >= c.Config.CardOtherParams(1030087).At(2))
        {
            int layers = c.GetBuffValue(BuffType.MengKuangEr);
            c.ModifyHp(-c.Config.CardOtherParams(1030087).At(2) * layers);
            c.ModifyBuffValue(BuffType.KuangJian, layers);
        }
        // 百毒不侵：回合末按自身负面状态数回血（封顶为层数）。早先缺失。
        if (c.HasBuff(BuffType.BaiDuBuQin))
        {
            int heal = c.GetDebuffCount();
            if (heal > c.GetBuffValue(BuffType.BaiDuBuQin)) heal = c.GetBuffValue(BuffType.BaiDuBuQin);
            if (heal > 0) c.ModifyHp(heal);
        }

        // ── IL_1a03 水势结算（波澜多触发一次）──
        // **水势不是攻击**：对对手的一次「不触发击伤、也不碰防御」的伤害，且**不消耗**层数；
        // 只有**刻印 101 / 仙命 137** 把它转成一次真正的攻击（`Attack(dst, ShuiShi)`，并临时挂 AfterCardAciton / KeYinShuiRen）。
        if (c.HasBuff(BuffType.ShuiShi))
        {
            int shuiShiTimes = 1;
            if (c.HasBuff(BuffType.BoLan)) shuiShiTimes++;
            for (int t = 0; t < shuiShiTimes; t++)
            {
                int shuiShi = c.GetBuffValue(BuffType.ShuiShi);
                if (c.Subs.HasKeYinType(c, 101) || c.HasFateStrategy(137))
                {
                    c.ModifyBuffValue(BuffType.AfterCardAciton, 1);
                    c.ModifyBuffValue(BuffType.KeYinShuiRen, 1);
                    CombatMath.Attack(c, foe, shuiShi, 1);
                    c.RemoveBuff(BuffType.KeYinShuiRen);
                    c.RemoveBuff(BuffType.AfterCardAciton);
                }
                else
                {
                    CombatMath.ApplyDamage(c, foe,
                        DamageInfo.Create(c, DamageType.Damage, shuiShi, skipWoundCheck: true));
                }
            }
            // 水势 + 土灵阵Plus：加防 = min(水势层数, 土灵阵Plus层数)。
            if (c.HasBuff(BuffType.TuLingZhenPlus))
                c.ModifyDef(Math.Min(c.GetBuffValue(BuffType.ShuiShi), c.GetBuffValue(BuffType.TuLingZhenPlus)));
        }
        // ── IL_1d27 临时水势 / 灵气回收（水势结算**之后**；早先排在最前，临时加倍的水势就没参与结算）──
        if (c.HasBuff(BuffType.JiLuLinShiShuiShi))
        {
            c.ModifyBuffValue(BuffType.ShuiShi, -c.GetBuffValue(BuffType.JiLuLinShiShuiShi));
            c.ModifyAnima(-c.GetBuffValue(BuffType.JiLuLinShiLingQi));
            c.RemoveBuff(BuffType.JiLuLinShiShuiShi);
            c.RemoveBuff(BuffType.JiLuLinShiLingQi);
        }
        // 影蜘蛛：按「当前防御 ÷ 9020027.o[1]」乘层数打对手，不消耗。
        if (c.HasBuff(BuffType.YingZhiZhu))
        {
            int spiderDmg = c.Def / c.Config.CardOtherParams(9020027).At(1);
            spiderDmg *= c.GetBuffValue(BuffType.YingZhiZhu);
            if (spiderDmg > 0)
                CombatMath.ApplyDamage(c, foe,
                    DamageInfo.Create(c, DamageType.Damage, spiderDmg, skipWoundCheck: true));
        }
        // ── IL_1ecd 散微幻：避邪 += 层数（独立的一条，不受天赋66 门控）──
        if (c.HasBuff(BuffType.SanWeiHuan))
            c.ModifyBuffValue(BuffType.BiXie, c.GetBuffValue(BuffType.SanWeiHuan));
        // ── IL_1f6f 刻印·铸城：把防（封顶刻印 50117 的 o[1]）转成生命及上限，扣一层 ──
        if (c.HasBuff(BuffType.KeYinZhuCheng))
        {
            int zc = c.Def;
            int cap = c.Config.KeYinOtherParams(50117).At(1);
            if (zc > cap) zc = cap;
            if (zc > 0)
            {
                c.ModifyDef(-zc);
                c.ModifyMaxHp(zc);
                c.ModifyHp(zc);
            }
            c.ModifyBuffValue(BuffType.KeYinZhuCheng, -1);
        }
        // ── IL_2119 粽子试炼（卡 114）：对手持有试炼、而自己此刻满血 → **试炼通过，对手上限清零（判负）** ──
        // 卡面「8 回合内加满生命方能通过试炼」；早先 sim 只有开局那一下（把对方打到 1 血），没有这条判定。
        if (foe.HasBuff(BuffType.ZongZiShiLian) && c.Hp == c.MaxHp)
            foe.ModifyMaxHp(-foe.MaxHp);

        // ── IL_21e4 收尾：迅影飞剑加剑意、一批「回合末减 1」、清本回合标记 ──
        if (c.GetBuffValue(BuffType.XunYingFeiJianJiaJianYi) > 0)
        {
            c.ModifyBuffValue(BuffType.JianYi, c.GetBuffValue(BuffType.XunYingFeiJianJiaJianYi));
            c.RemoveBuff(BuffType.XunYingFeiJianJiaJianYi);
        }
        Dec(c, BuffType.MuLingChunFengFuHuiHeShu);
        Dec(c, BuffType.WuFaJiaShengMing);
        Dec(c, BuffType.MengYinLeiZhen);
        Dec(c, BuffType.XuRuo);
        Dec(c, BuffType.PoZhan);
        Dec(c, BuffType.KunFu);
        if (c.HasBuff(BuffType.XiaCiHuiHeJieShuJianShaoFuMianBuff))
        {
            int cut = c.GetBuffValue(BuffType.XiaCiHuiHeJieShuJianShaoFuMianBuff);
            c.ModifyBuffValue(BuffType.XuRuo, -cut);
            c.ModifyBuffValue(BuffType.PoZhan, -cut);
            c.ModifyBuffValue(BuffType.KunFu, -cut);
            c.RemoveBuff(BuffType.XiaCiHuiHeJieShuJianShaoFuMianBuff);
        }
        Dec(c, BuffType.BenHuiHeWuFaZaiCiXingDong);
        Dec(c, BuffType.MengKunXian);
        Dec(c, BuffType.JiaLingQiJianBan);
        Dec(c, BuffType.BoLan);
        if (c.HasBuff(BuffType.BENLUNGONGJICISHU)) c.SetBuffValue(BuffType.BENLUNGONGJICISHU, 0);
        if (c.HasBuff(BuffType.ZaiCiXingDong)) c.RemoveBuff(BuffType.ZaiCiXingDong);

        // 一次性的「本回合生效」标记全清。
        c.RemoveBuff(BuffType.BaiShouLingJianZhenShengXiao);
        c.RemoveBuff(BuffType.LingGuiMiZongBuShengXiao);
        foe.RemoveBuff(BuffType.LingGuiMiZongBuShengXiao);
        c.RemoveBuff(BuffType.MengLingXuanBuChuFa);
        foe.RemoveBuff(BuffType.MengLingXuanBuChuFa);
        c.RemoveBuff(BuffType.MengLingXuanChuFaCiShu);
        foe.RemoveBuff(BuffType.MengLingXuanChuFaCiShu);
        c.RemoveBuff(BuffType.MingYeMiZongBuShengXiao);
        foe.RemoveBuff(BuffType.MingYeMiZongBuShengXiao);
        if (c.HasBuff(BuffType.LingGuaZiYanYiChuFa))
        {
            c.ModifyBuffValue(BuffType.GuaXiang, 1);
            c.RemoveBuff(BuffType.LingGuaZiYanYiChuFa);
        }
        c.RemoveBuff(BuffType.MengXingLuoHuaShenChuFaCiShu);
        c.RemoveBuff(BuffType.MengYuleiHuiHeXianZhi);
        c.RemoveBuff(BuffType.WuXingJuLingYiChuFa);
        c.RemoveBuff(BuffType.ChanXinJuLingYiChuFa);
        c.RemoveBuff(BuffType.ReXueHuaQiYiChuFa);
        c.RemoveBuff(BuffType.LingQiBengYongQuanShengXiao);
        c.RemoveBuff(BuffType.HuiHeJiaShengMing);
        // 共鸣位标记（回合末回收；原码还会清永久标记，本 sim 每场独立不做）。
        foreach (int id in new[] { 30, 18, 117, 11, 120 })
            if (c.CheckTalentResonanceTempFlag(id)) c.RemoveTalentResonanceTempFlag(id);
        c.RemoveBuff(BuffType.WuShiKunXianKunFu);
        Dec(c, BuffType.LuoHuaYouYiPlus);
        Dec(c, BuffType.HuoLingYinPlus);
        Dec(c, BuffType.JinLingYinPlus);
        // 刻印·山风：削减本回合再行动次数上限。
        if (c.HasBuff(BuffType.KeYinShanFeng))
        {
            c.ActionAgainPerRound -= c.GetBuffValue(BuffType.KeYinShanFeng);
            c.RemoveBuff(BuffType.KeYinShanFeng);
        }
        c.RemoveBuff(BuffType.JiGuanLingYin);
        // 记录本回合起始血（供下回合比较）。
        c.SetBuffValue(BuffType.LastTurnStartHp, c.GetBuffValue(BuffType.ThisTurnStartHp));
        c.RemoveBuff(BuffType.SiZhanBuDao);
        Dec(c, BuffType.MengBuDongJinGangZhen);
        Dec(c, BuffType.GongJiShiShiJiaNeiShangHuiHeShu);
    }
}
