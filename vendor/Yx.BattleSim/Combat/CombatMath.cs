using Yx.BattleSim.Config;
using Yx.BattleSim.Effects;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Combat;

/// <summary>
/// 伤害管线：ApplyDamage —— 忠实移植自 BattleCharacter.ApplyDamage 全流程（剥去屏震/特效/浮字，
/// 保留每一条数值与 buff 分支，顺序与原码一致）。落血委托 <see cref="Combatant.OnHit"/>。
///
/// 语义：<paramref name="attacker"/> 对 <paramref name="target"/> 造成 <see cref="DamageInfo"/> 伤害。
/// 依赖 attacker/target 的 Config/Subs（刻印/五行/共鸣 未移植时中性，见覆盖账 P4/P5）。
/// 段数（attackCount）由出牌驱动按段各调一次（见 BattleResolver）。
/// </summary>
public static class CombatMath
{
    public static void ApplyAttack(Combatant attacker, Combatant target, int rawAttack)
        => ApplyDamage(attacker, target, DamageInfo.Create(attacker, DamageType.Attack, rawAttack));

    /// <summary>
    /// 一次「攻击」= 逐段调 <see cref="CalculateAttack"/>（对应游戏 BattleCharacter.Attack(dst, attack, attackCount)）。
    /// 卡效果 ExecuteEffect 施完效果后调它出攻击段。
    /// </summary>
    public static void Attack(Combatant attacker, Combatant target, int attack, int attackCount)
    {
        // 原码 Attack(dst, attack, attackCount)（8639）：次数 0 → 1、上限 999，然后逐段 CalculateAttack ——
        // **不看攻击值**：攻 0 也照样走一遍（加攻 / 剑意等在 CalculateAttack 里叠上去）。
        int segs = attackCount == 0 ? 1 : attackCount;
        if (segs > 999) segs = 999;
        for (int i = 0; i < segs; i++)
            CalculateAttack(attacker, target, attack);
    }

    /// <summary>
    /// 「每段攻值不同」的攻击：对应 <c>BattleCharacter.Attack(dst, List&lt;int&gt; attacks)</c>（原码 13167）——
    /// 逐段 <see cref="CalculateAttack"/>，第 i 段用 attacks[i]。
    /// </summary>
    public static void Attack(Combatant attacker, Combatant target, IReadOnlyList<int> attacks)
    {
        for (int i = 0; i < attacks.Count && i < 999; i++)
            CalculateAttack(attacker, target, attacks[i]);
    }

    /// <summary>
    /// 攻击值计算：从 <paramref name="srcAttack"/> 经加攻 buff 链算出最终攻击，再落一次 ApplyDamage。
    /// 忠实移植自 <c>BattleCharacter.CalculateAttack</c>（原码 12680），**逐条照原码顺序**。
    /// ⚠ 顺序敏感的几处：梦•枯木逢春的翻倍在**减攻段之后**（原码 296 行位置，早先 sim 放在减攻之前 → (a×2−b) 与 (a−b)×2 之差）；
    ///   倍率下限只在最后钳一次；牌型判定（崩拳 / 普攻 / 灵剑）在**攻击时现判**（原码 `IsBengQuan(m_CurrentUsingCard.cardConfig)`），
    ///   不用出牌开始时缓存的标志 —— 出牌前钩子里挂的「下张牌算崩拳」等会改变判定结果。
    /// 共鸣 133：开局盘面（privateData.usedCards）里除基础 id 74 外没有持续牌 → 悯夜不封顶（原码 12730）。
    /// </summary>
    public static void CalculateAttack(Combatant attacker, Combatant target, int srcAttack)
    {
        int num = srcAttack;
        var cfg = attacker.Config;
        int cardId = attacker.CurrentCardId;
        string cardName = attacker.CurrentCardName;                              // 当前牌的配置名（上下文）
        bool hasCard = attacker.CurrentCardGrid >= 0 || cardId != 0 || cardName.Length > 0;   // 原码 m_CurrentUsingCard != null
        bool after = attacker.HasBuff(BuffType.AfterCardAciton);
        // 出牌时缓存的牌型标志 **或** 此刻现判（出牌前钩子挂的「下张牌算崩拳」等会让现判变真）。
        bool isBengQuan = attacker.CurrentCardIsBengQuan || CardTypes.IsBengQuan(attacker, cardId);

        // 机关剑阵：按机关刻文次数打真伤（消耗一层）。
        if (attacker.HasBuff(BuffType.JiGuanJianZhen))
        {
            attacker.ModifyBuffValue(BuffType.JiGuanJianZhen, -1);
            int hits = 1 + attacker.GetBuffValue(BuffType.JiGuanKeWen) / 2;
            for (int i = 0; i < hits; i++)
                ApplyDamage(attacker, attacker.Opponent, DamageInfo.Create(attacker, DamageType.Damage, cfg.KeYinOtherParams(50125).At(1), skipWoundCheck: true));
        }
        // 背鼓 → 虚弱；激虚 → 破绽。
        if (target.GetBuffValue(BuffType.BeiGu) > 0)
        {
            target.ModifyBuffValue(BuffType.BeiGu, -1);
            attacker.ModifyBuffValue(BuffType.XuRuo, 1);
        }
        if (attacker.GetBuffValue(BuffType.JiXu) > 0)
        {
            attacker.ModifyBuffValue(BuffType.JiXu, -1);
            target.ModifyBuffValue(BuffType.PoZhan, 1);
        }
        // 受击前加防（攻击结算前先给对方加防并消耗）。
        if (target.HasBuff(BuffType.XiaCiShouDaoGongJiQianJiaFang))
        {
            int add = target.GetBuffValue(BuffType.XiaCiShouDaoGongJiQianJiaFang);
            target.RemoveBuff(BuffType.XiaCiShouDaoGongJiQianJiaFang);
            target.ModifyDef(add);
        }
        // 攻击计数（供其它效果读取）。
        attacker.ModifyBuffValue(BuffType.BENLUNGONGJICISHU, 1);
        attacker.ModifyBuffValue(BuffType.DanKaGongJiJiShu, 1);
        attacker.ModifyBuffValue(BuffType.ZhanDouGongJiJiShu, 1);
        target.ModifyBuffValue(BuffType.BeAttackedCount, 1);

        // ── 加攻段 ──
        // 剑意（「下次攻击双倍剑意 / 加攻加成」时再加一次），记消耗（出牌后钩子里真正扣）。
        if (attacker.HasBuff(BuffType.JianYi) && !attacker.HasBuff(BuffType.KaPaiBuChuFaJianYi))
        {
            int jy = attacker.GetBuffValue(BuffType.JianYi);
            num += jy;
            if (attacker.HasBuff(BuffType.XiaCiGongJiShouShuangBeiJianYiJiaGongJiaCheng)) num += jy;
            attacker.SetBuffValue(BuffType.XiaoHaoJianYi, jy);
        }
        // 崩拳•戳：当前牌为崩拳时 +值，并记消耗（出牌后钩子扣）。
        if (attacker.HasBuff(BuffType.BengQuanChuo) && isBengQuan && !after)
        {
            num += attacker.GetBuffValue(BuffType.BengQuanChuo);
            attacker.SetBuffValue(BuffType.XiaoHaoBengQuanChuo, attacker.GetBuffValue(BuffType.BengQuanChuo));
        }
        // 悯夜：当前牌为崩拳时，按自身负面状态层数加攻（封顶为悯夜层数）。
        if (attacker.HasBuff(BuffType.MinYe) && isBengQuan && !after)
        {
            bool uncapped = false;
            if (attacker.HasTalentResonance(133))
            {
                int sustain = 0;
                foreach (int u in attacker.UsedCardsAtStart)
                    if (CardTypes.BaseId(u) != 74 && cfg.CardIsSustain(u)) sustain++;
                uncapped = sustain == 0;
            }
            int d = attacker.GetDebuffCount();
            if (!uncapped && d > attacker.GetBuffValue(BuffType.MinYe)) d = attacker.GetBuffValue(BuffType.MinYe);
            num += d;
        }
        // 后一格是 10030077 / 10040077：按自身负面状态数 / 10040077.o[0] 加攻。早先缺失。
        if (hasCard && !after && attacker.CurrentCardGrid >= 0)
        {
            int nextId = GridFunctions.NextGridCard(attacker, attacker.CurrentCardGrid)?.Id ?? 0;
            if (nextId == 10040077 || nextId == 10030077)
            {
                int dv = cfg.CardOtherParams(10040077).At(0);
                int n5 = dv == 0 ? 0 : attacker.GetDebuffCount() / dv;
                if (n5 > 0) num += n5;
            }
        }
        // 仙命 424：第 8 格（下标 7）的牌按负面状态数 / o[0] 加攻。早先缺失。
        if (attacker.HasFateStrategy(424) && hasCard && attacker.CurrentCardGrid == 7 && !after)
        {
            int dv = cfg.FateOtherParams(424).At(0);
            if (dv != 0) num += attacker.GetDebuffCount() / dv;
        }
        // 仙命 432：崩拳按「灵气 / o[0]」加攻（封顶 o[1]）。早先缺失。
        if (attacker.HasFateStrategy(432) && isBengQuan && !after)
        {
            int dv = cfg.FateOtherParams(432).At(0);
            int v = dv == 0 ? 0 : attacker.Anima / dv;
            v = Math.Min(v, cfg.FateOtherParams(432).At(1));
            num += v;
        }
        // 刻印 56：崩拳加攻。早先缺失。
        if (attacker.Subs.HasKeYinType(attacker, 56) && isBengQuan && !after)
            num += attacker.Subs.KeYinOtherparam(attacker, 56, 0);
        // 星位 + 星力：在星位格上出的牌加星力；共鸣 68 让**非**星位格也加。
        if (attacker.CurrentCardGrid >= 0)
        {
            bool onXingWei = GridMarkFunctions.IsXingWei(attacker, attacker.CurrentCardGrid);
            if (onXingWei) num += attacker.GetBuffValue(BuffType.XingLi);
            if (attacker.Subs.IsTalentResonanceEffective(attacker, 68) && !onXingWei) num += attacker.GetBuffValue(BuffType.XingLi);
        }
        if (attacker.HasBuff(BuffType.KuangWuQu)) num += attacker.GetBuffValue(BuffType.KuangWuQu);
        if (attacker.HasBuff(BuffType.JiaGong))
        {
            num += attacker.GetBuffValue(BuffType.JiaGong);
            if (attacker.HasBuff(BuffType.XiaCiGongJiShouShuangBeiJianYiJiaGongJiaCheng))
                num += attacker.GetBuffValue(BuffType.JiaGong);
        }
        // 机关剑列 + 机关刻文：+3、各扣一层、得无视防御。
        if (attacker.HasBuff(BuffType.JiGuanJianLie) && attacker.HasBuff(BuffType.JiGuanKeWen))
        {
            attacker.ModifyBuffValue(BuffType.JiGuanJianLie, -1);
            attacker.ModifyBuffValue(BuffType.JiGuanKeWen, -1);
            num += 3;
            attacker.ModifyBuffValue(BuffType.WuShiFangYu, 1);
        }
        // 机关剑斩 + 机关刻文：+2×机关刻文、清空机关刻文。
        if (attacker.HasBuff(BuffType.JiGuanJianZhan) && attacker.HasBuff(BuffType.JiGuanKeWen))
        {
            attacker.ModifyBuffValue(BuffType.JiGuanJianZhan, -1);
            num += attacker.GetBuffValue(BuffType.JiGuanKeWen) * 2;
            attacker.RemoveBuff(BuffType.JiGuanKeWen);
        }
        // 刻印 135：每张 135 刻印消耗一层机关刻文换 o[1] 攻。早先缺失。
        if (attacker.Subs.HasKeYinType(attacker, 135))
        {
            int exAtk = 0;
            foreach (int keyin in attacker.BattleKeYinCards)
            {
                if (keyin % 10000 == 135 && attacker.GetBuffValue(BuffType.JiGuanKeWen) >= 1)
                {
                    attacker.ModifyBuffValue(BuffType.JiGuanKeWen, -1);
                    exAtk += cfg.KeYinOtherParams(keyin).At(1);
                }
            }
            num += exAtk;
        }
        // 共鸣 53：加上自身外伤层数；共鸣 7：有（本轮）无视防御时加攻。早先缺失。
        if (attacker.Subs.IsTalentResonanceEffective(attacker, 53)) num += attacker.GetBuffValue(BuffType.WaiShang);
        if (attacker.Subs.HasTalentResonance(attacker, 7)
            && (attacker.HasBuff(BuffType.BenLunWuShiFangYu) || attacker.HasBuff(BuffType.WuShiFangYu)))
            num += cfg.ResonanceOtherParams(7).At(0);
        if (attacker.HasTalent(206) && attacker.HasBuff(BuffType.GunJiaShi)) num++;
        if (attacker.HasBuff(BuffType.GuYeLang) && attacker.Hp * 2 < attacker.MaxHp)
            num += attacker.GetBuffValue(BuffType.GuYeLang);
        if (attacker.HasBuff(BuffType.SuiShaZhen)) num += cfg.CardOtherParams(8000002).At(1);
        if (attacker.HasBuff(BuffType.DuanGu))
        {
            attacker.ModifyBuffValue(BuffType.DuanGu, -1);
            num += cfg.CardOtherParams(10000027).At(2);
            attacker.ModifyTiPo(1);
        }
        if (attacker.HasBuff(BuffType.XingChengDuanGuJue))
        {
            num += attacker.GetBuffValue(BuffType.XingChengDuanGuJue);
            attacker.RemoveBuff(BuffType.XingChengDuanGuJue);
        }
        // 天击反骨：扣一层，把对方所有负面状态复制到自己身上并清空对方的，清掉的层数加攻。
        if (attacker.HasBuff(BuffType.TianJiFanGuJiaGongJi))
        {
            attacker.ModifyBuffValue(BuffType.TianJiFanGuJiaGongJi, -1);
            foreach (var d in target.GetDebuffList())
                attacker.ModifyBuffValue(d, target.GetBuffValue(d));
            num += target.RemoveAllDebuff();
        }
        if (attacker.HasBuff(BuffType.HuanQiChenJiaGong))
        {
            attacker.ModifyBuffValue(BuffType.HuanQiChenJiaGong, -1);
            num += attacker.Anima;
        }
        if (attacker.HasBuff(BuffType.XiaCiGongJiDuoGong))
        {
            num += attacker.GetBuffValue(BuffType.XiaCiGongJiDuoGong);
            attacker.RemoveBuff(BuffType.XiaCiGongJiDuoGong);
        }
        if (attacker.HasBuff(BuffType.KeYinZhuoKunBie))
        {
            num += attacker.GetBuffValue(BuffType.KeYinZhuoKunBie) * target.GetDebuffCount();
            attacker.RemoveBuff(BuffType.KeYinZhuoKunBie);
        }
        // 刻印·震山崩：消耗自身一半防，按层数倍率加攻。
        if (attacker.HasBuff(BuffType.KeYinZhenShanBeng))
        {
            int half = attacker.Def / 2;
            if (half > 0)
            {
                attacker.ModifyDef(-half);
                num += half * attacker.GetBuffValue(BuffType.KeYinZhenShanBeng);
            }
            attacker.RemoveBuff(BuffType.KeYinZhenShanBeng);
        }
        if ((attacker.CurrentCardIsPuTong || CardTypes.IsPuTongGongJi(cardId)) && attacker.HasBuff(BuffType.XiaCiPuTongGongJiDuoGong))
        {
            num += attacker.GetBuffValue(BuffType.XiaCiPuTongGongJiDuoGong);
            attacker.RemoveBuff(BuffType.XiaCiPuTongGongJiDuoGong);
        }
        if (attacker.HasBuff(BuffType.XiaCiLingJianGongJiDuoGong) && (attacker.CurrentCardIsLingJian || CardTypes.IsLingJian(attacker, cardId)))
        {
            num += attacker.GetBuffValue(BuffType.XiaCiLingJianGongJiDuoGong);
            attacker.RemoveBuff(BuffType.XiaCiLingJianGongJiDuoGong);
        }
        // 仙命 152：描述带「灵气」或耗灵气的牌加攻。早先缺失。
        if (attacker.HasFateStrategy(152)
            && (cfg.CardDesc(cardId).Contains("灵气") || ((cfg as JsonBattleConfig)?.Card(cardId)?.Anima ?? 0) < 0))
            num += cfg.FateOtherParams(152).At(0);
        if (attacker.HasBuff(BuffType.QiRuoXuanHe))
        {
            attacker.ModifyBuffValue(BuffType.QiRuoXuanHe, -1);
            attacker.ModifyBuffValue(BuffType.QiShi, 1);
        }
        if (attacker.HasBuff(BuffType.GongJiJiaQiSHi))
            attacker.ModifyBuffValue(BuffType.QiShi, attacker.GetBuffValue(BuffType.GongJiJiaQiSHi));
        // 仙命 428：「掌」牌打出时**不消耗**气势。
        int qiShiCost = attacker.HasFateStrategy(428) && cardName.Contains("掌") ? 0 : 1;
        // 气势 / 玄心斩破 / 气沉丹田+：牌组里有 10000084 时走「加攻」，否则走下方「倍率」。
        bool qiShiLike = attacker.HasBuff(BuffType.QiShi) || attacker.HasBuff(BuffType.XuanXinZhanPo) || attacker.HasBuff(BuffType.QiChenDanTianPlus);
        if (qiShiLike && attacker.Subs.HasCardInDeck(attacker, 10000084))
        {
            int mul = attacker.HasBuff(BuffType.QiShiBeiLv) ? attacker.GetBuffValue(BuffType.QiShiBeiLv) : 1;
            if (attacker.HasBuff(BuffType.QiShi))
            {
                num += attacker.GetBuffValue(BuffType.QiShi) * mul;
                attacker.ModifyBuffValue(BuffType.QiShi, -qiShiCost);
            }
            if (attacker.HasBuff(BuffType.XuanXinZhanPo)) num += attacker.GetDebuffCount() * mul;
            if (attacker.HasBuff(BuffType.QiChenDanTianPlus)) num += attacker.Anima * mul;
        }

        // ── 减攻段 ──
        if (attacker.HasBuff(BuffType.JianGong) && num > 1 && !attacker.HasBuff(BuffType.XuanXinZhanPo) && !attacker.HasBuff(BuffType.WuShiJianGong))
        {
            num -= attacker.GetBuffValue(BuffType.JianGong);
            if (num < 1) num = 1;
        }
        if (target.HasBuff(BuffType.TieGu))
        {
            num -= cfg.CardOtherParams(7000035).At(0);
            if (num < 1) num = 1;
        }
        if (target.HasTalent(206) && target.HasBuff(BuffType.QuanJiaShi))
        {
            num--;
            if (num < 1) num = 1;
        }
        if (target.HasBuff(BuffType.ZhuShiBuYi))
        {
            num -= cfg.CardOtherParams(11000014).At(1);
            if (num < 1) num = 1;
            target.ModifyBuffValue(BuffType.ZhuShiBuYi, -1);
        }
        // 梦•枯木逢春（4000087）：「若用过，此牌攻翻倍」—— **在减攻段之后**（原码 296 行）。
        // 0–3 档：用过（MengKuMuJiShu > 0）翻一倍；4 档（4040087）：按层数逐次翻倍。
        if (CardTypes.BaseId(cardId) == 4000087 && cardId != 4040087 && !after && attacker.GetBuffValue(BuffType.MengKuMuJiShu) > 0)
            num *= 2;
        if (cardId == 4040087 && !after)
        {
            int mk = attacker.GetBuffValue(BuffType.MengKuMuJiShu);
            for (int i = 0; i < mk; i++) num *= 2;
        }

        // ── 倍率段 ──
        double num15 = 1.0;
        if (target.HasBuff(BuffType.PoZhan))
        {
            double add = 0.4;
            if (target.HasBuff(BuffType.YinFuYuJian)) add = target.GetBuffValue(BuffType.YinFuYuJian) / 100.0;
            if (attacker.Subs.IsTalentResonanceEffective(attacker, 136) && attacker.HasTalent(206) && attacker.HasBuff(BuffType.QuanJiaShi)) add -= 0.2;
            num15 += add;
        }
        if (attacker.HasBuff(BuffType.LeiTingXinFa) && hasCard && cardName.Contains("雷") && !after)
            num15 += attacker.GetBuffValue(BuffType.LeiTingXinFa) / 100.0;
        if (attacker.HasBuff(BuffType.XuRuo) && !attacker.HasBuff(BuffType.WuShiXuRuo) && !attacker.HasBuff(BuffType.XuanXinZhanPo))
        {
            double sub = -0.4;
            if (attacker.HasBuff(BuffType.YinFuYuJian)) sub = -attacker.GetBuffValue(BuffType.YinFuYuJian) / 100.0;
            if (attacker.Subs.IsTalentResonanceEffective(attacker, 136) && attacker.HasTalent(206) && attacker.HasBuff(BuffType.GunJiaShi)) sub += 0.2;
            num15 += sub;
        }
        if (target.HasBuff(BuffType.QianDun)) num15 -= 0.4;
        if (target.HasBuff(BuffType.ChaiZhao) && target.HasBuff(BuffType.QuanJiaShi))
            num15 -= cfg.CardOtherParams(221).At(0) / 100.0;
        if (target.HasBuff(BuffType.HuanQiChenJianShang))
            num15 -= cfg.CardOtherParams(328).At(2) / 100.0;
        if (target.HasBuff(BuffType.MengFanZhenJianGong))
            num15 -= cfg.CardOtherParams(4000078).At(2) / 100.0;
        qiShiLike = attacker.HasBuff(BuffType.QiShi) || attacker.HasBuff(BuffType.XuanXinZhanPo) || attacker.HasBuff(BuffType.QiChenDanTianPlus);
        if (qiShiLike && !attacker.Subs.HasCardInDeck(attacker, 10000084))
        {
            int mul = attacker.HasBuff(BuffType.QiShiBeiLv) ? attacker.GetBuffValue(BuffType.QiShiBeiLv) : 1;
            if (attacker.HasBuff(BuffType.QiShi))
            {
                num15 += attacker.GetBuffValue(BuffType.QiShi) * 0.1 * mul;
                attacker.ModifyBuffValue(BuffType.QiShi, -qiShiCost);
            }
            if (attacker.HasBuff(BuffType.XuanXinZhanPo)) num15 += attacker.GetDebuffCount() * 0.1 * mul;
            if (attacker.HasBuff(BuffType.QiChenDanTianPlus)) num15 += attacker.Anima * 0.1;
        }
        if (num15 < 0.0) num15 = 0.0;
        if (num > 0)
        {
            if (target.HasBuff(BuffType.MengFanZhenJianGong))
            {
                int reflect = Combatant.CeilDiv(num * cfg.CardOtherParams(4000078).At(2), 100);
                if (reflect < 1) reflect = 1;
                target.SetBuffValue(BuffType.MengFanZhenFanTan, reflect);
            }
            num = (int)Math.Round(num15 * 100.0) * num / 100;
            if (num < 1) num = 1;
            if (attacker.HasBuff(BuffType.WanShiRuYi) && num <= cfg.CardOtherParams(11000013).At(0))
                num = cfg.CardOtherParams(11000013).At(0);
        }

        // ── 攻击落地前的攻后效果 ──
        if (attacker.HasTalent(68))
        {
            int d = cfg.TalentOtherParams(68).At(0);
            if (attacker.Def == 0) d += cfg.TalentOtherParams(68).At(1);
            attacker.ModifyDef(d);
        }
        if (attacker.Subs.IsTalentResonanceEffective(attacker, 6))
            attacker.ModifyBuffValue(BuffType.XiaHuiHeJiaFang, cfg.ResonanceOtherParams(6).At(1));
        if (attacker.HasTalent(67)) target.ModifyMaxHp(-cfg.TalentOtherParams(67).At(0));
        if (attacker.Subs.HasTalentResonance(attacker, 5)) target.ModifyMaxHp(-cfg.ResonanceOtherParams(5).At(0));
        if (attacker.HasBuff(BuffType.WanMoShiXinQu)) attacker.ModifyHp(-attacker.GetBuffValue(BuffType.WanMoShiXinQu));
        if (attacker.HasBuff(BuffType.MuCi))
        {
            target.ModifyHp(-attacker.GetBuffValue(BuffType.MuCi));
            attacker.ModifyHp(attacker.GetBuffValue(BuffType.MuCi));
        }
        if (attacker.HasBuff(BuffType.GongJiXiQuShengMing))
        {
            attacker.ModifyBuffValue(BuffType.GongJiXiQuShengMing, -1);
            target.ModifyHp(-1);
            attacker.ModifyHp(1);
        }
        if (attacker.HasBuff(BuffType.MuLingChunFengFuHuiHeShu))
        {
            attacker.ModifyMaxHp(2);
            attacker.ModifyHp(2);
        }

        ApplyDamage(attacker, target, DamageInfo.Create(attacker, DamageType.Attack, num));
    }

    /// <summary>
    /// 原码 `m_CurrentUsingCard != null`：本场已经有「当前牌」。⚠ 普攻的 id 是 0，所以**不能**用 `CurrentCardId != 0` 代替
    /// （早先几处这么写，普攻打出的伤害就吃不到梦断拳 / 崩拳多形 / 狂剑灵石等「当前牌」分支）。
    /// </summary>
    private static bool HasCurrentCard(Combatant c)
        => c.CurrentCardGrid >= 0 || c.CurrentCardId != 0 || c.CurrentCardName.Length > 0;

    public static void ApplyDamage(Combatant attacker, Combatant target, DamageInfo info)
    {
        int num = info.Damage;
        bool isDmgOrReflect = info.Type == DamageType.Damage || info.Type == DamageType.ReflectDamage;

        // 受击前加防（下次受攻击前加防）。
        if (target.HasBuff(BuffType.XiaCiShouDaoGongJiQianJiaFang))
        {
            int add = target.GetBuffValue(BuffType.XiaCiShouDaoGongJiQianJiaFang);
            target.RemoveBuff(BuffType.XiaCiShouDaoGongJiQianJiaFang);
            target.ModifyDef(add);
        }
        // 铁骨：效果/反弹伤害减免（下限 1）。
        if (target.HasBuff(BuffType.TieGu) && isDmgOrReflect)
        {
            num -= attacker.Config.CardOtherParams(7000035).At(0);
            if (num < 1) num = 1;
        }
        // 拆招+全甲势 / 梦幻真剑功：百分比减伤。
        int reducePct = 0;
        if (target.HasBuff(BuffType.ChaiZhao) && target.HasBuff(BuffType.QuanJiaShi) && isDmgOrReflect)
            reducePct += attacker.Config.CardOtherParams(221).At(0);
        if (target.HasBuff(BuffType.MengFanZhenJianGong) && isDmgOrReflect)
            reducePct += attacker.Config.CardOtherParams(4000078).At(2);
        int mengFanReflect = 0;
        if (target.HasBuff(BuffType.MengFanZhenJianGong) && isDmgOrReflect)
            mengFanReflect = Combatant.CeilDiv(num * attacker.Config.CardOtherParams(4000078).At(2), 100);
        if (reducePct > 0)
        {
            num = num * (100 - reducePct) / 100;
            if (num < 1) num = 1;
        }

        // 无视防御（攻击方）。
        bool ignoreDef = false;
        if (info.Type == DamageType.Attack && attacker.HasBuff(BuffType.BenLunWuShiFangYu)) ignoreDef = true;
        else if (info.Type == DamageType.Attack && attacker.HasBuff(BuffType.WuShiFangYu))
        {
            attacker.ModifyBuffValue(BuffType.WuShiFangYu, -1);
            ignoreDef = true;
        }

        // 防御吸收（+碎防翻倍）。
        if (info.Type != DamageType.ReduceHp && !ignoreDef)
        {
            int def = target.Def;
            if (def > 0)
            {
                info.HitDef = true;
                if (info.Type == DamageType.Attack &&
                    (attacker.HasBuff(BuffType.SuiFang) || attacker.HasBuff(BuffType.SuiShaZhen)
                     || attacker.HasBuff(BuffType.YeRenHua) || attacker.HasBuff(BuffType.XiaCiGongJiSuiFang)))
                {
                    num = (num * 2 >= def) ? (num + Combatant.CeilDiv(def, 2)) : (num * 2);
                }
                target.ModifyDef(-num);
                num -= def;
                if (num < 0) num = 0;
            }
        }

        // 碎杀阵 / 下次攻击碎防 / 双倍减疫 的消耗。
        if (attacker.HasBuff(BuffType.SuiShaZhen) && info.Type == DamageType.Attack)
            attacker.ModifyBuffValue(BuffType.SuiShaZhen, -1);
        if (attacker.HasBuff(BuffType.XiaCiGongJiSuiFang) && info.Type == DamageType.Attack
            && !attacker.HasBuff(BuffType.SuiFang) && !attacker.HasBuff(BuffType.SuiShaZhen) && !attacker.HasBuff(BuffType.YeRenHua))
            attacker.ModifyBuffValue(BuffType.XiaCiGongJiSuiFang, -1);
        if (attacker.HasBuff(BuffType.XiaCiGongJiShouShuangBeiJianYiJiaGongJiaCheng) && info.Type == DamageType.Attack)
            attacker.ModifyBuffValue(BuffType.XiaCiGongJiShouShuangBeiJianYiJiaGongJiaCheng, -1);

        // 攻击后加伤链（外伤判定门槛）。
        if (info.Type == DamageType.Attack &&
            (attacker.HasTalent(67) || attacker.HasBuff(BuffType.XiaCiGongJiHouJianShengMing)
             || attacker.HasBuff(BuffType.BiDingShiZuoJiShang) || attacker.HasBuff(BuffType.LongMaJingShen)
             || (num > 0 && !info.SkipWoundCheck && !target.HasBuff(BuffType.HuTi) && !target.HasBuff(BuffType.YiHuaJieMu))))
        {
            // 锋锐：加伤并消耗，含返还 / 刻印(32) / 五行 联动。
            if (attacker.HasBuff(BuffType.FengRui) && info.Type == DamageType.Attack && !attacker.HasBuff(BuffType.BuHaoFengRui))
            {
                num += attacker.GetBuffValue(BuffType.FengRui);
                info.FengRui = attacker.GetBuffValue(BuffType.FengRui);
                if (attacker.HasBuff(BuffType.XiaCiFengRuiChuFaJianShengMing))
                {
                    num += attacker.GetBuffValue(BuffType.XiaCiFengRuiChuFaJianShengMing);
                    info.FengRui += attacker.GetBuffValue(BuffType.XiaCiFengRuiChuFaJianShengMing);
                    attacker.RemoveBuff(BuffType.XiaCiFengRuiChuFaJianShengMing);
                }
                int fr = attacker.GetBuffValue(BuffType.FengRui);
                attacker.RemoveBuff(BuffType.FengRui);
                if (attacker.HasBuff(BuffType.FanHuanFengRui))
                {
                    attacker.ModifyBuffValue(BuffType.FanHuanFengRui, -1);
                    attacker.ModifyBuffValue(BuffType.FengRui, fr);
                }
                if (attacker.CurrentCardBaseId == 7000015 && attacker.Subs.HasKeYinType(attacker, 32))
                    attacker.ModifyBuffValue(BuffType.FengRui, Math.Min(fr, attacker.Subs.KeYinOtherparam(attacker, 32, 0)));
                if (attacker.CurrentCardBaseId == 7000099 && attacker.Subs.CheckWuXing(attacker, BuffType.JiHuoJinLing) && !attacker.HasBuff(BuffType.AfterCardAciton))
                {
                    int bonus = Combatant.CeilDiv(fr * attacker.Config.CardOtherParams(7000099).At(0), 100);
                    if (bonus > 0) attacker.ModifyBuffValue(BuffType.FengRui, bonus);
                }
                if (attacker.CurrentCardBaseId == 423 && attacker.Subs.CheckWuXing(attacker, BuffType.JiHuoShuiLing) && !attacker.HasBuff(BuffType.AfterCardAciton))
                {
                    attacker.ModifyBuffValue(BuffType.ShuiShi, fr);
                    attacker.ModifyMaxHp(fr);
                    attacker.ModifyHp(fr);
                }
            }
            // 外伤（目标身上）。
            if (target.HasBuff(BuffType.WaiShang) && info.Type == DamageType.Attack)
                num += target.GetBuffValue(BuffType.WaiShang);
            if (attacker.HasBuff(BuffType.XiaCiGongJiHouJianShengMing))
            {
                num += attacker.GetBuffValue(BuffType.XiaCiGongJiHouJianShengMing);
                attacker.RemoveBuff(BuffType.XiaCiGongJiHouJianShengMing);
            }
            // 巨鼎落 ×2。
            if (attacker.HasBuff(BuffType.JuDingLuo) && info.Type == DamageType.Attack)
            {
                num *= 2;
                info.FengRui *= 2;
            }
            // 刻印水刃 ×1.5。
            if (attacker.HasBuff(BuffType.KeYinShuiRen) && info.Type == DamageType.Attack)
            {
                num = (int)MathF.Floor(num * 1.5f);
                info.FengRui = (int)MathF.Floor(info.FengRui * 1.5f);
            }
            // 机关刻文（目标）：-2、消耗。
            if (target.HasBuff(BuffType.JiGuanKeWen) && num > 0 && info.Type == DamageType.Attack)
            {
                num -= 2;
                if (num < 1) num = 1;
                target.ModifyBuffValue(BuffType.JiGuanKeWen, -1);
            }
            attacker.ModifyBuffValue(BuffType.WoundedCount, 1);
        }

        info.Damage = num;
        int dealt = target.OnHit(info);

        if (dealt > 0 && info.Type == DamageType.Attack && !info.SkipWoundCheck)
            attacker.ModifyBuffValue(BuffType.ActualDamage, dealt);

        // 梦幻真：反伤自身（一次）。
        if (mengFanReflect > 0 && !attacker.HasBuff(BuffType.MengFanZhenCiShu))
        {
            attacker.ModifyBuffValue(BuffType.MengFanZhenCiShu, 1);
            attacker.ModifyHp(-mengFanReflect);
            attacker.RemoveBuff(BuffType.MengFanZhenCiShu);
        }
        // 暗行蝙蝠：低伤回血。
        if (dealt > 0 && info.Type == DamageType.Attack && attacker.HasBuff(BuffType.AnXingBianFu) && dealt <= attacker.GetBuffValue(BuffType.AnXingBianFu))
            attacker.ModifyHp(dealt);
        // 天命绝舌：回血（封顶）、消耗。
        if (dealt > 0 && info.Type == DamageType.Attack && attacker.HasBuff(BuffType.TianMingJueShe))
        {
            int heal = Math.Min(dealt, attacker.GetBuffValue(BuffType.TianMingJueShe));
            attacker.ModifyHp(heal);
            attacker.RemoveBuff(BuffType.TianMingJueShe);
        }
        // 狂剑灵石：按比例回血（当前牌为狂剑）。
        if (dealt > 0 && attacker.HasBuff(BuffType.KuangJianLingShi) && info.Type == DamageType.Attack
            && HasCurrentCard(attacker) && (attacker.CurrentCardIsKuangJian || CardTypes.IsKuangJian(attacker, attacker.CurrentCardId))
            && !attacker.HasBuff(BuffType.AfterCardAciton))
        {
            int heal = (int)(dealt * (double)attacker.GetBuffValue(BuffType.KuangJianLingShi) / 100.0);
            if (heal > 0) attacker.ModifyHp(heal);
        }
        // 天击地煞：加防、消耗。
        if (attacker.HasBuff(BuffType.TianJiDiSha) && info.Type == DamageType.Attack)
        {
            attacker.ModifyBuffValue(BuffType.TianJiDiSha, -1);
            if (dealt > 0) attacker.ModifyDef(dealt);
        }
        // 刻印地煞：加防（封顶）、消耗。
        if (dealt > 0 && attacker.HasBuff(BuffType.KeYinDiSha) && info.Type == DamageType.Attack)
        {
            int add = Math.Min(dealt, attacker.GetBuffValue(BuffType.KeYinDiSha));
            attacker.RemoveBuff(BuffType.KeYinDiSha);
            if (add > 0) attacker.ModifyDef(add);
        }
        // ---- 落伤后链（以下全部按「实际掉血 dealt」计，对应原码 OnHit 之后的 num）----
        // 刻印·无狂焰：命中即给对方挂外伤、消耗。
        if (dealt > 0 && attacker.GetBuffValue(BuffType.KeYinWuKuangYan) > 0 && info.Type == DamageType.Attack)
        {
            attacker.ModifyBuffValue(BuffType.KeYinWuKuangYan, -1);
            target.ModifyBuffValue(BuffType.WaiShang, 1);
        }
        // 刻印·拳之勇：伤害≥15 时按 1/5 转化水势、消耗。
        if (dealt >= 15 && attacker.HasBuff(BuffType.KeYinQuanZhiYong) && info.Type == DamageType.Attack)
        {
            attacker.ModifyBuffValue(BuffType.KeYinQuanZhiYong, -1);
            int n = (int)MathF.Floor(dealt / 5f);
            if (n > 0) attacker.ModifyBuffValue(BuffType.ShuiShi, n);
        }
        // 拳勇：命中消耗一层，按 1/5 转化水势。
        if (attacker.HasBuff(BuffType.QuanYong) && info.Type == DamageType.Attack)
        {
            attacker.ModifyBuffValue(BuffType.QuanYong, -1);
            if (dealt > 0)
            {
                int n = (int)MathF.Floor(dealt / 5f);
                if (n > 0) attacker.ModifyBuffValue(BuffType.ShuiShi, n);
            }
        }
        // 结晶鲛珠：命中按「伤害/2 × 层数」加防。
        if (attacker.HasBuff(BuffType.JieJingJiaoZhu) && info.Type == DamageType.Attack && dealt > 0)
        {
            attacker.ModifyDef(dealt / 2 * attacker.GetBuffValue(BuffType.JieJingJiaoZhu));
        }
        // 崩拳多形：当前牌为崩拳（baseId 144）或带崩拳多形时，命中按 60% 回血并记「消耗崩拳多形」。
        if (HasCurrentCard(attacker) && info.Type == DamageType.Attack
            && (CardTypes.BaseId(attacker.CurrentCardId) == 144
                || (attacker.HasBuff(BuffType.BengQuanDuoXing)
                    && (attacker.CurrentCardIsBengQuan || CardTypes.IsBengQuan(attacker, attacker.CurrentCardId))))
            && !attacker.HasBuff(BuffType.AfterCardAciton))
        {
            if (dealt > 0) attacker.ModifyHp((int)MathF.Floor(dealt * 0.6f));
            if (attacker.HasBuff(BuffType.BengQuanDuoXing)) attacker.SetBuffValue(BuffType.XiaoHaoBengQuanDuoXing, 1);
        }
        // 梦断拳：当前牌为 10030087/10040087 或带梦断拳时，命中按 40% 加体魄并记「梦断拳结束」。
        if (HasCurrentCard(attacker) && info.Type == DamageType.Attack
            && (attacker.CurrentCardId == 10030087 || attacker.CurrentCardId == 10040087
                || attacker.HasBuff(BuffType.MengDuanQuan))
            && !attacker.HasBuff(BuffType.AfterCardAciton))
        {
            if (dealt > 0) attacker.ModifyTiPo((int)MathF.Floor(dealt * 0.4f));
            if (attacker.HasBuff(BuffType.MengDuanQuan)) attacker.SetBuffValue(BuffType.MengDuanQuanJieShu, 1);
        }
        // 共鸣 11：本回合首次用「猫」牌打出伤害 → 给对方挂外伤（生效前 o[0]、生效后 o[1]）。早先缺失。
        if (attacker.Subs.HasTalentResonance(attacker, 11) && info.Type == DamageType.Attack
            && attacker.CurrentCardName.Contains("猫") && dealt > 0 && !attacker.CheckTalentResonanceTempFlag(11))
        {
            var r11 = attacker.Config.ResonanceOtherParams(11);
            target.ModifyBuffValue(BuffType.WaiShang, attacker.IsTalentResonanceEffective(11) ? r11.At(1) : r11.At(0));
            attacker.SetTalentResonanceTempFlag(11, true);
        }
        // ⚠ 原码在这里 `if (damageInfo.type != DamageType.Attack) return;`（12334）——
        //   下面的反震心法 / 梦•反震 / 拆招反弹 / 摘花飞叶 / 伤魂咒阵 / 攻击时加内伤…**只对攻击生效**。
        //   早先 sim 没有这个早退：水势、反伤、真伤等每一下都触发了它们（反震还会对反伤再反伤）。
        if (info.Type != DamageType.Attack) return;
        // 仙命 430 劫拳势：打出伤害时扣一层，拳架势下给对方挂减攻，否则挂虚弱。早先缺失。
        if (dealt > 0 && attacker.GetBuffValue(BuffType.JieQuanShi) > 0)
        {
            attacker.ModifyBuffValue(BuffType.JieQuanShi, -1);
            if (attacker.GetBuffValue(BuffType.QuanJiaShi) > 0) target.ModifyBuffValue(BuffType.JianGong, 1);
            else target.ModifyBuffValue(BuffType.XuRuo, 1);
        }
        // 反震心法：受击方按自身层数（刻印74 时再加星力/加攻）反伤回去。
        if (target.HasBuff(BuffType.FanZhenXinFa))
        {
            int back = target.GetBuffValue(BuffType.FanZhenXinFa);
            if (target.Subs.HasKeYinType(target, 74))
            {
                back += target.GetBuffValue(BuffType.XingLi);
                back += target.GetBuffValue(BuffType.JiaGong);
            }
            ApplyDamage(target, attacker, DamageInfo.Create(target, DamageType.ReflectDamage, back, skipWoundCheck: true));
        }
        // 梦•反震心法的反弹（原码 ApplyDamage 12360）：
        //     if (dst.HasBuff(MengFanZhenJianGong) && dst.HasBuff(MengFanZhenFanTan) && !HasBuff(MengFanZhenCiShu))
        // ⚠ 早先只判了 `HasBuff(MengFanZhenFanTan)` —— 而 FanTan 在 CalculateAttack 里被置位后**从不清除**，
        //   于是「持续 1 回合」的减伤 buff 过期之后，反弹**永远持续**（卡 4000078 偏差 43）。
        //   三个条件缺一不可：减伤 buff 仍在、有记录的反弹值、且不在反弹的递归里（CiShu 防重入）。
        if (target.HasBuff(BuffType.MengFanZhenJianGong) && target.HasBuff(BuffType.MengFanZhenFanTan)
            && !attacker.HasBuff(BuffType.MengFanZhenCiShu))
        {
            int back = target.GetBuffValue(BuffType.MengFanZhenFanTan);
            attacker.ModifyBuffValue(BuffType.MengFanZhenCiShu, 1);
            attacker.ModifyHp(-back);
            attacker.RemoveBuff(BuffType.MengFanZhenCiShu);
        }
        // 拆招+滚甲势：反弹伤害。
        if (target.HasBuff(BuffType.ChaiZhao) && target.HasBuff(BuffType.GunJiaShi))
            ApplyDamage(target, attacker, DamageInfo.Create(target, DamageType.ReflectDamage, attacker.Config.CardOtherParams(221).At(1), skipWoundCheck: true));
        // 摘花飞叶：双向加内伤。
        if (attacker.HasBuff(BuffType.ZhaiHuaFeiYe))
            target.ModifyBuffValue(BuffType.NeiShang, attacker.GetBuffValue(BuffType.ZhaiHuaFeiYe));
        if (target.HasBuff(BuffType.ZhaiHuaFeiYe))
            attacker.ModifyBuffValue(BuffType.NeiShang, target.GetBuffValue(BuffType.ZhaiHuaFeiYe));
        // 伤魂咒阵：加内伤、消耗（双向）。
        if (attacker.HasBuff(BuffType.ShangHunZhouZhenShiJiaNeiShangCiShu))
        {
            attacker.ModifyBuffValue(BuffType.ShangHunZhouZhenShiJiaNeiShangCiShu, -1);
            target.ModifyBuffValue(BuffType.NeiShang, 1);
        }
        if (target.HasBuff(BuffType.ShangHunZhouZhenShiJiaNeiShangCiShu))
        {
            target.ModifyBuffValue(BuffType.ShangHunZhouZhenShiJiaNeiShangCiShu, -1);
            attacker.ModifyBuffValue(BuffType.NeiShang, 1);
        }
        if (attacker.HasBuff(BuffType.GongJiShiShiJiaNeiShangHuiHeShu))
            target.ModifyBuffValue(BuffType.NeiShang, 1);
        if (attacker.GetBuffValue(BuffType.XiaCiGongJiShiJiaNeiShang) > 0)
        {
            int add = attacker.GetBuffValue(BuffType.XiaCiGongJiShiJiaNeiShang);
            attacker.RemoveBuff(BuffType.XiaCiGongJiShiJiaNeiShang);
            target.ModifyBuffValue(BuffType.NeiShang, add);
        }
    }
}
