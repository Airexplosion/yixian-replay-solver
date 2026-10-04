namespace Yx.BattleSim.Model;

/// <summary>
/// 玩家选牌参数队列，对应游戏 <c>battleExecuter.battleParamsQueue</c>（10738 <c>GetNextParam</c> 里 <c>Dequeue()</c>）。
///
/// **它是全局共享的一条队列，不是每方一条** —— 队列挂在 <c>battleExecuter</c> 上（不是 <c>BattleCharacter</c>），
/// 所以 L 和 R 谁先取走就是谁的，游标单向前进。早先本 sim 把队列和游标都放在 <see cref="Combatant"/> 上，
/// 双方各从 0 开始 —— 只有在「仅一方消耗参数」时偶然正确，两边都消耗时取到的值会错位。
///
/// 取空时返回 -1（不是 0）：游戏那边 Dequeue 抛异常、catch 里
/// <c>Debug.LogError("参数获取失败"); return -1;</c>，而卡体会据此**回退到真掷骰**，例如
/// <c>int num2 = src.GetNextRandomValue(); if (num2 == -1) num2 = UnityEngine.Random.Range(lo, hi + 1);</c>。
/// 返回 0 的话那个分支永远不触发，这些牌的效果会静默变成「取到 0」。
/// 与游戏一致：取空时游标**不前进**（抛异常时队列不变）。
///
/// 可变是刻意的：它就是引擎里那条可变队列，语义要求原地前进；每场战斗新建一条，
/// 不跨场共享 —— 所以并发按场隔离，结算器对外仍是纯函数。
/// </summary>
public sealed class BattleParamQueue
{
    private readonly System.Collections.Generic.IReadOnlyList<int> _items;
    private int _cursor;

    public BattleParamQueue(System.Collections.Generic.IReadOnlyList<int> items) => _items = items;

    /// <summary>空队列：任何取值都返回 -1（未采集参数时的中性行为）。</summary>
    public static BattleParamQueue Empty { get; } =
        new BattleParamQueue(System.Array.Empty<int>());

    /// <summary>取下一个参数；队列用尽返回 -1 且游标不前进。</summary>
    public int Next() => _cursor < _items.Count ? _items[_cursor++] : -1;
}
