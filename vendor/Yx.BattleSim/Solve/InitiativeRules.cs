using Yx.BattleSim.Config;

namespace Yx.BattleSim.Solve;

/// <summary>Determine attainable initiative from pre-battle cultivation and speed bonuses.</summary>
public static class InitiativeRules
{
    private static readonly bool[] MeFirst = [true];
    private static readonly bool[] FoeFirst = [false];
    private static readonly bool[] Either = [true, false];
    private static readonly string[] Elements = ["金灵", "木灵", "水灵", "火灵", "土灵"];

    public static bool[] Allowed(PuzzleSide me, IReadOnlyList<int> board, PuzzleSide foe, IBattleConfig cfg)
    {
        long mine = Speed(me, board, cfg), theirs = Speed(foe, foe.Cards, cfg);
        return mine > theirs ? MeFirst : mine < theirs ? FoeFirst : Either;
    }

    // Current BattleCharacterUI.CalExtraSpeed: four permanent bonuses, talent 128 and keyin 42.
    // Talent 128 depends on the proposed board, not the whole hand or the original arrangement.
    public static long Speed(PuzzleSide side, IReadOnlyList<int> board, IBattleConfig cfg)
    {
        long speed = side.Exp;
        speed += side.PermanentBuffs.GetValueOrDefault(10012);
        speed += side.PermanentBuffs.GetValueOrDefault(10045);
        speed += side.PermanentBuffs.GetValueOrDefault(10056);
        speed += side.PermanentBuffs.GetValueOrDefault(369);
        foreach (int talent in side.Talents)
            if (talent % 10000 == 128 && (side.Talents.Contains(278) || SameElement(board, cfg)))
            {
                var values = cfg.TalentOtherParams(talent);
                if (values.Count == 0) throw new InvalidOperationException("Missing initiative talent config: " + talent);
                speed += values[0];
            }
        foreach (int keyin in side.Keyin)
            if (keyin % 10000 == 42)
            {
                var values = cfg.KeYinOtherParams(keyin);
                if (values.Count == 0) throw new InvalidOperationException("Missing initiative keyin config: " + keyin);
                speed += values[0];
            }
        return speed;
    }

    private static bool SameElement(IReadOnlyList<int> board, IBattleConfig cfg)
    {
        int element = -1;
        foreach (int id in board)
        {
            string name = cfg.CardName(id);
            for (int i = 0; i < Elements.Length; i++)
                if (name.Contains(Elements[i], StringComparison.Ordinal))
                {
                    if (element < 0) { element = i; break; }
                    if (element != i) return false;
                }
        }
        return element >= 0;
    }
}
