using Yx.BattleSim.Config;
using Yx.BattleSim.Effects;

namespace Yx.BattleSim;

/// <summary>
/// 数据目录（battle_config / card_effects）。默认在程序输出目录旁找 <c>*.local.json</c>；
/// 作为原生库随 mod 分发时由宿主在**第一场结算之前**调 <see cref="Initialize"/> 指到 mod 的 data 目录
/// （文件名可带或不带 <c>.local</c>）。
/// </summary>
public static class SimData
{
    /// <summary>显式指定的数据目录；null = 用 <see cref="AppContext.BaseDirectory"/>。</summary>
    public static string? Directory { get; private set; }

    /// <summary>按目录与文件基名找数据文件：先 <c>base.json</c>，再 <c>base.local.json</c>；都没有返回 null。</summary>
    public static string? Find(string baseName)
    {
        string dir = Directory ?? AppContext.BaseDirectory;
        foreach (string name in new[] { baseName + ".json", baseName + ".local.json" })
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// 指定数据目录并立即加载。必须在任何结算之前调用（<c>BattleResolver</c> 的默认配置在首次使用时定型）。
    /// 返回缺失的文件名列表（空 = 全部就绪）。
    /// </summary>
    public static IReadOnlyList<string> Initialize(string dataDirectory)
    {
        Directory = dataDirectory;
        var missing = new List<string>();
        if (!JsonBattleConfig.ReloadDefault()) missing.Add("battle_config.json");
        if (!CardEffectData.ReloadFromDataDirectory()) missing.Add("card_effects.json");
        return missing;
    }
}
