using UnityEngine;

namespace BeyondSpiderAssembly
{
    // Anything a kinetic round resolves against during penetration: nano armour and missiles
    // (ADR-0007). Terrain — a collider with neither this nor a BlockBehaviour — hard-stops a
    // round; a plain ship BlockBehaviour with no IKineticTarget is passed straight through.
    //
    // Armour carries a repairable integrity layer plus a structural layer and can ricochet;
    // missiles carry structural HP only and never ricochet. Both expose the same handful of
    // members so SpaceKineticRound can march through them uniformly.
    public interface IKineticTarget
    {
        // Structural HP still standing between the round and passing through, in damage units.
        float RemainingKineticHP { get; }

        // This target's own rigidbody velocity, so impact energy is measured relative to it.
        Vector3 KineticVelocity { get; }

        // Armour ricochets a non-penetrating oblique hit; missiles never do.
        bool CanRicochet { get; }

        // Once true the round treats this as empty space (breached armour / destroyed missile).
        bool IsBreached { get; }

        // Deposit `damage` of structural harm; the target clears / destroys itself when spent.
        void ApplyKineticDamage(float damage);
    }
}
