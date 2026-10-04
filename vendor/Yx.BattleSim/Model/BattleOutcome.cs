namespace Yx.BattleSim.Model;

/// <summary>一场战斗的结算结果。摆牌 AI 的目标函数以 <see cref="EndHpGap"/> 为主（最大化己方结束血差）。</summary>
public sealed record BattleOutcome
{
    /// <summary>胜方 uid；平局（双方同时判负的边界）为 null。</summary>
    public string? WinnerUid { get; init; }

    /// <summary>结束时己方(左) hp。</summary>
    public int LeftHp { get; init; }

    /// <summary>结束时对方(右) hp。</summary>
    public int RightHp { get; init; }

    /// <summary>血差 = 胜方 hp − 负方 hp（进 CalLifeDamage 的 gap）。无胜方为 0。</summary>
    public int EndHpGap { get; init; }

    /// <summary>真正扣的 meta 生命伤害（CalLifeDamage 结果）。</summary>
    public int LifeDamage { get; init; }

    /// <summary>结算到第几段/回合内步数（诊断用）。</summary>
    public int Steps { get; init; }
}
