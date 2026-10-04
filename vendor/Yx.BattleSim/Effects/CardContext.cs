using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 「当前牌」上下文（<see cref="Combatant"/> 上的 CurrentCard* 字段）的设置与快照。
/// 对应原码里 <c>cardItem.cardConfig</c> 这个活对象：临时执行另一张牌时 <c>InitData(其他牌)</c> 把它换掉，
/// 执行完 <c>InitData(原牌)</c> 换回 —— 钩子与伤害管线读到的「当前牌」随之变化。
/// </summary>
internal readonly struct CardContext
{
    private readonly int _baseId, _id;
    private readonly string _name;
    private readonly bool _bengQuan, _kuangJian, _lingJian, _puTong;

    private CardContext(Combatant c)
    {
        _baseId = c.CurrentCardBaseId;
        _id = c.CurrentCardId;
        _name = c.CurrentCardName;
        _bengQuan = c.CurrentCardIsBengQuan;
        _kuangJian = c.CurrentCardIsKuangJian;
        _lingJian = c.CurrentCardIsLingJian;
        _puTong = c.CurrentCardIsPuTong;
    }

    public static CardContext Capture(Combatant c) => new(c);

    public void Restore(Combatant c)
    {
        c.CurrentCardBaseId = _baseId;
        c.CurrentCardId = _id;
        c.CurrentCardName = _name;
        c.CurrentCardIsBengQuan = _bengQuan;
        c.CurrentCardIsKuangJian = _kuangJian;
        c.CurrentCardIsLingJian = _lingJian;
        c.CurrentCardIsPuTong = _puTong;
    }

    /// <summary>把「当前牌」设成 <paramref name="card"/>（牌型标志按设置时的状态算一次）。</summary>
    public static void Set(Combatant c, BattleCard card)
    {
        c.CurrentCardBaseId = CardTypes.BaseId(card.Id);
        c.CurrentCardName = card.Name ?? c.Config.CardName(card.Id);
        c.CurrentCardId = card.Id;
        c.CurrentCardIsBengQuan = CardTypes.IsBengQuan(c, card.Id);
        c.CurrentCardIsKuangJian = CardTypes.IsKuangJian(c, card.Id);
        c.CurrentCardIsLingJian = CardTypes.IsLingJian(c, card.Id);
        c.CurrentCardIsPuTong = CardTypes.IsPuTongGongJi(card.Id);
    }
}
