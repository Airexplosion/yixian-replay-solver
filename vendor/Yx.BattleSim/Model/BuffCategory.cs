namespace Yx.BattleSim.Model;

/// <summary>
/// buff 分类（忠实移植自游戏 enum BuffCategory，值必须一致——配置里存的就是这个序号）。
/// <see cref="Negative"/> 是「debuff」的判据：GetDebuffList / GetDebuffCount / RemoveAllDebuff 都按它筛。
/// </summary>
public enum BuffCategory
{
    Positive = 0,
    Negative = 1,
    Neutral = 2,
    Hidden = 3,
    Permanent = 4,
}
