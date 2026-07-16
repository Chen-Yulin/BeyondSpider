using UnityEngine;

namespace BeyondSpiderAssembly
{
    public static class DamageRouter
    {
        // Physical kinetic hits no longer route here — SpaceKineticRound resolves penetration
        // against IKineticTarget directly (ADR-0007). Only the laser still uses this router.
        //
        // The armor throttles the incoming beam damage by its own LaserDamageMultiplier (an intact
        // nano coating refracts ~95% away), so the caller passes the full un-reflected beam damage
        // and lets the armor decide how much actually bites. Non-armor colliders (missiles,
        // structure) are handled by the caller separately.
        //
        // Returns the armor it damaged (null when the collider wasn't nano-armor) so the laser can
        // pick the matching impact visual: an intact coating mirrors the beam, a stripped one
        // sprays sparks (NanoArmorBehaviour.ReflectsLaser / LaserImpactFx).
        public static NanoArmorBehaviour RouteLaserHit(Collider hitCollider, float damage)
        {
            if (hitCollider == null || damage <= 0f)
            {
                return null;
            }

            NanoArmorBehaviour armor = hitCollider.GetComponentInParent<NanoArmorBehaviour>();
            if (armor == null)
            {
                return null;
            }

            armor.ApplyLaserDamage(damage);
            return armor;
        }
    }
}
