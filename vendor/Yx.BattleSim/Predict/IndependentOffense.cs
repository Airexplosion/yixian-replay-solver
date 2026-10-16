using Yx.BattleSim.Effects;
using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;
using Yx.BattleSim.Model;
using Yx.BattleSim.Resolve;

namespace Yx.BattleSim.Predict;

public static class IndependentOffense
{
    // A neutral, non-attacking target. Its full health equals our initial max health.
    // Keep it alive after each hit so an early kill cannot truncate the ten-round comparison.
    public static int[] Calculate(SideInput mine, int battleRound, bool meFirst, IBattleConfig? config = null)
    {
        var self = BattleResolver.ToCombatant(mine);
        var target = BattleResolver.ToCombatant(new SideInput {
            Uid = "R", StartHp = Math.Max(1, self.MaxHp), Life = 100, Board = [], UnlockGrids = 1
        });
        self.Opponent = target; target.Opponent = self;
        if (config != null) self.Config = target.Config = config;
        self.CurrentRound = target.CurrentRound = battleRound;
        int[] result = Enumerable.Repeat(-1, 10).ToArray();
        int damage = 0;
        bool active = false;
        var previous = Combatant.HpDeltaSink;
        var previousSteps = StepLog.PredictionSink;
        StepLog.PredictionSink = null;
        Combatant.HpDeltaSink = (c, delta, cost) => {
            if (!ReferenceEquals(c, target)) return;
            if (active && delta < 0 && !cost) damage -= delta;
            c.Hp = Math.Max(1, c.MaxHp);
        };
        void IdleTurn()
        {
            // Empty cards would make a normal attack: only tick buffs, never play the target's cards.
            TurnTickFunctions.OnTurnStarted(target);
            TurnTickFunctions.OnTurnEnded(target);
        }
        try
        {
            SharedBattleStart.Prepare(meFirst ? self : target, meFirst ? target : self);
            if (meFirst) { BattleStartFunctions.OnBattleStarted(self); BattleStartFunctions.OnBattleStarted(target); }
            else { BattleStartFunctions.OnBattleStarted(target); BattleStartFunctions.OnBattleStarted(self); }
            for (int round = 0; round < result.Length; round++)
            {
                if (DeathRules.DeathCheck(self, target)) break;
                damage = 0; active = true;
                if (!meFirst) IdleTurn();
                BattleResolver.PlayTurn(self, target);
                if (meFirst && !DeathRules.DeathCheck(self, target)) IdleTurn();
                result[round] = damage;
                active = false;
            }
            return result;
        }
        finally { Combatant.HpDeltaSink = previous; StepLog.PredictionSink = previousSteps; }
    }
}
