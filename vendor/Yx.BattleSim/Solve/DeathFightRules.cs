using Yx.BattleSim.Model;

namespace Yx.BattleSim.Solve;

public static class DeathFightRules
{
    public static SideInput Prepare(SideInput side, bool second, bool tied)
    {
        var buffs = new Dictionary<int, int>(side.PermanentBuffs);
        buffs.Remove((int)BuffType.SiZhanZhiZhi);
        buffs.Remove((int)BuffType.SiZhanBuDao);
        if (second && (tied || buffs.GetValueOrDefault((int)BuffType.HouShouBiDingSiZhan) > 0))
            buffs[(int)BuffType.SiZhanZhiZhi] = 1;
        return side with { PermanentBuffs = buffs };
    }
}
