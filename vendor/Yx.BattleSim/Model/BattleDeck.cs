namespace Yx.BattleSim.Model;

/// <summary>
/// 牌组队列 + <c>skip</c> 语义，忠实移植 <c>BattleCharacter.m_TempBattleCardItems</c> /
/// <c>ShiftCard</c> / <c>PushCard</c> / <c>UnshiftCard</c>（反编译 9688–9721）。
///
/// **为什么必须建模**：原码出一张牌收尾时（15590–15630）
/// <code>
/// if (cardConfig.cardType == CardType.Sustain || cardConfig.cardType == CardType.Consume || cardItem.skip)
///     cardItem.skip = true;
/// </code>
/// 而 <see cref="ShiftCard"/> 会把 skip 掉的格子跳过去 —— 于是**持续 / 消耗牌整场只出一次**，
/// 之后每回合都被跳过；直到八格全部出局，才把当前格复位成普攻。<br/>
/// 早先 sim 按「游标 % 8 循环」发牌，等于让 442 张持续牌 + 133 张消耗牌无限重出，
/// B5 单卡隔离里成片的误差正是从这里来的 —— 卡 28 无极卦盘（`[持续]`，a=0）：
/// real `R=7`（1 次卡 + 31 次普攻 = 93）/ sim `R=16`（4 次卡 + 28 次普攻 = 84）。
///
/// 队列语义（顺序敏感，不能改成环形索引）：
///   出牌取**队首** <c>RemoveAt(0)</c>；出完 <c>PushCard</c> 塞**队尾**；
///   灵气不足时原码走 <c>UnshiftCard</c> 塞回**队首**（下次还是这张）。
/// </summary>
public sealed class BattleDeck
{
    /// <summary>战斗牌组格数（对应游戏 8 个出牌格）。</summary>
    public const int Grids = 8;

    /// <summary>
    /// 牌组里的**一个格子**。对应游戏里常驻的那张 <c>CardItem</c>：
    /// <c>skip</c> 是格子身上的状态（不是牌型的），所以同一张牌在不同格互不影响。
    /// </summary>
    public sealed class Item
    {
        /// <summary>板上格位（0..7）；-1 只用于「空队列兜底」。复位成普攻时**格位保留**（见 <see cref="OnResetToPuGong"/>）。</summary>
        public int Slot;

        /// <summary>已出局：持续 / 消耗牌出过一次之后为 true，轮转时被跳过。普攻永不为 true。</summary>
        public bool Skip;

        /// <summary>诊断用：这张格子的牌是否已被复位成普攻。</summary>
        public bool IsPuGong => Slot < 0;
    }

    private readonly List<Item> _items = new();

    /// <summary>
    /// 八格全出局、某格被 <c>InitData(0)</c> 复位成普攻时回调（参数 = 该格格位）。
    /// 原码改的是**常驻 CardItem**：那一格从此就是普攻（id 0），格位不变 —— 所以「连音」这类按
    /// 「上一格」换牌的效果仍然有效。早先 sim 把格位置成 -1，连音找不到上一格，普攻原样打出
    /// （206 逍遥连音曲 b5_206_0：real R 一滴血不掉，sim R=67）。
    /// </summary>
    public System.Action<int>? OnResetToPuGong;
    private readonly int _slots;

    /// <summary>建一副满格牌组。格位 0..slots-1 —— 超出实际摆牌数的格位由调用方当作普攻。</summary>
    public BattleDeck(int slots = Grids)
    {
        _slots = slots > 0 ? slots : Grids;
        for (int i = 0; i < _slots; i++) _items.Add(new Item { Slot = i });
    }

    /// <summary>队列里的格子数（稳态恒等于 <see cref="Grids"/>）。</summary>
    public int Count => _items.Count;

    /// <summary>
    /// 取本回合要出的那张牌：跳过已出局的格子；**八格全部出局时**把最后取到的那格复位成普攻并解封
    /// （对应原码的 <c>if (num == 8) { m_CurrentUsingCard.InitData(0); skip = false; }</c>）。
    /// 返回的格子已经**离开队列**，出完牌后调用方要 <see cref="PushCard"/> 塞回队尾。
    /// </summary>
    public Item ShiftCard()
    {
        if (_items.Count == 0) _items.Add(new Item { Slot = -1 });   // 兜底：空队列当普攻

        Item current = _items[0];
        _items.RemoveAt(0);

        int scanned = 0;
        while (current.Skip && scanned < _slots)
        {
            scanned++;
            _items.Add(current);                    // PushCard：出局的格子回到队尾继续等
            current = _items[0];
            _items.RemoveAt(0);
        }

        if (scanned == _slots)
        {
            // 八格全出局 → 这一格被 InitData(0) 复位成普攻，并解除 skip（否则永远轮不到任何人出牌）。
            current.Skip = false;
            if (current.Slot >= 0) OnResetToPuGong?.Invoke(current.Slot);
        }

        return current;
    }

    /// <summary>出完牌塞回**队尾**（对应 <c>PushCard</c>）。</summary>
    /// <summary>
    /// 队首**不移除**地看一眼。对应 <c>BattleCharacter.GetCurrentCardInDeck()</c>（9708，= <c>m_TempBattleCardItems[0]</c>）。
    /// 「复制对方下1张牌的效果」（卡 177）要的就是它。
    /// </summary>
    public Item? PeekCard() => _items.Count > 0 ? _items[0] : null;

    public void PushCard(Item item) => _items.Add(item);

    /// <summary>ReverseCardItems（原码 9723）：把**队列里剩下的**格子整体倒序（正在出的那张不在队列里）。反转出牌方向的三张牌用。</summary>
    public void Reverse() => _items.Reverse();

    /// <summary>该格是否已出局（原码 <c>GetBattleDeckCardItemList()[grid].skip</c>）。格子正在出牌、不在队列里时按未出局算。</summary>
    public bool IsSkipped(int slot)
    {
        foreach (var it in _items) if (it.Slot == slot) return it.Skip;
        return false;
    }

    /// <summary>
    /// RightMoveCardItems（原码）：把队尾那张搬到队首，重复 <paramref name="r"/> 次 ——「跳至上一张牌」用。
    /// careSkip 时搬到的是已出局（skip）的牌不计次数（照原码 `i--`）。
    /// ⚠ 原码的计数器 `num` 在循环体里每次都重置为 0，所以它的「全是出局牌就停」只在队列 ≤2 张时生效；
    ///   队列里全是出局牌时原码会死循环 —— 这里多加一道总步数上限兜底（不影响正常盘面）。
    /// </summary>
    public void RightMoveCardItems(int r, bool careSkip)
    {
        int count = _items.Count;
        if (count == 0) return;
        int guard = 0;
        for (int i = 1; i <= r; i++)
        {
            if (++guard > count * (r + 1) + 8) break;
            Item last = _items[count - 1];
            if (careSkip)
            {
                int num = 0;
                if (last.Skip) { i--; num++; }
                if (num >= count - 1) break;
            }
            _items.RemoveAt(count - 1);
            _items.Insert(0, last);
        }
    }

    /// <summary>塞回**队首**（对应 <c>UnshiftCard</c>）——灵气不足没出成时用，下次还是这张。</summary>
    public void UnshiftCard(Item item) => _items.Insert(0, item);

    /// <summary>
    /// 出牌成功后的收尾登记（对应原码 15608–15626）：
    /// 持续 / 消耗牌用一次就标记出局，之后轮转直接跳过。**必须在 PushCard 之前调**。
    /// </summary>
    public static void MarkUsed(Item item, bool isSustainOrConsume)
    {
        if (isSustainOrConsume || item.Skip) item.Skip = true;
    }
}
