using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// **出牌前的拦截链**：一张牌在真正执行效果**之前**，先由持有者身上的若干 buff 对它做手脚
/// （升级 / 降级 / 复制 / 变形 / 吸取生命）。对应原码 <c>CardActionBase.Execute</c> 状态机里
/// 串联的那一串拦截段，yisim 把它整理成了同名的 <c>do_*(card_idx)</c> 家族（<c>gamestate_full.js</c>）。
///
/// 关键性质：**改写是永久的** —— 原码直接改常驻 <c>CardItem</c> 的 <c>cardInfo.id</c>，yisim 同样写回
/// <c>cards[idx]</c>。所以 <see cref="Combatant.ReplaceCard"/> 写的是本场盘面，之后每回合取到的都是新牌。
/// 调用点必须在 <c>ExecuteEffect</c> **之前**：降级/复制之后执行的是**换过之后**那张牌（连攻击数值一起变）。
///
/// 已实现的升级链包括相生相成、画龙点睛、崆峒印、下张木灵/云剑、咸蛋黄粽、
/// 忘白鹿和下次狂剑升级；相生判定读取 UsedWuXing，升级后的牌写回本场盘面。
/// 逍遥复刻在调用方付费后执行；此处不负责回合外的永久卡牌存档。
/// </summary>
internal static class PrePlayFunctions
{
    /// <summary>幽冥化生壶（卡 181）——从它身上读「吸取」的数值 otherParams[1]。</summary>
    private const int LianYaoHuCardId = 181;

    /// <summary>升一档稀有度对应的 id 增量（<c>cardInfo.id += 10000</c>；BaseId 的反函数）。</summary>
    private const int RarityStep = 10000;

    /// <summary>
    /// 出牌前跑一遍拦截链。<paramref name="grid"/> = 这张牌所在的格位（连音要读前一格）。
    /// 可能<b>就地换掉 <paramref name="card"/> 所在格的牌</b>，调用方随后要重新取一次牌。
    /// </summary>
    public static void Apply(Combatant actor, Combatant foe, BattleCard card, int grid)
    {
        // ⚠ 顺序照原码 `CardActionBase.Execute`（d__11）的 goto 链（322 复刻在调用方、付费之后已处理）：
        //   连音 → 幽冥虚魂拳 → 崆峒印 → 升级下张木灵 → 相生相成 → 咸蛋黄粽 → 刻印·忘白鹿 → 画龙点睛
        //   → 升级下张云剑 → 升级下次狂剑 → 炼妖壶 → 下次牌降级。
        // 每一条都作用在**前面各条改写之后**的那张牌上（原码每次改写都 `cardConfig = cardItem.cardConfig`），
        //   所以每步之后重新从盘面取牌。早先 sim 把炼妖壶放在最前、咸蛋黄粽命中就 return（后面几条不再判），
        //   且缺 7 条（幽冥虚魂拳 / 升级下张木灵 / 相生相成 / 忘白鹿 / 升级下张云剑 / 升级下次狂剑 / 下次牌降级）。
        ApplyContinuousTune(actor, card, grid);
        card = Cur(actor, card, grid);

        // 幽冥虚魂拳：这张牌变成**同稀有度的普攻**（cardInfo.id = rarity × 10000）。
        if (actor.HasBuff(BuffType.YouMingXuHunQuan))
        {
            actor.ModifyBuffValue(BuffType.YouMingXuHunQuan, -1);
            Replace(actor, grid, actor.Config.CardRarity(card.Id) * RarityStep);
            card = Cur(actor, card, grid);
        }
        card = UpgradeIf(actor, card, grid, BuffType.KongTongYin, true);
        card = UpgradeIf(actor, card, grid, BuffType.ShengJiXiaZhangMuLingPai, actor.Config.CardName(card.Id).Contains("木灵"));
        card = UpgradeIf(actor, card, grid, BuffType.XiangShengXiangCheng,
            actor.HasBuff(BuffType.UsedWuXing)
            && WuXingFunctions.IsXiangSheng((BuffType)actor.GetBuffValue(BuffType.UsedWuXing), actor.Config.CardName(card.Id), actor.HasTalent(137)));
        // 咸蛋黄粽：**先消耗**；升得动就升，升不动 +2 食欲。（不 return —— 后面各条照常判。）
        if (actor.HasBuff(BuffType.XianDanHuangZong))
        {
            actor.ModifyBuffValue(BuffType.XianDanHuangZong, -1);
            if (CanUpgrade(actor, card)) { Replace(actor, grid, card.Id + RarityStep); card = Cur(actor, card, grid); }
            else actor.ModifyBuffValue(BuffType.ShiYu, 2);
        }
        card = UpgradeIf(actor, card, grid, BuffType.KeYinWangBaiLu, CardTypes.IsLingJian(actor, card.Id));
        card = UpgradeIf(actor, card, grid, BuffType.HuaLongDianJing, true);
        card = UpgradeIf(actor, card, grid, BuffType.ShengJiXiaZhangYunJian, CardTypes.IsYunJian(actor, card.Id));
        // 升级下次狂剑（卡 1030076 施加）：血量高于 o[2]、狂剑、**稀有度 0** 才升级，并扣 o[2] 生命。
        int kjCost = actor.Config.CardOtherParams(1030076).At(2);
        if (actor.HasBuff(BuffType.ShengJiXiaCiKuangJian) && CanUpgrade(actor, card) && actor.Hp > kjCost
            && CardTypes.IsKuangJian(actor, card.Id) && actor.Config.CardRarity(card.Id) == 0)
        {
            actor.ModifyBuffValue(BuffType.ShengJiXiaCiKuangJian, -1);
            actor.ModifyHp(-kjCost);
            Replace(actor, grid, card.Id + RarityStep);
            card = Cur(actor, card, grid);
        }
        ConsumeLianYaoHu(actor, foe, card, grid);
        card = Cur(actor, card, grid);
        // 下次牌降级（对手的刻印 87 / 回合 tick 施加）：可降级则扣一层并降一档。
        if (actor.HasBuff(BuffType.XiaCiPaiJiangJi) && CanDowngrade(actor, card))
        {
            actor.ModifyBuffValue(BuffType.XiaCiPaiJiangJi, -1);
            Replace(actor, grid, card.Id - RarityStep);
        }
        // （「下张牌激活五行」在 CardHookFunctions.OnBeforeExecuted 里已有实现，这里不再重复。）
    }

    /// <summary>「门控升级」的通用形状：`if (HasBuff(b) &amp;&amp; CanUpgrade() &amp;&amp; extra) { −1 层; Upgrade(); }`（不可升级时连层都不消耗）。</summary>
    private static BattleCard UpgradeIf(Combatant actor, BattleCard card, int grid, BuffType b, bool extra)
    {
        if (!actor.HasBuff(b) || !CanUpgrade(actor, card) || !extra) return card;
        actor.ModifyBuffValue(b, -1);
        Replace(actor, grid, card.Id + RarityStep);
        return Cur(actor, card, grid);
    }

    /// <summary>就地换牌（原码改写常驻 CardItem 的 cardInfo.id，永久）。格位无效（普攻复位格）时不动。</summary>
    private static void Replace(Combatant actor, int grid, int newId)
    {
        if (grid < 0 || grid >= actor.Board.Count) return;
        actor.ReplaceCard(grid, actor.Config.BuildCard(newId));
    }

    /// <summary>重新取这一格当前的牌（前面的改写可能已换掉它）。</summary>
    private static BattleCard Cur(Combatant actor, BattleCard card, int grid)
        => grid >= 0 && grid < actor.Board.Count ? actor.Board[grid] : card;

    // ─────────────────────────────────────────────────────────────────────
    // 炼妖壶（卡 181）：持有者每次出牌消耗一层；这张牌可降级（rarity ≥ 1 且不是 19）则降一档，
    // 否则吸取 181 的 o[1] 点生命及上限（原码 21219–21310；yisim do_spirit_fusion_pot 逐行印证）。
    // ⚠ 判据是**稀有度**不是 CanUpgrade()：普攻 noUpgrade=0（CanUpgrade 为真）但 rarity=0（不可降级）。
    // ─────────────────────────────────────────────────────────────────────
    private static void ConsumeLianYaoHu(Combatant actor, Combatant foe, BattleCard card, int grid)
    {
        if (!actor.HasBuff(BuffType.LianYaoHu)) return;
        actor.ModifyBuffValue(BuffType.LianYaoHu, -1);

        if (CanDowngrade(actor, card))
        {
            Replace(actor, grid, card.Id - RarityStep);
            return;
        }
        int drain = actor.Config.CardOtherParams(LianYaoHuCardId).At(1);
        actor.ModifyHp(-drain);
        actor.ModifyMaxHp(-drain);
        foe.ModifyMaxHp(drain);
        foe.ModifyHp(drain);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 连音（卡 206 逍遥连音曲，buff BengTianXiaoYaoQu）：**出普攻时**把本格换成「前一格的牌」
    // 反编译 20825：
    //   if (HasBuff(BengTianXiaoYaoQu) && IsPuTongGongJi(cardInfo.id))
    //   {
    //       ModifyBuffValue(BengTianXiaoYaoQu, -1);
    //       int dstCardId = GetPreviousGridCardConfig(src, grid).id;
    //       cardItem.cardInfo.id = (前一格 noUpgrade || == 19) ? dstCardId : dstCardId + 10000;
    //   }
    // 即「复制**并升级一档**」；前一格不可升级才原样复制。卡面写的是「双方的下一张普攻变其上一格的牌的升级」。
    // ─────────────────────────────────────────────────────────────────────
    private static void ApplyContinuousTune(Combatant actor, BattleCard card, int grid)
    {
        if (!actor.HasBuff(BuffType.BengTianXiaoYaoQu)) return;
        if (!CardTypes.IsPuTongGongJi(card.Id)) return;      // 只对普通攻击生效

        BattleCard? prev = GridFunctions.PreviousGridCard(actor, grid);
        if (prev is null) return;

        actor.ModifyBuffValue(BuffType.BengTianXiaoYaoQu, -1);
        int prevId = prev.Id;
        int copyId = prevId != 19 && actor.Config.CanUpgrade(prevId) ? prevId + RarityStep : prevId;
        actor.ReplaceCard(grid, actor.Config.BuildCard(copyId));
    }

    /// <summary>
    /// 能否升级这张牌：原码统一走 <c>cardConfig.CanUpgrade()</c>（唯一输入是 noUpgrade），
    /// 另外 19 号牌是任何升级/降级效果都跳过的例外。
    /// </summary>
    private static bool CanUpgrade(Combatant c, BattleCard card)
        => card.Id != 19 && c.Config.CanUpgrade(card.Id);

    /// <summary>
    /// 能否**降级**这张牌：原码统一走 <c>cardConfig.rarity &gt;= 1 &amp;&amp; cardConfig.id != 19</c>。
    /// **和 <see cref="CanUpgrade"/> 不是一回事**：普攻 noUpgrade=0（可升级）但 rarity=0（不可降级）。
    /// </summary>
    private static bool CanDowngrade(Combatant c, BattleCard card)
        => card.Id != 19 && c.Config.CardRarity(card.Id) >= 1;
}
