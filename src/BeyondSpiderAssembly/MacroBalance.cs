using UnityEngine;

namespace BeyondSpiderAssembly
{
    // Topmost balance layer: four macro dials the designer tunes to reshape the whole economy at
    // once. Everything concrete (weapon/shield/armor exchange rates, active-defense energy draw,
    // reactor output) is DERIVED from these four in SpaceBalance — edit here, not in the blocks.
    //
    // The combat economy has exactly four independent levers, and these four map onto them with no
    // overlap. Three are "which side is cheaper" splits laid out as a tree; one is raw supply:
    //
    //     供给 ─────────────────────────  EnergyAbundance   → reactor output
    //     攻 ÷ 防 ──────────────────────  OffenseEfficiency → weapon rate vs ALL defence
    //       防御 内部:
    //         主动 ÷ 被动 ──────────────  ActivePassiveSplit→ active-defence energy vs passive
    //           被动 内部:
    //             护盾 ÷ 装甲 ──────────  ShieldArmorSplit  → shield rate vs armor rate
    //
    //   主动防御 (active) = AA 防空炮台 / 近防炮 / 点防御导弹 (they hunt the incoming threat).
    //   被动防御 (passive) = 护盾 / 装甲            (they sit on the hull and soak / deflect).
    //
    // Each split is a reciprocal tilt around 0.5, so 0.5 = neutral (keeps the current hand-tuned
    // ratio) and the two ends push cost from one side to the other, approaching "one side free" at
    // the extremes (clamped, so 0/1 are near-free asymptotes rather than exact 0/∞). At the neutral
    // point (0.5,0.5,0.5, abundance 1) every derived multiplier below is exactly 1, so SpaceBalance
    // returns precisely the values it had before this layer existed.
    public static class MacroBalance
    {
        // 1. 进攻效率 — attack cost vs defence cost. 0 = 防御相较攻击无成本 (defence ~free, offence
        //    inefficient); 1 = 攻击相较防御无成本 (offence ~free); 0.5 = 均衡 (= current baseline).
        public const float OffenseEfficiency = 0.5f;

        // 2. 能量充沛度 — reactor (power-generation) output multiplier. 1 = current, >1 more
        //    abundant, →0 starved, →+∞ unlimited. The only dial that is not a 0..1 split.
        public const float EnergyAbundance = 1f;

        // 3. 护盾-装甲占比 — within PASSIVE defence, which system you lean on. 0 = 偏重护盾 (shield is
        //    the cheap primary, armor the dear backup); 1 = 偏重装甲; 0.5 = current ratio.
        public const float ShieldArmorSplit = 0.5f;

        // 4. 主动-被动防御 — within defence, active vs passive. 0 = 主动防御耗能极低, 偏主动 (AA/CIWS/
        //    interceptors cheap); 1 = 被动防御耗能极低, 偏被动 (护盾/装甲 cheap); 0.5 = 均衡.
        public const float ActivePassiveSplit = 0.5f;

        // Reciprocal tilt: 0.5 → 1, →0 approaches 0 (that side ~free), →1 approaches ~49 (that side
        // dear). Clamped off the exact endpoints so a 0 or 1 dial can't divide-by-zero downstream.
        private static float Tilt(float x)
        {
            x = Mathf.Clamp(x, 0.02f, 0.98f);
            return x / (1f - x);
        }

        // Derived multipliers (all 1 at the neutral point). SpaceBalance multiplies its reference
        // rates by these. Nested tilts compose: e.g. armor cost = defence-level × passive-share ×
        // armor-share, so pushing offence, passive and armor all up stacks onto ArmorMult.
        public static readonly float OffenseMult;   // × RefWeaponEnergyPerDamage
        public static readonly float ShieldMult;    // × RefShieldEnergyPerDamage
        public static readonly float ArmorMult;     // × RefArmorEnergyPerHP
        public static readonly float ActiveMult;    // × active-defence energy draws (flak/CIWS/interceptor)
        public static readonly float ReactorMult;   // × RefReactorPowerPerVolume

        static MacroBalance()
        {
            float defenseMult = Tilt(OffenseEfficiency);   // offence-eff high → defence dearer
            OffenseMult = 1f / defenseMult;                // offence-eff high → offence cheaper

            float apTilt = Tilt(ActivePassiveSplit);
            ActiveMult = defenseMult * apTilt;             // ap→0 active cheap, ap→1 active dear
            float passiveMult = defenseMult / apTilt;      // ap→0 passive dear, ap→1 passive cheap

            float saTilt = Tilt(ShieldArmorSplit);
            ShieldMult = passiveMult * saTilt;             // sa→0 shield cheap, sa→1 shield dear
            ArmorMult = passiveMult / saTilt;              // sa→0 armor dear,  sa→1 armor cheap

            ReactorMult = Mathf.Max(0f, EnergyAbundance);
        }
    }
}
