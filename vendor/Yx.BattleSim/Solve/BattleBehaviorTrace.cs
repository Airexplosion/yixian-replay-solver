using System.Text;
using Yx.BattleSim.Model;

namespace Yx.BattleSim.Solve;

/// <summary>
/// 当前求解场景下的观测等价：保留尝试使用的原牌/格位、临时牌、逐步血防变化、
/// 开局/效果/回合边界的灵气与 buff。未被使用的原始牌位不写入，但其产生的效果照常记录。
/// 不承诺未抽到的随机分支也等价。只为进榜候选创建，完整签名只留前 N 名。
/// </summary>
internal sealed class BattleBehaviorTrace(int[] board)
{
    private readonly StringBuilder _text = new();

    public void Begin(int foe, bool first) => _text.Append("scenario:").Append(foe).Append(':').Append(first).AppendLine();
    public void Sample(int sample) => _text.Append("sample:").Append(sample).AppendLine();
    public void RandomDraws(int count) => _text.Append("draws:").Append(count).AppendLine();
    public void Result(OrderResult result) => _text.Append("result:").Append(result).AppendLine();

    public void Observe(string phase, int value, Combatant actor)
    {
        if (actor.Opponent is null) return;
        _text.Append(phase).Append(':').Append(value).Append(':').Append(actor.Uid).Append(':');
        if (phase is "attempt" or "card" or "temp" or "effect-end")
        {
            int grid = actor.CurrentCardGrid;
            _text.Append(grid).Append(':');
            // 变牌前的原牌也有意义；不能因最终都变成同一张牌就合并。
            if (actor.Uid == "L" && grid >= 0 && grid < board.Length) _text.Append(board[grid]);
        }
        var left = actor.Uid == "L" ? actor : actor.Opponent;
        Side(left);
        Side(left.Opponent);
        _text.AppendLine();
    }

    private void Side(Combatant c)
    {
        _text.Append('|').Append(c.Hp).Append(',').Append(c.MaxHp).Append(',').Append(c.Def)
            .Append(',').Append(c.Anima).Append(',').Append(c.TiPo).Append(',').Append(c.Life)
            .Append(',').Append(c.TempLife).Append(',').Append(c.IsDead)
            .Append(',').Append(c.CurrentCardActionAgain).Append(',').Append(c.CurrentCardSkip)
            .Append(':').Append(c.DumpBuffs());
    }

    public override string ToString() => _text.ToString();
}
