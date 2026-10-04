namespace Yx.BattleSim.Model;

/// <summary>
/// 逐步对拍的事件流（与探针 arenaprobe 的「逐步对拍」批次**同一格式、同一打点位置**）：
///   · 出牌：<c>#L1000090</c>（临时牌 / 复制执行加 <c>*</c>）—— 对应原码 <c>CardActionBase.ExecuteEffect</c> 入口；
///   · 快照：<c>hp,def,max|hp,def,max</c>（左 | 右）—— 对应 <c>ModifyHp / ModifyDef / ModifyMaxHp</c> 返回之后；
///     与上一条快照相同则不记。
/// 事件之间用空格分隔。两边第一处不同就是分叉点。
/// </summary>
public static class StepLog
{
    [ThreadStatic] public static Action<string>? Sink;
    // 求解去重专用，独立于诊断日志，避免并行求解串场。
    [ThreadStatic] internal static Action<string, int, Combatant>? BehaviorSink;
    [ThreadStatic] private static string? _last;

    public static void Reset() => _last = null;

    public static void Card(Combatant src, int cardId, bool temp)
    {
        BehaviorSink?.Invoke(temp ? "temp" : "card", cardId, src);
        if (Sink is null) return;
        Sink("#" + src.Uid + cardId.ToString(System.Globalization.CultureInfo.InvariantCulture) + (temp ? "*" : ""));
    }

    public static void Snap(Combatant any)
    {
        BehaviorSink?.Invoke("state", 0, any);
        if (Sink is null) return;
        Combatant? l = any.Uid == "L" ? any : any.Opponent;
        Combatant? r = l?.Opponent;
        if (l is null || r is null) return;
        string s = Side(l) + "|" + Side(r);
        if (s == _last) return;
        _last = s;
        Sink(s);
    }

    private static string Side(Combatant c)
        => c.Hp.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
           + c.Def.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
           + c.MaxHp.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
