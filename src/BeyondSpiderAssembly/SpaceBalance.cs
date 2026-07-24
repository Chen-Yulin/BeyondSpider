using UnityEngine;

namespace BeyondSpiderAssembly
{
    // Central balance model for the "energy–firepower–armor" triangle (电磁炮 / 装甲 / 护盾).
    // Every offensive, defensive and repair number in the mod is derived from the handful of
    // design constants here instead of being a per-block magic number, so the three systems
    // stay in a fixed economic relationship no matter how each block is scaled. Sitting ABOVE
    // this is MacroBalance — four macro dials (进攻效率 / 能量充沛度 / 护盾-装甲占比 / 主动-被动
    // 防御) that scale the exchange rates and reactor output below; the four are all 1×/neutral
    // by default, so out of the box SpaceBalance behaves exactly as if MacroBalance weren't there. Before this
    // existed, each block's damage/energy/HP was an independent placeholder and they didn't
    // line up (a railgun shot cost ~10x its own weapon budget yet one-shot an armour block 5x
    // over, and a shield negated it for pocket change).
    //
    // Two "algorithms" the user asked for live here:
    //
    //  1. Energy-usage algorithm — one exchange rate per bus converts a combat effect into the
    //     energy it costs. A ship's reactor budget on a bus therefore maps directly onto a
    //     sustainable rate of that effect (damage/s, repair/s, mitigation/s):
    //        Weapon bus : WeaponEnergyPerDamage  MJ per point of potential damage dealt
    //        Armor bus  : ArmorEnergyPerHP        MJ per point of integrity HP repaired
    //        Shield bus : ShieldEnergyPerDamage   MJ per point of velocity-damage negated
    //     The rates are deliberately ordered shield > weapon > armour: mitigating a hit
    //     proactively (area, ignores IFF, removes the whole velocity term) is the dearest,
    //     repairing afterwards is the cheapest (it only restores the regenerating layer and
    //     leaves permanent structural loss behind), firing sits in between.
    //
    //  2. Damage-and-recovery algorithm — everything is measured in Armor Units (AU), where
    //     1 AU = one default nano-armour block's total hit points (ArmorUnitHP). Weapons are
    //     tuned by how many reference shots strip 1 AU; repair by how it compares to a single
    //     weapon's DPS; shields by how much of a round's velocity-damage they shed per burst.
    //
    // Retuning game feel should be done HERE, not in the blocks: the blocks only read these.
    public static class SpaceBalance
    {
        // ---- Reference frame: the Armor Unit (AU) ----
        // Integrity is the self-repairing layer (refills from the Armor bus); structural is the
        // permanent layer that weakens joints as it drops and never self-repairs. A hit spends
        // integrity first, then structural. Total = 1 AU, the yardstick every weapon is rated
        // against.
        public const float ArmorIntegrityPool = 400f;
        public const float ArmorStructuralPool = 600f;
        public const float ArmorUnitHP = ArmorIntegrityPool + ArmorStructuralPool; // 1000 HP = 1 AU

        // ---- Exchange rates (energy-usage algorithm) ----
        // Reference rates at the neutral macro point; the live rates below are these scaled by the
        // MacroBalance multipliers, so the four macro dials reshape the whole economy from one place
        // (see MacroBalance). At neutral every multiplier is 1 and the live rates equal the Ref*.
        public const float RefWeaponEnergyPerDamage = 0.6f;   // MJ per potential damage point
        public const float RefArmorEnergyPerHP = 0.5f;        // MJ per integrity HP restored
        public const float RefShieldEnergyPerDamage = 0.9f;   // MJ per velocity-damage point negated
        public const float RefReactorPowerPerVolume = 1200f;  // MW per unit ship-core volume

        public static readonly float WeaponEnergyPerDamage = RefWeaponEnergyPerDamage * MacroBalance.OffenseMult;
        public static readonly float ArmorEnergyPerHP = RefArmorEnergyPerHP * MacroBalance.ArmorMult;
        public static readonly float ShieldEnergyPerDamage = RefShieldEnergyPerDamage * MacroBalance.ShieldMult;

        // Reactor output per unit core volume (SpaceShipCore reads this instead of its own constant),
        // scaled by 能量充沛度. And the multiplier every active-defence block (AA turret / CIWS /
        // interceptor launcher) applies to its Weapon-bus energy draw, scaled by 主动-被动防御 — the
        // one hook that makes those ad-hoc-energy blocks respond to the macro layer without touching
        // their damage models.
        public static readonly float ReactorPowerPerVolume = RefReactorPowerPerVolume * MacroBalance.ReactorMult;
        public static readonly float ActiveDefenseEnergyScale = MacroBalance.ActiveMult;

        // ---- Kinetic penetration model (damage side of algorithm 2, ADR-0007) ----
        // A round's whole damage budget is its velocity term — a scaled kinetic energy — with no
        // flat floor: damage(v) = mass * v^2 * KineticDamagePerKE, where v is the round's speed
        // RELATIVE to the target it strikes. Damage dealt equals kinetic energy lost (in the
        // target's rest frame), so a round bleeds this budget block by block as it penetrates.
        // KineticDamagePerKE was raised from the old 0.00005 (which sat beside a caliber floor)
        // so the default 400 mm / 2600 m·s railgun still deals ~560 at the muzzle from the KE
        // term alone — roughly two shots to strip one AU (400 integrity + 600 structural).
        public const float KineticDamagePerKE = 0.0000625f;

        // Below this (absolute) speed a round is spent and removed, so slowed / ricocheted rounds
        // don't linger.
        public const float RoundStallSpeed = 40f;

        // ---- Ricochet (ADR-0007) ----
        // A round that fails to penetrate armour (D < remaining HP) at an incidence angle (from
        // the surface normal) beyond RicochetMinAngleDeg ricochets with probability climbing
        // linearly to RicochetMaxProbability at a grazing 90°. A ricochet deposits only
        // RicochetDamageFraction of the surrendered budget as damage, the round keeps
        // RicochetKEKeep of its budget reflected off the surface, and the rest is dissipated —
        // the sole case where KE lost exceeds damage dealt. Missiles never ricochet.
        public const float RicochetMinAngleDeg = 45f;
        public const float RicochetMaxProbability = 0.9f;
        public const float RicochetKEKeep = 0.25f;
        public const float RicochetDamageFraction = 0.5f;
        public const float RicochetScatterDeg = 6f;

        // ---- Kinetic impact knockback (physical feedback alongside the damage-only model above) ----
        // A shell or missile striking armour that's still standing (HP != 0, i.e. not yet breached)
        // also shoves it in the round/missile's own travel direction at the exact impact point — a
        // fraction of the striker's own momentum (mass * speed), so heavier/faster hits knock harder
        // without a separate tuning knob per weapon. Placeholder fraction pending in-game tuning.
        public const float KineticImpactImpulseFraction = 0.0005f;

        // ---- Blast knockback (physical feedback alongside MissileBlastDamage/Radius above) ----
        // A missile's explosion also shoves everything nearby off course — including cannon shells
        // flying through the blast, which is the point: a well-timed intercept can throw off an
        // inbound salvo's aim. Unlike blast DAMAGE (which falls off linearly to zero at the radius),
        // this force floors at BlastForceFalloffFloor so the edge of the blast still gives a real
        // shove instead of fading out. Placeholder scale pending in-game tuning.
        public const float MissileBlastForcePerCharge = 10f;
        public const float BlastForceFalloffFloor = 0.3f;

        public static float MissileBlastForce(float warheadCharge)
        {
            return Mathf.Max(0f, warheadCharge) * MissileBlastForcePerCharge;
        }

        // ---- Missiles as penetrable targets (ADR-0007) ----
        // A point-defence interceptor gets this tiny fixed structural HP so a round (or any AA)
        // can shoot it down; a heavy missile's own Health slider is its structural HP directly.
        public const float InterceptorStructuralHP = 5f;
        // Heavy-missile mass and warhead: mass = Health*MissileMassPerHealth + WarheadCharge, so
        // toughness and payload both add weight; the warhead charge alone drives blast damage
        // (linear) and blast radius (cube-root of yield). Defaults calibrated so the stock
        // 650-HP / 24-charge missile keeps its old ~50 mass, ~384 blast damage, ~40 m radius.
        public const float MissileMassPerHealth = 0.04f;
        public const float MissileBlastDamagePerCharge = 16f;
        public const float MissileBlastRadiusScale = 3.9f;

        // ---- Missile guidance (ADR-0008) ----
        // Both guided munitions steer a single rear nozzle by proportional navigation plus a
        // closing-speed governor; thrust acts only along the nose and attitude slews at a finite
        // turn rate. The closing-speed caps bound the terminal turn radius (r = Vc^2 / a_lat), so the
        // heavy missile — far heavier than an interceptor — gets its own, lower cap or it could never
        // turn. Retune the feel here; the control law itself lives in MissileGuidance.
        public const float MissileNavConstant = 4f;             // PN gain N
        public const float MissileGovernorGain = 2f;            // Kv (1/s), closing-speed regulation
        public const float MissileClosingSpeedCap = 600f;       // interceptor closing-speed cap (m/s)
        public const float HeavyMissileClosingSpeedCap = 300f;  // heavy missile's dedicated lower cap
        public const float InterceptorTurnRate = 180f;          // deg/s
        public const float HeavyMissileTurnRate = 40f;          // deg/s
        public const float MissileFuzeRadius = 6f;              // hard proximity detonation (+ target radius)
        public const float MissileArmingRadius = 14f;           // closest-approach fuze active only inside this

        // ---- Missile self-hull avoidance ----
        // A missile guiding onto a target on the far side of its own launching ship must not carve
        // straight back through the hull to get there. MissileGuidance.BoxAvoidAccel adds a
        // repulsive bias against the ship's own core-local OBB (ShipPartition.ComputeHullBounds);
        // the margin scales with the hull's own size so bigger ships get a proportionally bigger
        // berth. Placeholder gain pending in-game tuning.
        public const float MissileHullAvoidMarginFactor = 0.6f; // fraction of the hull's largest half-extent
        public const float MissileHullAvoidGain = 1800f;

        // ---- Repair model (recovery side of algorithm 2) ----
        // Integrity refills at ArmorRepairFraction of its pool per second at full power (40 HP/s
        // for a default block). That per-block rate sits far below any single weapon's DPS, so
        // focused fire always out-damages the repair on one block; the Armor bus budget only
        // decides how many DIFFERENT chipped blocks a ship can mend at once. Repair slows toward
        // ArmorMinRepairSpeedFactor as integrity nears zero (heavy damage is hard to climb out of).
        public const float ArmorRepairFraction = 0.1f;
        public const float ArmorMinRepairSpeedFactor = 0.15f;

        // Potential (un-mitigated, un-reflected) muzzle damage of a kinetic round. Used both to
        // deal damage and to price the shot on the Weapon bus, so the weapon that pays to fire a
        // round and the shield that pays to stop it agree on exactly what the round is worth.
        public static float KineticDamage(float caliber, float mass, float speed)
        {
            // Pure KE term now (no caliber floor). `caliber` is kept in the signature for callers
            // that price by nominal muzzle speed, but is unused. See ADR-0007.
            return mass * speed * speed * KineticDamagePerKE;
        }

        // Heavy-missile derivations (ADR-0007): toughness (Health) and payload (Warhead Charge)
        // both add mass; the charge alone drives blast damage and (cube-root) blast radius.
        public static float MissileMass(float health, float warheadCharge)
        {
            return health * MissileMassPerHealth + Mathf.Max(0f, warheadCharge);
        }

        public static float MissileBlastDamage(float warheadCharge)
        {
            return Mathf.Max(0f, warheadCharge) * MissileBlastDamagePerCharge;
        }

        public static float MissileBlastRadius(float warheadCharge)
        {
            return MissileBlastRadiusScale * Mathf.Pow(Mathf.Max(0f, warheadCharge), 1f / 3f);
        }

        // Energy a burst weapon spends to fire one shot of the given potential damage: a small
        // fixed floor (capacitor switching / firing overhead) plus the per-damage exchange rate.
        public static float WeaponShotEnergy(float baseCost, float potentialDamage)
        {
            return baseCost + WeaponEnergyPerDamage * Mathf.Max(0f, potentialDamage);
        }

        // ---- Subsystem detonation (damage-refinement spec) ----
        // A direct hit, or a missile blast with clear line of sight (SubsystemDetonation), sets
        // off the reactor's own output or the capacitor's own stored charge as a secondary
        // explosion — cube-root radius scaling off the driving quantity, same shape as
        // MissileBlastRadius off warhead charge, so the numbers stay in a familiar unit language.
        // The reactor coefficients are picked so even a modest single-block core (~1200 MW) already
        // outranges and outdamages the heaviest missile (200-charge, ~23 m / ~3200 dmg) — "更大规模"
        // in the user's spec. Capacitor coefficients sit below that: a stored-energy pop, not a
        // fireball. Placeholder scale, pending in-game tuning (same status as the missile numbers
        // above).
        public const float ReactorBlastRadiusScale = 3.5f;
        public const float ReactorBlastDamagePerPower = 2.2f;
        public const float ReactorBlastForcePerPower = 0.5f;
        public const float CapacitorBlastRadiusScale = 1.4f;
        public const float CapacitorBlastDamagePerCapacity = 0.5f;
        public const float CapacitorBlastForcePerCapacity = 0.1f;

        public static float ReactorBlastRadius(float totalPower)
        {
            return ReactorBlastRadiusScale * Mathf.Pow(Mathf.Max(0f, totalPower), 1f / 3f);
        }

        public static float ReactorBlastDamage(float totalPower)
        {
            return Mathf.Max(0f, totalPower) * ReactorBlastDamagePerPower;
        }

        public static float ReactorBlastForce(float totalPower)
        {
            return Mathf.Max(0f, totalPower) * ReactorBlastForcePerPower;
        }

        public static float CapacitorBlastRadius(float capacity)
        {
            return CapacitorBlastRadiusScale * Mathf.Pow(Mathf.Max(0f, capacity), 1f / 3f);
        }

        public static float CapacitorBlastDamage(float capacity)
        {
            return Mathf.Max(0f, capacity) * CapacitorBlastDamagePerCapacity;
        }

        public static float CapacitorBlastForce(float capacity)
        {
            return Mathf.Max(0f, capacity) * CapacitorBlastForcePerCapacity;
        }

        // ---- Electric thruster (ADR-0018) ----
        // A thruster's ceiling comes from its NOZZLE CROSS-SECTION — the two block-scale extents
        // perpendicular to the thrust axis — not its volume (which is what the reactor and
        // capacitor use). One geometric number then feeds three things at once: thrust ceiling,
        // plume radius, and the efficiency curve below; volume could feed none of the latter two,
        // and would let a player stretch a long thin needle for free thrust. Length contributes
        // nothing, deliberately.
        //
        // Power follows the ideal-rocket relation P = F²/(2·ṁ) with mass flow ṁ ∝ nozzle area, so:
        //   full throttle  → P = ThrusterPowerPerArea × area          (linear in size, easy to balance)
        //   part throttle  → P scales with the SQUARE of output ratio  (half thrust, quarter power)
        // Cruising is therefore very cheap and hard burns very expensive, and one big nozzle beats
        // four small ones at equal thrust — paying for it in volume and center-of-mass balance.
        //
        // Placeholder scale pending in-game tuning, same status as every other constant here: at
        // these numbers a scale-1 thruster gives 900 kN for 400 MW at full burn, weighs 1.5, and
        // two of them accelerate a ~300-mass hull at ~6 m/s².
        public const float ThrustPerArea = 900f;          // kN per unit nozzle cross-section
        public const float ThrusterPowerPerArea = 400f;   // MW at full throttle, per unit cross-section
        public const float ThrusterMassPerThrust = 1f / 750f;

        // Thrust a nozzle of this cross-section can produce at full output.
        public static float ThrusterMaxThrust(float nozzleArea)
        {
            return ThrustPerArea * Mathf.Max(0f, nozzleArea);
        }

        // Instantaneous draw (MW) at an output ratio in [0,1] — the F²/area law above.
        public static float ThrusterPowerDraw(float nozzleArea, float outputRatio)
        {
            float ratio = Mathf.Clamp01(outputRatio);
            return ThrusterPowerPerArea * Mathf.Max(0f, nozzleArea) * ratio * ratio;
        }

        // Inverse of the above, used for brownout degradation: with only `energyRatio` of the
        // requested energy delivered, thrust falls off as its square root — the same law, run
        // backwards, so a half-fed thruster pushes at ~71% instead of some invented number.
        public static float ThrusterOutputForEnergyRatio(float energyRatio)
        {
            return Mathf.Sqrt(Mathf.Clamp01(energyRatio));
        }

        // Mass is LINEAR in thrust, deliberately unlike the reactor's 1.25-power penalty: stacking
        // a size penalty on an engine is counterproductive — bigger engines would push their own
        // weight worse, which is the opposite of what scaling one up should buy.
        public static float ThrusterMass(float maxThrust)
        {
            return 0.3f + Mathf.Max(0f, maxThrust) * ThrusterMassPerThrust;
        }

        // Thruster detonation (ADR-0018 §10): a high-power electrical component that, unlike the
        // reactor, MUST be mounted on the hull exterior — so the efficient big nozzle is also a big
        // bomb you can't bury. Scaled off MaxThrust, sitting just under the capacitor's numbers
        // (a thruster stores far less than a capacitor bank of the same nominal figure).
        public const float ThrusterBlastRadiusScale = 1.2f;
        public const float ThrusterBlastDamagePerThrust = 0.35f;
        public const float ThrusterBlastForcePerThrust = 0.08f;

        public static float ThrusterBlastRadius(float maxThrust)
        {
            return ThrusterBlastRadiusScale * Mathf.Pow(Mathf.Max(0f, maxThrust), 1f / 3f);
        }

        public static float ThrusterBlastDamage(float maxThrust)
        {
            return Mathf.Max(0f, maxThrust) * ThrusterBlastDamagePerThrust;
        }

        public static float ThrusterBlastForce(float maxThrust)
        {
            return Mathf.Max(0f, maxThrust) * ThrusterBlastForcePerThrust;
        }
    }
}
