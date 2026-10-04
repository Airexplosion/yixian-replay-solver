using System.Runtime.CompilerServices;
using Yx.BattleSim.Config;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Effects;

/// <summary>
/// 估值用的候选池：固定负面池与「随机使用 N 张牌」一族的卡池。
///
/// 卡池按笔记 §3.8 的筛选条件从卡表（<see cref="JsonBattleConfig"/>）里构造，**按配置实例缓存**
/// （<see cref="ConditionalWeakTable{TKey,TValue}"/>，并发求解线程共享、只读）。
/// 候选一律是 **1 级**（id 的等级位为 0，即 baseId）：除 10000065 写明「1 级效果」外，被选中的牌用几级服务器没告诉我们，
/// 笔记建议默认 1 级。卡表不是 <see cref="JsonBattleConfig"/>（测试桩）时池为空 → 估值 -1（效果消失，与原先一致）。
///
/// ⚠ 池子定义是按卡面描述 + 配置字段推的，还没有用真参数核对（笔记 §5）：
///   「门派牌」是否只限本方门派、362「专属牌」是否就是 Subcategory 6、被选牌的等级，都待 battlerec 录到真参数后校正。
/// </summary>
public static class ParamPools
{
    // BuffType 数值：100 内伤 101 虚弱 102 破绽 103 减攻 104 困缚 105 外伤 367 冥 393 食滞。
    private static readonly int[] PoolSix = [100, 103, 101, 102, 104, 105];
    private static readonly int[] PoolNeiXuWai = [100, 101, 105];
    private static readonly int[] PoolNeiXuWaiJian = [100, 101, 105, 103];
    private static readonly int[] PoolSeven = [100, 101, 102, 103, 104, 105, 367];
    /// <summary>刻印 161：8 种负面里挑，抽到困缚（104）时服务器改写成冥（367）→ 冥占两份。</summary>
    private static readonly int[] PoolKeYin161 = [100, 101, 102, 103, 367, 105, 367, 393];

    public static IReadOnlyList<int> Debuffs(ParamDebuffPool pool) => pool switch
    {
        ParamDebuffPool.Six => PoolSix,
        ParamDebuffPool.NeiXuWai => PoolNeiXuWai,
        ParamDebuffPool.NeiXuWaiJian => PoolNeiXuWaiJian,
        ParamDebuffPool.Seven => PoolSeven,
        ParamDebuffPool.KeYin161 => PoolKeYin161,
        _ => [],
    };

    // Subcategory 枚举（HotUpdate 405086）：0 无 1 机缘 2 法宝 3 灵宠 4 秘术 5 成长 6 专属 7 转换
    // 8 甜粽 9 咸粽 10 融会 11 TA18 12 幻 13 TA27 14 梦 15 马。
    private const int ScNone = 0, ScJiYuan = 1, ScArtifact = 2, ScPet = 3, ScMiShu = 4, ScGrowth = 5,
        ScExclusive = 6, ScZhuanHuan = 7, ScTianZong = 8, ScXianZong = 9, ScRongHui = 10, ScDream = 14, ScMa = 15;
    private const int LevelYuanYing = 4, LevelHuaShen = 5, LevelFanXu = 6;
    private const int SectBengQuan = 4;

    private sealed class Cache
    {
        public readonly object Gate = new();
        public readonly Dictionary<int, int[]> Pools = new();
    }

    private static readonly ConditionalWeakTable<IBattleConfig, Cache> Caches = new();

    /// <summary>
    /// 取值方 <paramref name="c"/> 视角下的卡池（候选卡 id 列表，已排序、已缓存）。
    /// 只有 <see cref="ParamCardPool.OtherSect"/> 依赖取值方（本方宗门）。
    /// </summary>
    public static IReadOnlyList<int> Cards(Combatant c, ParamCardPool pool)
    {
        if (c.Config is not JsonBattleConfig cfg) return [];
        int sect = pool switch
        {
            ParamCardPool.OtherSect => c.Sect,
            ParamCardPool.HuaShenSameWuXing => PreviousGridWuXing(c),
            _ => 0,
        };
        if (pool == ParamCardPool.HuaShenSameWuXing && sect == 0) return [];
        int key = (int)pool * 16 + sect;
        var cache = Caches.GetValue(cfg, _ => new Cache());
        lock (cache.Gate)
        {
            if (!cache.Pools.TryGetValue(key, out var list))
            {
                list = Build(cfg, pool, sect);
                cache.Pools[key] = list;
            }
            return list;
        }
    }

    private static int[] Build(JsonBattleConfig cfg, ParamCardPool pool, int sect)
    {
        var list = new List<int>();
        foreach (var kv in cfg.AllCards)
        {
            if (CardTypes.Rarity(kv.Key) != 0) continue;   // 只取 1 级
            if (Matches(kv.Key, kv.Value, pool, sect)) list.Add(kv.Key);
        }
        list.Sort();
        return list.ToArray();
    }

    private static readonly string[] WuXingNames = ["", "金灵", "木灵", "水灵", "火灵", "土灵"];

    private static int WuXingIndex(string name)
    {
        string wx = WuXingFunctions.GetWuXingName(name);
        return wx.Length == 0 ? 0 : Array.IndexOf(WuXingNames, wx);
    }

    /// <summary>取值方当前格的前一格牌的五行（1..5；没有 → 0）。</summary>
    private static int PreviousGridWuXing(Combatant c)
    {
        var prev = GridFunctions.PreviousGridCard(c, c.CurrentCardGrid);
        return prev is null ? 0 : System.Math.Max(0, WuXingIndex(CardTypes.NameOf(c, prev.Id)));
    }

    /// <summary>
    /// 7040075 梦•浑天印（化神档）：「使用 1 张与前一格牌相同[五行]和等级的化神期[常规牌]」。
    /// 在同五行的化神期常规牌里挑一张，换成前一格牌的等级（该等级不存在时退回 1 级）；前一格没有五行 → -1。
    /// </summary>
    public static int PickHuaShenSameWuXing(Combatant c)
    {
        int id = ParamEstimator.Pick(Cards(c, ParamCardPool.HuaShenSameWuXing));
        if (id == -1 || c.Config is not JsonBattleConfig cfg) return id;
        var prev = GridFunctions.PreviousGridCard(c, c.CurrentCardGrid);
        int tiered = id + (prev is null ? 0 : CardTypes.Rarity(prev.Id)) * 10000;
        return cfg.Card(tiered) is null ? id : tiered;
    }

    /// <summary>可抽到的门派牌：宗门 1..4、常规子类、非隐藏。</summary>
    private static bool IsSect(JsonBattleConfig.CardMeta m)
        => m.Sect is >= 1 and <= 4 && m.Subcategory == ScNone && !m.Hidden;

    /// <summary>可抽到的副职牌：职业 1..7、非隐藏（成长 / 转换子类的副职牌也算）。</summary>
    private static bool IsCareer(JsonBattleConfig.CardMeta m)
        => m.Career is >= 1 and <= 7 && !m.Hidden && m.Subcategory is ScNone or ScGrowth or ScZhuanHuan;

    private static bool HasAttack(JsonBattleConfig.CardMeta m)
        => m.Attack > 0 || m.RandomAttack > 0 || m.Desc.Contains("{attack}");

    private static bool Matches(int id, JsonBattleConfig.CardMeta m, ParamCardPool pool, int sect) => pool switch
    {
        ParamCardPool.Sect => IsSect(m),
        ParamCardPool.SectOrCareer => IsSect(m) || IsCareer(m),
        ParamCardPool.SectAttack => IsSect(m) && HasAttack(m),
        ParamCardPool.SectNoAttack => IsSect(m) && !HasAttack(m),
        ParamCardPool.OtherSect => IsSect(m) && (sect == 0 || m.Sect != sect),
        ParamCardPool.ZongZi => m.Subcategory is ScTianZong or ScXianZong,
        ParamCardPool.ActionAgainOrShenFa => (IsSect(m) || IsCareer(m))
            && (m.ActionAgain || m.Desc.Contains("再次行动") || m.Desc.Contains("身法")),
        ParamCardPool.MaName => m.Name.Contains('马') && m.Subcategory != ScMa,
        ParamCardPool.MiShu => m.Subcategory == ScMiShu && m.Sect is >= 1 and <= 4,
        ParamCardPool.Exclusive => m.Subcategory == ScExclusive,
        ParamCardPool.HuaShenDream => m.Subcategory == ScDream && m.Level == LevelHuaShen,
        ParamCardPool.JiYuan => m.Subcategory == ScJiYuan,
        ParamCardPool.FanXu => m.Level == LevelFanXu && m.Subcategory != ScMa,
        ParamCardPool.HighTreasure => (m.Subcategory is ScArtifact or ScPet or ScMiShu) && m.Level >= LevelYuanYing,
        ParamCardPool.KuangJian => m.Name.Contains("狂剑") && !m.Name.StartsWith("极•", StringComparison.Ordinal)
            && (m.Subcategory is ScNone or ScMiShu or ScRongHui) && id != 186,
        ParamCardPool.BengQuan => m.Name.Contains("崩拳") && m.Sect == SectBengQuan && m.Subcategory == ScNone && !m.Hidden,
        ParamCardPool.HuaShenSameWuXing => m.Subcategory == ScNone && m.Level == LevelHuaShen && !m.Hidden
            && WuXingIndex(m.Name) == sect,
        _ => false,
    };
}
