namespace Yx.BattleSim.Effects;

/// <summary>
/// 卡效果中断：对应原码卡效果里抛出的异常（被 <c>CardActionBase.ExecuteEffect</c> 的 try 接住、记日志后继续对局）。
/// 只用来复现原码**确定会抛**的路径（如非拳师角色的 SwitchJiaShi 空引用），由 <see cref="CardEffects"/> 的 ExecuteEffect 捕获。
/// </summary>
public sealed class CardEffectAbort(string reason) : System.Exception(reason);
