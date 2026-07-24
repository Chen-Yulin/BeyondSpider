using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // Reactor/capacitor secondary-explosion triggers (damage-refinement spec): a direct hit, or a
    // missile blast with clear line of sight to the block's own center, sets off the stored energy
    // — far beyond what an ordinary hit/blast would do on its own. Two entry points:
    //   TryDirectHit      — a kinetic round's or missile's own impact collider IS the reactor/capacitor.
    //   TryProximityBlast — a missile warhead going off nearby: every reactor/capacitor within the
    //                       blast radius detonates UNLESS intact armor sits on the straight line
    //                       between the blast origin and its center (WW2-Naval Bomb.ExploDestroy's
    //                       armor-in-the-way check, simplified to a binary line-of-sight test since
    //                       the user's spec is "no armor blocking", not a thickness budget).
    // Both are safe to call redundantly (a missile's contact point is trivially "within radius,
    // clear LOS" of whatever it just touched) — SpaceShipCore/SuperCapacitorBlock.Detonate() gate
    // on their own `detonated` flag, so a second call is a no-op.
    public static class SubsystemDetonation
    {
        public static bool TryDirectHit(Collider hitCollider)
        {
            if (hitCollider == null || !NetAuthority.IsAuthority)
            {
                return false;
            }

            SpaceShipCore core = hitCollider.GetComponentInParent<SpaceShipCore>();
            if (core != null && core.IsAlive)
            {
                core.Detonate();
                return true;
            }

            SuperCapacitorBlock capacitor = hitCollider.GetComponentInParent<SuperCapacitorBlock>();
            if (capacitor != null && capacitor.IsAlive)
            {
                capacitor.Detonate();
                return true;
            }

            // Thrusters (ADR-0018) join the same list: a high-power electrical component that,
            // unlike the reactor, has to be mounted on the hull exterior — so the efficient big
            // nozzle is also a big bomb the player cannot bury.
            ThrusterBlock thruster = hitCollider.GetComponentInParent<ThrusterBlock>();
            if (thruster != null && thruster.IsAlive)
            {
                thruster.Detonate();
                return true;
            }

            return false;
        }

        public static void TryProximityBlast(Vector3 origin, float blastRadius)
        {
            if (!NetAuthority.IsAuthority || blastRadius <= 0f)
            {
                return;
            }

            foreach (ShipState ship in SpaceCombatRegistry.Ships)
            {
                // Backward loops: Detonate() may pull the block out of these very lists on its way
                // through OnSimulateStop-style cleanup, so a forward index would skip an entry.
                for (int i = ship.Cores.Count - 1; i >= 0; i--)
                {
                    SpaceShipCore core = ship.Cores[i];
                    if (core != null && core.IsAlive && HasLineOfSight(origin, core.transform.position, blastRadius))
                    {
                        core.Detonate();
                    }
                }
                for (int i = ship.Capacitors.Count - 1; i >= 0; i--)
                {
                    SuperCapacitorBlock capacitor = ship.Capacitors[i];
                    if (capacitor != null && capacitor.IsAlive && HasLineOfSight(origin, capacitor.transform.position, blastRadius))
                    {
                        capacitor.Detonate();
                    }
                }
                for (int i = ship.Thrusters.Count - 1; i >= 0; i--)
                {
                    ThrusterBlock thruster = ship.Thrusters[i];
                    if (thruster != null && thruster.IsAlive && HasLineOfSight(origin, thruster.transform.position, blastRadius))
                    {
                        thruster.Detonate();
                    }
                }
            }
        }

        // Binary line-of-sight test (WW2-Naval Bomb reference, simplified per the user's literal
        // spec): blocked by ANY intact (non-breached) nano armor plate on the segment, regardless
        // of thickness. Breached armor is transparent, same as it already is to kinetic rounds
        // (ADR-0007).
        private static bool HasLineOfSight(Vector3 origin, Vector3 target, float blastRadius)
        {
            Vector3 delta = target - origin;
            float distance = delta.magnitude;
            if (distance > blastRadius)
            {
                return false;
            }
            if (distance < 0.05f)
            {
                return true;
            }

            Vector3 direction = delta / distance;
            RaycastHit[] hits = Physics.RaycastAll(origin, direction, distance);
            for (int i = 0; i < hits.Length; i++)
            {
                NanoArmorBehaviour armor = hits[i].collider.GetComponentInParent<NanoArmorBehaviour>();
                if (armor != null && !armor.IsBreached)
                {
                    return false;
                }
            }
            return true;
        }

        // Secondary blast from a detonated reactor/capacitor: damages every armor plate within
        // range with linear falloff (same shape as MissileProjectile.ApplyBlastDamageToArmor) and
        // gives everything with a rigidbody in range an outward shove — the ship-scale echo of a
        // subsystem going up, on top of the FX-only DetonateMsg clients receive.
        public static void ApplySecondaryBlast(Vector3 origin, float blastRadius, float baseDamage, float baseForce)
        {
            if (!NetAuthority.IsAuthority || blastRadius <= 0f)
            {
                return;
            }

            foreach (ShipState ship in SpaceCombatRegistry.Ships)
            {
                for (int i = 0; i < ship.Armor.Count; i++)
                {
                    NanoArmorBehaviour armor = ship.Armor[i];
                    if (armor == null)
                    {
                        continue;
                    }
                    float distance = Vector3.Distance(origin, armor.transform.position);
                    if (distance > blastRadius)
                    {
                        continue;
                    }
                    float falloff = Mathf.Clamp01(1f - distance / blastRadius);
                    armor.ApplyPhysicalDamage(baseDamage * falloff);
                }
            }

            Collider[] hits = Physics.OverlapSphere(origin, blastRadius);
            for (int i = 0; i < hits.Length; i++)
            {
                Rigidbody rb = hits[i].attachedRigidbody;
                if (rb == null)
                {
                    continue;
                }
                Vector3 offset = rb.position - origin;
                float distance = offset.magnitude;
                Vector3 dir = distance > 0.01f ? offset / distance : Random.onUnitSphere;
                float falloff = Mathf.Lerp(1f, SpaceBalance.BlastForceFalloffFloor, Mathf.Clamp01(distance / blastRadius));
                rb.AddForce(dir * baseForce * falloff, ForceMode.Impulse);
            }
        }
    }

    // MP relay for the reactor/capacitor detonation FX (host-authoritative, ADR-0015): the real
    // damage/joint-break happens host-side inside SpaceShipCore.Detonate()/SuperCapacitorBlock.Detonate()
    // and reaches clients through vanilla machine sync (the block physically tearing free), same as
    // an armor breach. This message carries only what a client needs to play the matching FX at the
    // right place — no block identity required, unlike MissileLauncherNet's mirror stream.
    public class SubsystemDetonationNet : SingleInstance<SubsystemDetonationNet>
    {
        public override string Name { get { return "BeyondSpider Subsystem Detonation Net"; } }

        // (position, driftVelocity, scale, kind) — kind 0 = reactor (totalPower), 1 = capacitor
        // (Capacity), 2 = thruster (MaxThrust). A thruster going up is a stored-energy pop like a
        // capacitor's, so it deliberately shares that FX recipe rather than getting its own.
        public static MessageType DetonateMsg = ModNetworking.CreateMessageType(
            DataType.Vector3, DataType.Vector3, DataType.Single, DataType.Integer);

        public void DetonateReceiver(Message msg)
        {
            Vector3 position = (Vector3)msg.GetData(0);
            Vector3 velocity = (Vector3)msg.GetData(1);
            float scale = (float)msg.GetData(2);
            int kind = (int)msg.GetData(3);

            if (kind == 0)
            {
                SpaceEffectAssets.PlayReactorExplosion(position, velocity, scale);
            }
            else
            {
                SpaceEffectAssets.PlayCapacitorExplosion(position, velocity, scale);
            }
        }
    }
}
