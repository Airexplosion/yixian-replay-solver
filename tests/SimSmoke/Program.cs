using Yx.BattleSim.Model;
using Yx.BattleSim.Oracle;
using Yx.BattleSim.Resolve;
using Yx.BattleSim.Solve;
using Yx.BattleSim.Config;

static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
var side=new OracleSide {Hp=50,Life=90,
    PermanentBuffs=new(){[(int)BuffType.TiPo]=10,[(int)BuffType.TiPoShangXian]=20}};
var input=side.ToSide("L");
string temporary=Path.GetTempFileName();
try
{
    File.WriteAllText(temporary,"{\"card\":{}}");
    var config=JsonBattleConfig.Load(temporary);
    var early=new PuzzleSide().ToSideWith("L",new[]{0,0,0,0},config);
    Check(early.UnlockGrids==4,"Early-round slot count lost during conversion");
    Check(BattleResolver.ToCombatant(early).Board.Count==4,"Early-round board must cycle across four slots");
}
finally { File.Delete(temporary); }
var combatant=BattleResolver.ToCombatant(input);
Check(combatant.Hp==50 && combatant.MaxHp==60,"Carried physique increases the cap without granting start HP");
Check(combatant.TiPo==10,"Carried physique lost");
Check(combatant.GetBuffValue(BuffType.TiPoShangXian)==20,"Carried physique cap lost");
Console.WriteLine("PASS: four-slot board and carried physique initialization");
