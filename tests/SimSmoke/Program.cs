using Yx.BattleSim.Model;
using Yx.BattleSim.Oracle;
using Yx.BattleSim.Resolve;

static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
var side=new OracleSide {Hp=50,Life=90,UnlockGrids=4,
    PermanentBuffs=new(){[(int)BuffType.TiPo]=10,[(int)BuffType.TiPoShangXian]=20}};
var input=side.ToSide("L");
Check(input.UnlockGrids==4,"Early-round slot count lost during conversion");
var combatant=BattleResolver.ToCombatant(input);
Check(combatant.Board.Count==4,"Early-round board must cycle across four slots");
Check(combatant.Hp==60 && combatant.MaxHp==60,"Carried physique must restore start HP and max HP");
Check(combatant.TiPo==10,"Carried physique lost");
Check(combatant.GetBuffValue(BuffType.TiPoShangXian)==20,"Carried physique cap lost");
Console.WriteLine("PASS: four-slot board and carried physique initialization");
