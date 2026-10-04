using Yx.BattleSim.Model;

namespace Yx.BattleSim.Combat;

/// <summary>
/// 胜负判定与生命伤害。忠实移植自 BattleExecuter.DeathCheck / BattleCharacter.CheckSiZhan /
/// BattleExecuter.CalLifeDamage（数值公式已逐行核对反编译）。
/// 注：CalLifeDamage 里的 YuanGu / TA21 / VersusRelateAll 等特殊模式此处不实现（PvP 标准模式走通用公式），
/// 需要时按覆盖账补齐。
/// </summary>
public static class DeathRules
{
    /// <summary>
    /// 死战：hp&lt;=0 且带「死战之志」→ 转「死战不倒」并消耗之；返回是否处于「死战不倒」。
    /// 复现游戏 CheckSiZhan 的副作用（会改 buff），故按可变状态机调用。
    /// </summary>
    public static bool CheckSiZhan(Combatant c)
    {
        if (c.Hp <= 0 && c.HasBuff(BuffType.SiZhanZhiZhi))
        {
            c.ModifyBuffValue(BuffType.SiZhanBuDao, 1);
            c.RemoveBuff(BuffType.SiZhanZhiZhi);
        }
        return c.HasBuff(BuffType.SiZhanBuDao);
    }

    /// <summary>
    /// 战斗是否结束（有一方判负）。忠实 DeathCheck：死战不倒且血不高于对方时不结束。
    /// </summary>
    public static bool DeathCheck(Combatant left, Combatant right)
    {
        if (CheckSiZhan(left) && left.Hp <= right.Hp) return false;
        if (CheckSiZhan(right) && right.Hp <= left.Hp) return false;
        if (left.Hp > 0) return right.Hp <= 0;
        return true;
    }

    /// <summary>
    /// 生命伤害（真正扣的 meta 血）。gap = 胜方 hp − 负方 hp。
    /// 公式逐行对应 CalLifeDamage 的通用分支。
    /// </summary>
    public static int CalLifeDamage(Combatant winner, Combatant loser, int round, bool fastMode,
        int fate21Param = 0)
    {
        int gap = winner.Hp - loser.Hp;
        int num;
        if (gap <= 20)
        {
            num = round + 1 + CeilDiv(gap, 5);
        }
        else
        {
            int extra = CeilDiv(gap - 20, 10);
            int cap = 2 + round / 2;
            if (extra > cap) extra = cap;
            num = round + 5 + extra;
        }
        if (round <= 9) num--;

        if (fastMode) num = (int)MathF.Floor(num * 1.5f);

        if (winner.HasFateStrategy(21))
        {
            num -= fate21Param;
            if (num < 1) num = 1;
        }
        return num;
    }

    /// <summary>ceil(a/b) for non-negative a（对应游戏 Mathf.CeilToInt((float)a/b)）。</summary>
    private static int CeilDiv(int a, int b) => (a + b - 1) / b;
}
