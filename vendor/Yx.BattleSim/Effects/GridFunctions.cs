using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 牌桌格子与相邻（忠实移植 <c>CardActionBase.GetNextGrid/GetPreviousGrid/GetNext(Previous)GridCardConfig</c>
/// + <c>CardConfig.IsWuXingCard</c> 扩展）。
///
/// 格子是**环形**：默认 8 格；<c>FanZhuanChuPai</c>（翻转出牌）把方向反掉；越界按格数取模回绕。
/// 环长取 <c>lastRoundData.unlockGrids</c>（= <see cref="Model.SideInput.UnlockGrids"/>，缺省 8；≤0 按 8，同原码）。
/// </summary>
public static class GridFunctions
{
    /// <summary>默认格数（游戏的 unlockGrids 缺失时也回退 8）。</summary>
    public const int DefaultGrids = 8;

    public static int UnlockGrids(Combatant c) => c.UnlockGrids > 0 ? c.UnlockGrids : DefaultGrids;

    /// <summary>GetNextGrid：下一格（FanZhuanChuPai 时反向），环形回绕。</summary>
    public static int GetNextGrid(Combatant src, int gridNumber)
    {
        int n = UnlockGrids(src);
        int g = src.HasBuff(BuffType.FanZhuanChuPai) ? gridNumber - 1 : gridNumber + 1;
        if (g < 0) g += n;
        if (g >= n) g %= n;
        return g;
    }

    /// <summary>GetPreviousGrid：上一格（FanZhuanChuPai 时反向），环形回绕。</summary>
    public static int GetPreviousGrid(Combatant src, int gridNumber)
    {
        int n = UnlockGrids(src);
        int g = src.HasBuff(BuffType.FanZhuanChuPai) ? gridNumber + 1 : gridNumber - 1;
        if (g < 0) g += n;
        if (g >= n) g %= n;
        return g;
    }

    /// <summary>GetNextGridCardConfig：该格上的牌（空格/越界返回 null）。</summary>
    public static BattleCard? NextGridCard(Combatant src, int gridNumber) => CardAt(src, GetNextGrid(src, gridNumber));

    /// <summary>GetPreviousGridCardConfig：该格上的牌（空格/越界返回 null）。</summary>
    public static BattleCard? PreviousGridCard(Combatant src, int gridNumber) => CardAt(src, GetPreviousGrid(src, gridNumber));

    private static BattleCard? CardAt(Combatant src, int grid)
        => grid >= 0 && grid < src.Board.Count ? src.Board[grid] : null;

    /// <summary>该格牌的名字（空格/越界返回空串）。相邻判定（相生/相克/激活）都按名做。</summary>
    public static string CardNameAt(Combatant src, int grid)
    {
        var c = CardAt(src, grid);
        return c is null ? "" : (c.Name ?? src.Config.CardName(c.Id));
    }

    /// <summary>CardConfig.IsWuXingCard：牌名含任一五行灵。</summary>
    public static bool IsWuXingCard(string name)
        => name.Contains("金灵") || name.Contains("木灵") || name.Contains("土灵")
        || name.Contains("水灵") || name.Contains("火灵");
}
