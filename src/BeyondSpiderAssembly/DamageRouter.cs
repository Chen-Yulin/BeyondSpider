using UnityEngine;

namespace BeyondSpiderAssembly
{
    public static class DamageRouter
    {
        public static bool RoutePhysicalHit(Collider hitCollider, float damage)
        {
            if (hitCollider == null || damage <= 0f)
            {
                return false;
            }

            NanoArmorBehaviour armor = hitCollider.GetComponentInParent<NanoArmorBehaviour>();
            if (armor == null)
            {
                return false;
            }

            armor.ApplyPhysicalDamage(damage);
            return true;
        }
    }
}
