using Yx.BattleSim.Combat;
using Yx.BattleSim.Config;
using Yx.BattleSim.Effects;

namespace Yx.BattleSim.Model;

/// <summary>
/// buff 的**写入口**——忠实移植 <c>BattleCharacter.ModifyBuffValue(BuffType, int)</c>（原码 9864–10518，805 行）。
///
/// 这是全战斗最密集的联动点：**改一个 buff 的实际增量，会和身上其它 buff / 天赋 / 共鸣 / 刻印 / 仙命 /
/// 牌库互相作用**。结构是三段，顺序极敏感（不可重排、不可归并）：
///
///   ① **写入前的 delta 修正链**（9888–10026）：加量、转换、抵消、改道。多处直接 <c>return</c> 中止
///      （辟邪吃满、仙果宝宗转回血、星力→内伤），因此「写了没有」本身取决于前面的分支。
///   ② **写入**（10027–10044）：首次写入 / 累加；**≤0 时移除，并把多出的部分退回 delta**
///      （`delta -= buffs[num]`）——后面所有联动看的是这个**已修正的 delta**，不是入参。
///   ③ **写入后的联动链**（10046–10517）：气势不绝/龙马精神回填（**会递归调回本函数**）、
///      负面状态的各种反制、五行聚灵的十余条后续（天赋79/10079/20079/30079/112/138/200/202、
///      共鸣34/47、刻印31、混天克、命411…），最后是**气势上限 clamp**。
///
/// 为什么值得单独一个文件：它是前三批（气势写入侧 / 身法获得联动 / 画龙点睛前置）共同的阻塞点。
///
/// 表现层整体剥离（Eff/浮字/音效/skinNumber/animator），不改数值。
/// ⚠ 少数分支依赖本 sim 没有的数据，就地注明并走默认支路（见各处 ⬜）。
/// </summary>
public sealed partial class Combatant
{
    /// <summary>宗字 buff（<c>CardActionBase.GetZongZiBuff()</c>）：无迷踪按这些 buff 的获得量回血。</summary>
    private static readonly int[] ZongZiBuffs = [100, 248, 392, 393];

    /// <summary>
    /// ModifyBuffValue：改一个 buff 的值，并跑完整条联动链。忠实照抄原码分支顺序。
    /// </summary>
    public void ModifyBuffValue(BuffType buffType, int delta)
    {
        if (delta == 0) return;

        bool negative = Config.BuffCategoryOf(buffType) == BuffCategory.Negative;

        // ══ ① 写入前的 delta 修正链 ══

        // 悲星石：吃掉一层，本次负面状态增量按其层数加倍。
        if (delta > 0 && negative && HasBuff(BuffType.BeiXingShi) && buffType != BuffType.Min)
        {
            delta += GetBuffValue(BuffType.BeiXingShi);
            RemoveBuff(BuffType.BeiXingShi);
        }

        // 天赋256：把一部分负面状态转成「镜中悬影」并镜像给对手。
        if (delta > 0 && HasTalent(256) && negative && !HasBuff(BuffType.JingZhongXuanYing) && buffType != BuffType.Min)
        {
            int d = Math.Min(delta, Config.TalentOtherParams(256).At(0));
            ModifyBuffValue(BuffType.JingZhongXuanYing, d);
            Opponent.ModifyBuffValue(buffType, d);
        }

        // 对手的共鸣25 / 天赋251 / 自身的登焰淬心 / 落花有意+ / 蛇影：都会把「我方施加的内伤」加重。
        if (delta > 0 && Opponent.IsTalentResonanceEffective(25) && buffType == BuffType.NeiShang) delta++;
        if (delta > 0 && Opponent.HasTalent(251) && buffType == BuffType.NeiShang) delta++;
        if (delta > 0 && GetBuffValue(BuffType.DengYanCuiXin) > 0 && buffType == BuffType.NeiShang)
        {
            ModifyBuffValue(BuffType.DengYanCuiXin, -1);
            delta++;
        }
        if (delta > 0 && negative && HasBuff(BuffType.LuoHuaYouYiPlus) && buffType == BuffType.NeiShang) delta += 2;
        if (delta > 0 && negative && HasBuff(BuffType.SheYing)
            && (buffType == BuffType.NeiShang || buffType == BuffType.WaiShang))
            delta += GetBuffValue(BuffType.SheYing);

        // 共鸣67 / 73 / 52 / 55：一次性加量（各消耗自己的临时标志）。
        if (delta > 0 && IsTalentResonanceEffective(67) && buffType == BuffType.GuaXiang && !CheckTalentResonanceTempFlag(67))
        {
            delta++;
            SetTalentResonanceTempFlag(67, true);
        }
        if (delta > 0 && IsTalentResonanceEffective(73) && buffType == BuffType.JiaGong && !CheckTalentResonanceTempFlag(73))
        {
            delta++;
            SetTalentResonanceTempFlag(73, true);
        }
        if (delta > 0 && HasTalentResonance(52) && buffType == BuffType.QiShi && !CheckTalentResonanceTempFlag(52))
        {
            // 注意：52 与 55 的生效/未生效取的是**相反**的 otherParams 下标，不是笔误。
            delta += IsTalentResonanceEffective(52)
                ? Config.ResonanceOtherParams(52).At(1)
                : Config.ResonanceOtherParams(52).At(0);
            SetTalentResonanceTempFlag(52, true);
        }
        if (delta > 0 && HasTalentResonance(55) && buffType == BuffType.Min && !CheckTalentResonanceTempFlag(55))
        {
            delta += IsTalentResonanceEffective(55)
                ? Config.ResonanceOtherParams(55).At(1)
                : Config.ResonanceOtherParams(55).At(0);
            SetTalentResonanceTempFlag(55, true);
        }

        // 「下次加X多加」这一类一次性加成：吃满后自身清掉。
        if (delta > 0 && buffType == BuffType.JianYi && HasBuff(BuffType.XiaCiJiaJianYiDuoJia))
        {
            delta += GetBuffValue(BuffType.XiaCiJiaJianYiDuoJia);
            RemoveBuff(BuffType.XiaCiJiaJianYiDuoJia);
        }
        if (delta > 0 && buffType == BuffType.JiaGong && HasBuff(BuffType.XiaCiJiaGongDuoJia))
        {
            delta += GetBuffValue(BuffType.XiaCiJiaGongDuoJia);
            RemoveBuff(BuffType.XiaCiJiaGongDuoJia);
        }
        if (delta > 0 && buffType == BuffType.QiShi && HasBuff(BuffType.XiaCiQiShiDuoJia))
        {
            delta += GetBuffValue(BuffType.XiaCiQiShiDuoJia);
            RemoveBuff(BuffType.XiaCiQiShiDuoJia);
        }
        if (delta > 0 && buffType == BuffType.GuaXiang && HasBuff(BuffType.XiaCiJiaGuaXiangDuoJia))
        {
            delta += GetBuffValue(BuffType.XiaCiJiaGuaXiangDuoJia);
            RemoveBuff(BuffType.XiaCiJiaGuaXiangDuoJia);
        }

        // 仙命148：加攻 -1，转成 1 层木刺。
        if (delta > 0 && buffType == BuffType.JiaGong && HasFateStrategy(148))
        {
            delta--;
            ModifyBuffValue(BuffType.MuCi, 1);
        }
        // 加攻转木刺：最多转走自身层数。
        if (delta > 0 && buffType == BuffType.JiaGong && HasBuff(BuffType.JiaGongZhuanMuCi))
        {
            int n = Math.Min(delta, GetBuffValue(BuffType.JiaGongZhuanMuCi));
            if (n > 0)
            {
                delta -= n;
                ModifyBuffValue(BuffType.MuCi, n);
            }
        }
        // 悟境卦眼：加卦象时 +1，自身 -1。
        if (delta > 0 && buffType == BuffType.GuaXiang && HasBuff(BuffType.WuJingGuaYan))
        {
            delta++;
            ModifyBuffValue(BuffType.WuJingGuaYan, -1);
        }

        // 天赋206：拳家式少吃外伤 / 棍家式少吃剑伤。
        if (delta > 0 && HasTalent(206) && HasBuff(BuffType.QuanJiaShi) && buffType == BuffType.WaiShang) delta--;
        if (delta > 0 && HasTalent(206) && HasBuff(BuffType.GunJiaShi) && buffType == BuffType.JianGong) delta--;

        // 仙果宝宗：吃掉一层，把这次负面状态整份转成回血，**并中止**（不再施加该负面）。
        if (delta > 0 && negative && HasBuff(BuffType.XianGuoBaoZong) && buffType != BuffType.Min)
        {
            ModifyBuffValue(BuffType.XianGuoBaoZong, -1);
            ModifyBuffValue(BuffType.HuiFu, delta);
            return;
        }

        // 困五金环：锋锐增量按层数加倍。
        if (delta > 0 && buffType == BuffType.FengRui && HasBuff(BuffType.KunWuJinHuan))
            delta += GetBuffValue(BuffType.KunWuJinHuan);
        // 火灵印+：锋锐转成对对手的 2 倍削上限，自身锋锐归零（整份改道）。
        if (delta > 0 && buffType == BuffType.FengRui && HasBuff(BuffType.HuoLingYinPlus))
        {
            Opponent.ModifyHp(-delta * 2);
            Opponent.ModifyMaxHp(-delta * 2);
            delta = 0;
        }
        // 辟邪：吃掉一部分负面状态（自身抵消），若吃满就中止。
        if (delta > 0 && negative && HasBuff(BuffType.BiXie))
        {
            int n = Math.Min(delta, GetBuffValue(BuffType.BiXie));
            delta -= n;
            ModifyBuffValue(BuffType.BiXie, -n);
            if (delta == 0) return;
        }
        // 星力→内伤改道（刻印29 / 仙命399 开关激活）：中止，星力不落自身。
        if (delta > 0 && buffType == BuffType.XingLi && Subs.HasKeYinType(this, 29))
        {
            Opponent.ModifyBuffValue(BuffType.NeiShang, delta);
            return;
        }
        if (delta > 0 && buffType == BuffType.XingLi && HasFateStrategy(399)
            && FateStrategyFunctions.IsSwitchActive(this, 399))
        {
            Opponent.ModifyBuffValue(BuffType.NeiShang, delta);
            return;
        }

        // ══ ② 写入 ══

        if (!_buffsPresent(buffType))
        {
            if (delta < 0) return;              // 本来就没有：减不成立，整条链到此为止
            SetBuffValue(buffType, delta);
        }
        else
        {
            SetBuffValue(buffType, GetBuffValue(buffType) + delta);
        }
        if (GetBuffValue(buffType) <= 0)
        {
            // 归零/转负：移除，并把「多减掉的」退回 delta（后续联动看的是这个值）
            delta -= GetBuffValue(buffType);
            RemoveBuff(buffType);
        }

        // ══ ③ 写入后的联动链 ══

        // 气势不绝：气势被清空时从它回填（**递归**回本函数）。
        if (HasBuff(BuffType.QiShiBuJue) && buffType == BuffType.QiShi && GetBuffValue(BuffType.QiShi) == 0)
            ModifyBuffValue(BuffType.QiShi, GetBuffValue(BuffType.QiShiBuJue));
        // 龙马精神：卦象被清空时回填。
        if (HasBuff(BuffType.LongMaJingShen) && buffType == BuffType.GuaXiang && GetBuffValue(BuffType.GuaXiang) == 0)
            ModifyBuffValue(BuffType.GuaXiang, GetBuffValue(BuffType.LongMaJingShen));

        // 天赋177 / 刻印159：获得负面状态时回血。
        if (HasTalent(177) && delta > 0 && negative)
            ModifyHp(Math.Abs(delta) * Config.TalentOtherParams(177).At(0));
        if (Subs.HasKeYinType(this, 159) && delta > 0 && negative)
            ModifyHp(Math.Abs(delta) * Subs.KeYinOtherparam(this, 159, 0));

        // 阴符绝阵：把获得的负面状态弹回去。
        if (delta > 0 && negative && buffType != BuffType.Min && HasBuff(BuffType.YinFuJueZhen))
        {
            int damage = delta * GetBuffValue(BuffType.YinFuJueZhen);
            CombatMath.ApplyDamage(Opponent, this, DamageInfo.Create(this, DamageType.ReflectDamage, damage, skipWoundCheck: true));
        }
        // 「下次获得负面状态加防」：一次性转防。
        if (GetBuffValue(BuffType.XiaCiHuoDeFuMianZhuangTaiJiaFang) > 0 && delta > 0 && negative)
        {
            int v = GetBuffValue(BuffType.XiaCiHuoDeFuMianZhuangTaiJiaFang);
            RemoveBuff(BuffType.XiaCiHuoDeFuMianZhuangTaiJiaFang);
            ModifyDef(v);
        }

        // ── 身法（用户追问的那族）──
        // 牌库有 391：获得身法时等量加防。
        if (delta > 0 && buffType == BuffType.ShenFa && Subs.HasCardInDeck(this, 391)) ModifyDef(delta);
        // 平虚御风：获得身法时按层数弹伤。
        if (delta > 0 && buffType == BuffType.ShenFa && HasBuff(BuffType.PingXuYuFeng))
        {
            int damage = delta * GetBuffValue(BuffType.PingXuYuFeng);
            CombatMath.ApplyDamage(this, Opponent, DamageInfo.Create(this, DamageType.ReflectDamage, damage, skipWoundCheck: true));
        }
        // 天眼风铃断曲：获得身法时转 1 体魄。
        if (delta > 0 && buffType == BuffType.ShenFa && HasBuff(BuffType.TianYanFengLingDuanQu))
        {
            ModifyBuffValue(BuffType.TianYanFengLingDuanQu, -1);
            ModifyTiPo(1);
        }

        // ── 负面状态的各种反制 ──
        if (GetBuffValue(BuffType.XiaCiHuoDePoZhanShiShiJiaPoZhan) > 0 && delta > 0 && negative)
        {
            int v = GetBuffValue(BuffType.XiaCiHuoDePoZhanShiShiJiaPoZhan);
            RemoveBuff(BuffType.XiaCiHuoDePoZhanShiShiJiaPoZhan);
            Opponent.ModifyBuffValue(BuffType.PoZhan, v);
        }
        if (HasBuff(BuffType.XuanYuXueLian) && delta > 0 && negative)
        {
            ModifyBuffValue(BuffType.XuanYuXueLian, -1);
            Opponent.ModifyBuffValue(buffType, Math.Abs(delta));
        }
        // 梦灵玄：把负面状态转给对手（化身器时最多 2 层）。
        if (HasBuff(BuffType.MengLingXuan) && HasBuff(BuffType.MengLingXuanHuaShenQi)
            && GetBuffValue(BuffType.MengLingXuanBuChuFa) != 1
            && GetBuffValue(BuffType.MengLingXuanChuFaCiShu) < GetBuffValue(BuffType.MengLingXuan)
            && delta > 0 && negative)
        {
            ModifyBuffValue(BuffType.MengLingXuanChuFaCiShu, 1);
            Opponent.ModifyBuffValue(buffType, delta >= 2 ? 2 : 1);
        }
        else if (HasBuff(BuffType.MengLingXuan)
            && GetBuffValue(BuffType.MengLingXuanBuChuFa) != 1
            && GetBuffValue(BuffType.MengLingXuanChuFaCiShu) < GetBuffValue(BuffType.MengLingXuan)
            && delta > 0 && negative)
        {
            ModifyBuffValue(BuffType.MengLingXuanChuFaCiShu, 1);
            Opponent.ModifyBuffValue(buffType, 1);
        }
        // 金气宗 / 流水五琴 / 覆面加恢复：获得负面状态时的三种反制。
        if (HasBuff(BuffType.JinQiZong) && delta > 0 && negative)
            ModifyHp(Math.Abs(delta) * GetBuffValue(BuffType.JinQiZong));
        if (delta > 0 && negative && HasBuff(BuffType.LiuShuiWuQin)
            && buffType != BuffType.NeiShang && buffType != BuffType.Min)
            ModifyBuffValue(BuffType.NeiShang, delta * GetBuffValue(BuffType.LiuShuiWuQin));
        if (delta > 0 && negative && HasBuff(BuffType.FuMianJiaHuiFu) && buffType != BuffType.Min)
            Opponent.ModifyBuffValue(BuffType.HuiFu, delta * GetBuffValue(BuffType.FuMianJiaHuiFu));

        // 醉拳家式：破绽被削时转加攻。
        if (delta < 0 && buffType == BuffType.PoZhan && GetBuffValue(BuffType.ZuiQuanJiaShi) > 0)
        {
            int n = -delta * GetBuffValue(BuffType.ZuiQuanJiaShi);
            if (n > 0) ModifyBuffValue(BuffType.JiaGong, n);
        }
        // 牌库有 415：负面状态变动（含被削）都按绝对值加体魄。
        if (delta != 0 && negative && Subs.HasCardInDeck(this, 415))
            ModifyTiPo(Math.Abs(delta));

        // 脉(Min)：原码按角色 4000003 区分正负（该角色回血，其余扣血）；角色 id 来自 SideInput.CharacterId。
        if (buffType == BuffType.Min)
        {
            int n = Math.Abs(delta) * 3;
            ModifyHp(CharacterId == 4000003 ? n : -n);
        }

        // 无迷踪：宗字 buff 的获得量按层数回血。
        if (HasBuff(BuffType.WuMiZong) && delta > 0 && Array.IndexOf(ZongZiBuffs, (int)buffType) >= 0)
            ModifyHp(GetBuffValue(BuffType.WuMiZong) * delta);

        // 天赋36 / 61：卦象的后续（加灵气 / 补星力）。
        if (delta > 0 && buffType == BuffType.GuaXiang && HasTalent(36))
            ModifyAnima(Config.TalentOtherParams(36).At(0));
        if (delta > 0 && buffType == BuffType.GuaXiang && HasTalent(61))
        {
            if (!HasBuff(BuffType.XingLi))
                ModifyBuffValue(BuffType.XingLi, Config.TalentOtherParams(61).At(0));
            else if (IsTalentResonanceEffective(119)
                     && GetBuffValue(BuffType.XingLi) < Config.ResonanceOtherParams(119).At(0))
                ModifyBuffValue(BuffType.XingLi, Config.TalentOtherParams(61).At(0));
        }

        // 卦象的一大串后续：记录、六爻杀阵、梦凉衣甲、天赋135、星月乾坤扇。
        if (delta > 0 && buffType == BuffType.GuaXiang)
        {
            ModifyBuffValue(BuffType.JiLuJiaGuoGuaXiang, delta);
            if (HasBuff(BuffType.LiuYaoShaZhen))
            {
                int n = delta * GetBuffValue(BuffType.LiuYaoShaZhen);
                if (Subs.HasKeYinType(this, 74))
                {
                    n += GetBuffValue(BuffType.XingLi);
                    n += GetBuffValue(BuffType.JiaGong);
                }
                CombatMath.ApplyDamage(this, Opponent, DamageInfo.Create(this, DamageType.Damage, n, skipWoundCheck: true));
            }
            if (HasBuff(BuffType.MengLiangYiJiaShengMing))
                ModifyHp(delta * GetBuffValue(BuffType.MengLiangYiJiaShengMing));
            if (HasBuff(BuffType.MengLiangYiJiaFang))
                ModifyDef(delta * GetBuffValue(BuffType.MengLiangYiJiaFang));
            if (HasTalent(135)) ModifyHp(delta);
            if (HasBuff(BuffType.XiaCiJiaGuaXiangZaoChengShangHai))
            {
                int v = GetBuffValue(BuffType.XiaCiJiaGuaXiangZaoChengShangHai);
                RemoveBuff(BuffType.XiaCiJiaGuaXiangZaoChengShangHai);
                CombatMath.ApplyDamage(this, Opponent, DamageInfo.Create(this, DamageType.Damage, v, skipWoundCheck: true));
            }
        }
        if (delta > 0 && (buffType == BuffType.GuaXiang || buffType == BuffType.XingLi)
            && HasBuff(BuffType.XingYueQianKunShan))
            CombatMath.ApplyDamage(this, Opponent,
                DamageInfo.Create(this, DamageType.Damage, delta * GetBuffValue(BuffType.XingYueQianKunShan), skipWoundCheck: true));

        // 失去卦象：记录失去量；梦雨雷第一回合把失去的补回来。
        if (delta < 0 && buffType == BuffType.GuaXiang)
        {
            int lost = -delta;
            if (lost > 0)
            {
                ModifyBuffValue(BuffType.JiLuShiQuGuaXiang, lost);
                if (GetBuffValue(BuffType.MengYuLei) > 0 && GetBuffValue(BuffType.MengYuleiHuiHeXianZhi) == 0)
                {
                    ModifyBuffValue(BuffType.MengYuleiHuiHeXianZhi, 1);
                    ModifyBuffValue(BuffType.GuaXiang, lost);
                }
            }
        }
        // 失去星力：紫芒星宝转加攻。
        if (buffType == BuffType.XingLi && delta < 0 && HasBuff(BuffType.ZiMangXingBao) && -delta > 0)
            ModifyBuffValue(BuffType.JiaGong, -delta);

        // 失去气势：生气凌人反伤。
        if (delta < 0 && buffType == BuffType.QiShi && HasBuff(BuffType.ShengQiLingRen))
            CombatMath.ApplyDamage(this, Opponent,
                DamageInfo.Create(this, DamageType.Damage, -delta * GetBuffValue(BuffType.ShengQiLingRen), skipWoundCheck: true));

        // 给对手挂虚化时，仙命405 让对手加灵气。
        if (delta > 0 && buffType == BuffType.XuRuo && Opponent.HasFateStrategy(405)) Opponent.ModifyAnima(1);

        // 气势（天赋209 + 拳家式）：一次性转 2 身法。
        if (delta > 0 && buffType == BuffType.QiShi && HasTalent(209) && HasBuff(BuffType.QuanJiaShi)
            && !HasBuff(BuffType.LingQiBengYongQuanShengXiao))
        {
            ModifyBuffValue(BuffType.LingQiBengYongQuanShengXiao, 1);
            ModifyBuffValue(BuffType.ShenFa, 2);
        }
        // 剑意加防。
        if (delta > 0 && buffType == BuffType.JianYi && HasBuff(BuffType.JiaJianYiJiaFang))
            ModifyDef(delta * GetBuffValue(BuffType.JiaJianYiJiaFang));

        // ── 五行聚灵：激活任意一灵时的一长串后续 ──
        if (delta > 0 && IsWuXingActivation(buffType))
        {
            // 共鸣34：一次性再行动（扣血换）。
            if (IsTalentResonanceEffective(34) && !CheckTalentResonanceTempFlag(34)
                && !HasBuff(BuffType.ExActionAgain) && GetBuffValue(BuffType.ZaiCiXingDong) < ActionAgainPerRound)
            {
                SetTalentResonanceTempFlag(34, true);
                ModifyHp(-Config.ResonanceOtherParams(34).At(0));
                ModifyBuffValue(BuffType.ExActionAgain, 1);
            }
            // 天赋79/10079/20079/30079：加防 / 回血 / 加锋锐 / 加灵气。
            if (HasTalent(79)) ModifyDef(Config.TalentOtherParams(79).At(0));
            if (HasTalent(10079)) ModifyHp(Config.TalentOtherParams(10079).At(0));
            if (HasTalent(20079)) ModifyBuffValue(BuffType.FengRui, Config.TalentOtherParams(20079).At(0));
            if (HasTalent(30079)) ModifyAnima(Config.TalentOtherParams(30079).At(0));
            // 浑元无极阵：一次性再行动。
            if (HasBuff(BuffType.HunYuanWuJiZhen) && !HasBuff(BuffType.ExActionAgain)
                && GetBuffValue(BuffType.ZaiCiXingDong) < ActionAgainPerRound)
            {
                ModifyBuffValue(BuffType.HunYuanWuJiZhen, -1);
                ModifyBuffValue(BuffType.ExActionAgain, 1);
            }
            // 天赋112（共鸣124 时门槛放到 2 层）：按**本次落值**分派每灵的加成。
            if (HasTalent(112) && (GetBuffValue(buffType) == 1
                || (IsTalentResonanceEffective(124) && GetBuffValue(buffType) == 2)))
            {
                switch (buffType)
                {
                    case BuffType.JiHuoJinLing: ModifyBuffValue(BuffType.FengRui, 4); break;
                    case BuffType.JiHuoShuiLing: ModifyBuffValue(BuffType.ShuiShi, 2); break;
                    case BuffType.JiHuoMuLing: ModifyBuffValue(BuffType.JiaGong, 1); break;
                    case BuffType.JiHuoHuoLing:
                        Opponent.ModifyHp(-7);
                        Opponent.ModifyMaxHp(-7);
                        break;
                    case BuffType.JiHuoTuLing: ModifyDef(12); break;
                }
            }
            // 刻印·还元灵：再给一份（火灵是 8 而非 7）。
            if (HasBuff(BuffType.KeYinHuanYuanLing))
            {
                ModifyBuffValue(BuffType.KeYinHuanYuanLing, -1);
                switch (buffType)
                {
                    case BuffType.JiHuoJinLing: ModifyBuffValue(BuffType.FengRui, 4); break;
                    case BuffType.JiHuoShuiLing: ModifyBuffValue(BuffType.ShuiShi, 2); break;
                    case BuffType.JiHuoMuLing: ModifyBuffValue(BuffType.JiaGong, 1); break;
                    case BuffType.JiHuoHuoLing:
                        Opponent.ModifyHp(-8);
                        Opponent.ModifyMaxHp(-8);
                        break;
                    case BuffType.JiHuoTuLing: ModifyDef(10); break;
                }
            }
            // 天赋138：削对手血上限（火灵多一段，共鸣47 再加两段）。
            if (HasTalent(138))
            {
                int n = Config.TalentOtherParams(138).At(1);
                if (buffType == BuffType.JiHuoHuoLing) n += Config.TalentOtherParams(138).At(2);
                if (HasTalentResonance(47) && buffType == BuffType.JiHuoHuoLing) n += Config.ResonanceOtherParams(47).At(0);
                if (IsTalentResonanceEffective(47)) n += Config.ResonanceOtherParams(47).At(1);
                Opponent.ModifyHp(-n);
                Opponent.ModifyMaxHp(-n);
            }
            if (HasTalent(200) && buffType == BuffType.JiHuoMuLing) ModifyMaxHp(2);
            if (HasTalent(202) && GetBuffValue(buffType) == 1) ModifyAnima(1);
            // 仙命310：第二层起一次性加灵气。
            if (HasFateStrategy(310) && GetBuffValue(buffType) > 1 && !HasBuff(BuffType.WuXingJuLingYiChuFa))
            {
                ModifyBuffValue(BuffType.WuXingJuLingYiChuFa, 1);
                ModifyAnima(1);
            }
            // 刻印31：已有该灵 >1 层时补一次打伤。
            if (Subs.HasKeYinType(this, 31) && GetBuffValue(buffType) > 1)
                CombatMath.ApplyDamage(this, Opponent,
                    DamageInfo.Create(this, DamageType.Damage, Subs.KeYinOtherparam(this, 31, 0), skipWoundCheck: true));
            // 混天克：按相克环转一灵。
            if (GetBuffValue(BuffType.HunTianKe) > 0)
            {
                ModifyBuffValue(BuffType.HunTianKe, -1);
                ModifyBuffValue(WuXingFunctions.GetKeZhiWuXing(buffType), 1);
            }
            // 牌库有 372：按它的 otherParams[0] 合计打一次伤。
            if (Subs.HasCardInDeck(this, 372))
                CombatMath.ApplyDamage(this, Opponent,
                    DamageInfo.Create(this, DamageType.Damage, CardTotalOtherparamInDeck(372, 0), skipWoundCheck: true));
            // 震银心法：按层数打一次伤。
            if (GetBuffValue(BuffType.ZhenYinXinFa) > 0)
                CombatMath.ApplyDamage(this, Opponent,
                    DamageInfo.Create(this, DamageType.Damage, GetBuffValue(BuffType.ZhenYinXinFa), skipWoundCheck: true));
            // 仙命411：水灵带一土灵，土灵则加防。
            if (HasFateStrategy(411))
            {
                switch (buffType)
                {
                    case BuffType.JiHuoShuiLing: ModifyBuffValue(BuffType.JiHuoTuLing, 1); break;
                    case BuffType.JiHuoTuLing: ModifyDef(Config.FateOtherParams(411).At(0)); break;
                }
            }
        }

        // 练云：记录云剑使用层数。
        if (delta > 0 && buffType == BuffType.LianYun)
            ModifyBuffValue(BuffType.YongGuoYunJianJiShu, delta);
        // 失去负面状态：记录失去量。
        if (delta < 0 && negative) ModifyBuffValue(BuffType.JiLuSHiQuFuMian, -delta);
        // 锋锐 / 水势 / 气势：记录累计获得量与次数。
        if (delta > 0 && buffType == BuffType.FengRui)
        {
            ModifyBuffValue(BuffType.JiaFengRuiCiShu, 1);
            ModifyBuffValue(BuffType.JiLuJiaGuoDeFengRui, delta);
        }
        if (delta > 0 && buffType == BuffType.ShuiShi)
        {
            ModifyBuffValue(BuffType.JiaShuiShiCiShu, 1);
            ModifyBuffValue(BuffType.JiLuJiaGuoDeShuiShi, delta);
        }
        if (delta > 0 && buffType == BuffType.QiShi) ModifyBuffValue(BuffType.JiLuJiaGuoDeQiShi, delta);

        // 刻印109 / 天赋257：气势变动（增或减）都按绝对值加防。
        if (Subs.HasKeYinType(this, 109) && buffType == BuffType.QiShi && delta != 0)
            ModifyDef(Math.Abs(delta) * Subs.KeYinOtherparam(this, 109, 0));
        if (HasTalent(257) && buffType == BuffType.QiShi && delta != 0)
            ModifyDef(Math.Abs(delta));
        // 记录本场失去的气势（原码紧跟天赋 257）。早先缺失 —— 读它的牌（按失去气势计数）拿到的恒为 0。
        if (buffType == BuffType.QiShi && delta < 0)
            ModifyBuffValue(BuffType.JiLuShiQuQiShi, -delta);

        // ── 气势上限（用户点名的机制）──
        // 气势超过上限时：共鸣81 先抬一格上限；然后把超出的部分截掉，**截掉的部分转成加防**；
        // 若有生气凌人，再按「截掉量 × 层数」给对手一次伤害。
        if ((buffType == BuffType.QiShi || buffType == BuffType.QiShiShangXian)
            && GetBuffValue(BuffType.QiShi) > GetBuffValue(BuffType.QiShiShangXian))
        {
            if (buffType == BuffType.QiShi && delta > 0 && IsTalentResonanceEffective(81))
                SetBuffValue(BuffType.QiShiShangXian, GetBuffValue(BuffType.QiShiShangXian) + 1);

            int excess = GetBuffValue(BuffType.QiShi) - GetBuffValue(BuffType.QiShiShangXian);
            SetBuffValue(BuffType.QiShi, GetBuffValue(BuffType.QiShiShangXian));
            ModifyDef(excess);
            if (HasBuff(BuffType.ShengQiLingRen))
                CombatMath.ApplyDamage(this, Opponent,
                    DamageInfo.Create(this, DamageType.Damage, excess * GetBuffValue(BuffType.ShengQiLingRen), skipWoundCheck: true));
        }

        // 共鸣30：卦象一次性加灵气。
        if (IsTalentResonanceEffective(30) && delta > 0 && buffType == BuffType.GuaXiang && !CheckTalentResonanceTempFlag(30))
        {
            SetTalentResonanceTempFlag(30, true);
            ModifyAnima(1);
        }
        // 对手的共鸣70：被挂负面状态时给我方一层内伤（每次战斗一次）。
        if (Opponent.IsTalentResonanceEffective(70) && delta > 0 && negative
            && !Opponent.CheckTalentResonanceTempFlag(70) && buffType != BuffType.Min)
        {
            Opponent.SetTalentResonanceTempFlag(70, true);
            ModifyBuffValue(BuffType.NeiShang, 1);
        }
    }

    /// <summary>
    /// ⬜ 原码用 <c>buffs.ContainsKey(num)</c>（**值为 0 也算存在**），与 <see cref="HasBuff"/> 的「值 != 0」不同。
    /// 写入口必须用「键是否存在」的语义，否则 0 值 buff 的累加会走错支路。
    /// </summary>
    private bool _buffsPresent(BuffType buff) => _buffs is not null && _buffs.ContainsKey(buff);

    /// <summary>五行「激活」buff（金木水火土灵）。</summary>
    private static bool IsWuXingActivation(BuffType b)
        => b == BuffType.JiHuoJinLing || b == BuffType.JiHuoMuLing || b == BuffType.JiHuoShuiLing
        || b == BuffType.JiHuoTuLing || b == BuffType.JiHuoHuoLing;
}
