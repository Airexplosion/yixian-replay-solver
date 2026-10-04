using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 「按格位标记」的 buff —— 一个 buff 的**位图**，第 i 位 = 第 i 格被标记。
/// 忠实移植自 <c>BattleCharacter</c> 上两组同构的方法：
///   · <c>XingWei</c>（星位，buff 311）：<c>IsXingWei/AddXingWei</c> + <c>CheckXingWei</c>；
///   · <c>MengEJie</c>（梦厄劫跳过格，buff 306）：<c>IsMengEJieSkipPos/AddMengEJieSkipPos/RemoveMengEJieSkipPos</c>。
///
/// 为什么不是「每格一个 buff」：游戏存的是位图，所以<b>「该格是否标记」在 buff 值里是 bit，不是计数</b>。
/// 直接对 <c>XingWei</c> 用 ModifyBuffValue 加减会得到完全不同的语义 —— 必须走这里的位运算。
///
/// ⚠ 两处容易漏掉的真效果：
///   1. <see cref="AddXingWei"/> 对**已标记**的格不重复置位，而是 <c>ModifyAnima(1)</c>（游戏文案「星位聚灵」）；
///   2. <see cref="CheckXingWei"/> 在 <c>LongMaJingShen</c>（龙马精神）下**任意格都算星位**。
/// </summary>
public static class GridMarkFunctions
{
    /// <summary>格位数（与 <see cref="GridFunctions.DefaultGrids"/> 同源）。</summary>
    private const int Grids = 8;

    // ---- 星位（XingWei）----

    /// <summary>IsXingWei：该格是否被标记为星位。忠实照抄（含未归一化入参的原始移位语义）。</summary>
    public static bool IsXingWei(Combatant c, int gridNumber)
        => (c.GetBuffValue(BuffType.XingWei) & (1 << gridNumber)) > 0;

    /// <summary>
    /// AddXingWei：归一格号后置位；**已经是星位则不置位，改为 +1 修为**（「星位聚灵」）。
    /// </summary>
    public static void AddXingWei(Combatant c, int gridNumber)
    {
        int g = Normalize(gridNumber);
        if (!IsXingWei(c, g))
            c.SetBuffValue(BuffType.XingWei, c.GetBuffValue(BuffType.XingWei) | (1 << g));
        else
            c.ModifyAnima(1);
    }

    /// <summary>CheckXingWei：该格是星位，或身上有龙马精神（全格视为星位）。</summary>
    public static bool CheckXingWei(Combatant c, int gridNumber)
        => IsXingWei(c, gridNumber) || c.HasBuff(BuffType.LongMaJingShen);

    // ---- 梦厄劫跳过格（MengEJie）----

    /// <summary>IsMengEJieSkipPos：该格是否被梦厄劫标记为跳过。</summary>
    public static bool IsMengEJieSkipPos(Combatant c, int gridNumber)
        => (c.GetBuffValue(BuffType.MengEJie) & (1 << gridNumber)) > 0;

    /// <summary>AddMengEJieSkipPos：归一后置位（已置位不做任何事，**没有**星位那种 +修为 的副作用）。</summary>
    public static void AddMengEJieSkipPos(Combatant c, int gridNumber)
    {
        int g = Normalize(gridNumber);
        if (!IsMengEJieSkipPos(c, g))
            c.SetBuffValue(BuffType.MengEJie, c.GetBuffValue(BuffType.MengEJie) | (1 << g));
    }

    /// <summary>RemoveMengEJieSkipPos：归一后清位。</summary>
    public static void RemoveMengEJieSkipPos(Combatant c, int gridNumber)
    {
        int g = Normalize(gridNumber);
        if (IsMengEJieSkipPos(c, g))
            c.SetBuffValue(BuffType.MengEJie, c.GetBuffValue(BuffType.MengEJie) & ~(1 << g));
    }

    /// <summary>格号归一：负数 +8、超界取模（照抄原码，故入参越界也不抛）。</summary>
    private static int Normalize(int gridNumber)
    {
        if (gridNumber < 0) gridNumber += Grids;
        if (gridNumber > Grids - 1) gridNumber %= Grids;
        return gridNumber;
    }
}
