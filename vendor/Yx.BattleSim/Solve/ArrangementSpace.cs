namespace Yx.BattleSim.Solve;

/// <summary>
/// 摆法空间：牌池是多重集（同 id 的牌互换位置结果一样，只算一次），从中选 <see cref="Slots"/> 张排成一排。
/// 内部用「种类下标」表示一张牌（<see cref="TypeIds"/>[t] = 卡 id），一套摆法 = 每格一个种类下标。
/// 空格（补位牌 <see cref="PadType"/>，默认 id 0 = 普通攻击，数量不限）永远可选。
/// </summary>
public sealed class ArrangementSpace
{
    /// <summary>种类下标 → 卡 id。</summary>
    public int[] TypeIds { get; }

    /// <summary>每种牌的可用张数（补位牌为 <see cref="int.MaxValue"/>）。</summary>
    public int[] Counts { get; }

    public int Slots { get; }

    /// <summary>空格（补位牌）的种类下标。</summary>
    public int PadType { get; }

    /// <summary>每种牌是否是「消耗 / 持续」牌（受 <see cref="MaxLimited"/> 限制）。</summary>
    public bool[] Limited { get; }

    /// <summary>
    /// 消耗牌 + 持续牌合计上限（游戏 <c>CardPanel.Get_MAX_CONSUMED_CONTINUOUS_CARD_COUNT()</c>：
    /// 2 + 刻印类型 64 张数 + 永久 buff JiaChiXuXiaoHaoWei）；&lt;0 = 不限。
    /// </summary>
    public int MaxLimited { get; }

    /// <param name="isLimited">这张牌是否消耗 / 持续牌（null = 都不是）。</param>
    /// <param name="placeable">这张牌能否上场（炼化牌 / 变化牌 / id 1 不能；null = 都能）。不能上场的直接不进牌池。</param>
    public ArrangementSpace(IReadOnlyList<int> pool, int slots, int padId,
        Func<int, bool>? isLimited = null, int maxLimited = -1, Func<int, bool>? placeable = null)
    {
        if (slots <= 0 || slots > 10) throw new ArgumentOutOfRangeException(nameof(slots), "格数须在 1..10（摆法键每格 6 位）");
        var ids = new List<int>();
        var counts = new List<int>();
        foreach (int id in pool)
        {
            if (placeable is not null && !placeable(id)) continue;
            int k = ids.IndexOf(id);
            if (k < 0) { ids.Add(id); counts.Add(1); }
            else counts[k]++;
        }
        // 空格永远可选：游戏牌桌 0 = 空格，战斗里按普通攻击打（B7 探针补空位即 0，实机一致）。
        // 所以「留空一格」不只是牌不够时的补位 —— 放一张差牌可能不如空着；消耗 / 持续上限卡住时也靠它填满。
        {
            int k = ids.IndexOf(padId);
            if (k < 0) { ids.Add(padId); counts.Add(int.MaxValue); k = ids.Count - 1; }
            else counts[k] = int.MaxValue;
            PadType = k;
        }
        if (ids.Count > 60) throw new ArgumentException("牌池种类过多（>60）");
        TypeIds = ids.ToArray();
        Counts = counts.ToArray();
        Slots = slots;
        MaxLimited = maxLimited;
        Limited = new bool[TypeIds.Length];
        for (int t = 0; t < TypeIds.Length; t++)
            Limited[t] = t != PadType && isLimited is not null && isLimited(TypeIds[t]);
    }

    /// <summary>摆法是否满足消耗 / 持续上限。</summary>
    public bool Valid(byte[] arr)
    {
        if (MaxLimited < 0) return true;
        int n = 0;
        foreach (byte t in arr) if (Limited[t]) n++;
        return n <= MaxLimited;
    }

    public int[] ToCardIds(byte[] arr)
    {
        var r = new int[arr.Length];
        for (int i = 0; i < arr.Length; i++) r[i] = TypeIds[arr[i]];
        return r;
    }

    /// <summary>摆法的唯一键（每格 6 位，最多 10 格）。</summary>
    public static ulong Key(byte[] arr)
    {
        ulong k = 0;
        for (int i = 0; i < arr.Length; i++) k = (k << 6) | arr[i];
        return k;
    }

    /// <summary>不同摆法的总数（多重集排列，封顶 1e15）。</summary>
    public double CountDistinct()
    {
        // f[r, l] = 用已处理的种类填 r 格、其中受限牌 l 张的「Σ Π 1/k!」；总数 = Slots! × Σ_l f[Slots, l]
        int lmax = MaxLimited < 0 ? Slots : Math.Min(MaxLimited, Slots);
        var f = new double[Slots + 1, lmax + 1];
        f[0, 0] = 1;
        for (int t = 0; t < TypeIds.Length; t++)
        {
            int cap = Math.Min(Counts[t], Slots);
            var g = new double[Slots + 1, lmax + 1];
            for (int r = 0; r <= Slots; r++)
                for (int l = 0; l <= lmax; l++)
                {
                    if (f[r, l] == 0) continue;
                    double inv = 1;
                    for (int k = 0; k <= cap && r + k <= Slots; k++)
                    {
                        if (k > 0) inv /= k;
                        int nl = Limited[t] ? l + k : l;
                        if (nl > lmax) break;
                        g[r + k, nl] += f[r, l] * inv;
                    }
                }
            f = g;
        }
        double fact = 1;
        for (int i = 2; i <= Slots; i++) fact *= i;
        double sum = 0;
        for (int l = 0; l <= lmax; l++) sum += f[Slots, l];
        return Math.Min(fact * sum, 1e15);
    }

    /// <summary>枚举全部不同摆法（调用方先用 <see cref="CountDistinct"/> 确认规模）。</summary>
    public List<byte[]> EnumerateAll()
    {
        var result = new List<byte[]>();
        var left = (int[])Counts.Clone();
        var cur = new byte[Slots];
        int limitedUsed = 0;
        void Rec(int pos)
        {
            if (pos == Slots) { result.Add((byte[])cur.Clone()); return; }
            for (int t = 0; t < TypeIds.Length; t++)
            {
                if (left[t] <= 0) continue;
                if (Limited[t] && MaxLimited >= 0 && limitedUsed >= MaxLimited) continue;
                left[t]--;
                if (Limited[t]) limitedUsed++;
                cur[pos] = (byte)t;
                Rec(pos + 1);
                if (Limited[t]) limitedUsed--;
                left[t]++;
            }
        }
        Rec(0);
        return result;
    }

    /// <summary>随机合法摆法（满足消耗 / 持续上限）。</summary>
    public byte[] Random(Random rng)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            var a = RandomUnchecked(rng);
            if (Valid(a)) return a;
        }
        return RandomFallback();
    }

    /// <summary>兜底：先放非受限牌、不够再放受限牌（上限内），保证合法。</summary>
    private byte[] RandomFallback()
    {
        var arr = new byte[Slots];
        var left = (int[])Counts.Clone();
        int pos = 0, lim = 0;
        for (int pass = 0; pass < 2 && pos < Slots; pass++)
            for (int t = 0; t < TypeIds.Length && pos < Slots; t++)
            {
                if ((pass == 0) == Limited[t]) continue;
                while (left[t] > 0 && pos < Slots && (!Limited[t] || MaxLimited < 0 || lim < MaxLimited))
                {
                    arr[pos++] = (byte)t;
                    left[t]--;
                    if (Limited[t]) lim++;
                }
            }
        if (pos < Slots) throw new InvalidOperationException("牌池放不满格数（受消耗 / 持续上限所限）");
        return arr;
    }

    private byte[] RandomUnchecked(Random rng)
    {
        var bag = new List<byte>();
        for (int t = 0; t < TypeIds.Length; t++)
        {
            int n = Math.Min(Counts[t], Slots);
            for (int k = 0; k < n; k++) bag.Add((byte)t);
        }
        var arr = new byte[Slots];
        for (int i = 0; i < Slots; i++)
        {
            int j = rng.Next(bag.Count);
            arr[i] = bag[j];
            bag.RemoveAt(j);
        }
        return arr;
    }

    /// <summary>把卡 id 序列转成摆法；牌不在池里 / 超出张数 / 长度不对时返回 null。长度不足时用补位牌补齐。</summary>
    public byte[]? FromCardIds(IReadOnlyList<int> ids)
    {
        if (ids.Count > Slots) return null;
        var left = (int[])Counts.Clone();
        var arr = new byte[Slots];
        for (int i = 0; i < Slots; i++)
        {
            int t;
            if (i < ids.Count) t = Array.IndexOf(TypeIds, ids[i]);
            else t = PadType;
            if (t < 0 || left[t] <= 0) return null;
            left[t]--;
            arr[i] = (byte)t;
        }
        return Valid(arr) ? arr : null;
    }

    /// <summary>
    /// 邻域：交换两格（种类不同）+ 插入（抽一张插到别处）+ 把某格换成池里还有富余的另一种牌。
    /// </summary>
    public List<byte[]> Neighbors(byte[] arr)
    {
        var res = new List<byte[]>();
        for (int i = 0; i < Slots; i++)
            for (int j = i + 1; j < Slots; j++)
            {
                if (arr[i] == arr[j]) continue;
                var n = (byte[])arr.Clone();
                (n[i], n[j]) = (n[j], n[i]);
                res.Add(n);
            }
        // 插入：把第 i 格的牌抽出来插到第 j 格，中间的整体挪一格（相邻两格的插入等同交换，跳过）。
        // 排序类问题里「整段挪位」单靠交换要好几步才走得到，局部搜索容易卡住（实测 15 张牌池个别种子卡在次优）。
        for (int i = 0; i < Slots; i++)
            for (int j = 0; j < Slots; j++)
            {
                if (Math.Abs(i - j) <= 1) continue;
                var n = new byte[Slots];
                int w = 0;
                for (int r = 0; r < Slots; r++)
                {
                    if (r == i) continue;
                    if (w == j) n[w++] = arr[i];
                    n[w++] = arr[r];
                }
                if (w == j) n[w] = arr[i];
                bool same = true;
                for (int r = 0; r < Slots; r++) if (n[r] != arr[r]) { same = false; break; }
                if (!same) res.Add(n);   // 插入不改变牌的集合，受限张数不变
            }
        var used = new int[TypeIds.Length];
        foreach (byte t in arr) used[t]++;
        for (int t = 0; t < TypeIds.Length; t++)
        {
            if (used[t] >= Counts[t]) continue;
            for (int i = 0; i < Slots; i++)
            {
                if (arr[i] == t) continue;
                var n = (byte[])arr.Clone();
                n[i] = (byte)t;
                if (Valid(n)) res.Add(n);
            }
        }
        return res;
    }

    /// <summary>随机扰动 k 步（交换或替换），用于跳出局部最优。</summary>
    public byte[] Perturb(byte[] arr, int k, Random rng)
    {
        var cur = arr;
        for (int s = 0; s < k; s++)
        {
            var nb = Neighbors(cur);
            if (nb.Count == 0) break;
            cur = nb[rng.Next(nb.Count)];
        }
        return cur;
    }
}
