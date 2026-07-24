using System.Collections.Generic;
using Modding;
using Modding.Blocks;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class SuperCapacitorBlock : SpaceBlock
    {
        public MMenu BusMenu;
        public MInfo CapacityInfo;
        public EnergyBus Bus;
        public float Capacity;

        private const float MinScaleComponent = 0.05f;
        // Capacity used to be a player-set MSlider multiplied by a scale factor; now it's computed
        // directly from the block's build-mode volume instead, same idea as RailgunBarrelBlock's
        // caliber/bore length/muzzle velocity — see agent-besiege-mod-guide.md.
        private const float CapacityPerVolume = 1000f;

        // See agent-besiege-mod-guide.md's "注册时序规范" — retried each tick until it
        // succeeds, since ShipState may not exist yet when this OnSimulateStart runs.
        private bool registered;
        private bool detonated;

        public bool IsAlive { get { return !detonated && BlockBehaviour != null && BlockBehaviour.isSimulating; } }

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Super Capacitor";
            // Single-select, never a proportional split (ADR-0018): a capacitor's gameplay value is
            // "which line do I lose when this bank gets shot", and mixing dilutes exactly that.
            BusMenu = AddMenu("Energy Bus", 3, new List<string> { "Armor", "Shield", "Weapon", "Universal", "Thrust" }, false);
            CapacityInfo = AddInfo("Capacity", "BSCapacityInfo");
            RecomputeCapacity();
        }

        private void RecomputeCapacity()
        {
            Vector3 scale = transform.localScale;
            float x = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.x));
            float y = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.y));
            float z = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.z));
            Capacity = CapacityPerVolume * x * y * z;
            CapacityInfo.Set(Capacity.ToString("F0") + " MJ");
        }

        public override void BuildingUpdate()
        {
            base.BuildingUpdate();
            RecomputeCapacity();
        }

        public override void SimulateFixedUpdateAlways()
        {
            base.SimulateFixedUpdateAlways();
            // RecomputeCapacity();
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            Bus = (EnergyBus)Mathf.Clamp(BusMenu.Value, 0, EnergyGrid.BusCount - 1);
            RecomputeCapacity();
            ShipState ship = OwnShip();
            if (ship != null)
            {
                OnAssignedToShip(ship);
            }
            if (Body != null)
            {
                Body.mass = 0.2f + Capacity / 2500f;
            }
        }

        public override void OnAssignedToShip(ShipState ship)
        {
            SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Capacitors);
            registered = true;
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Capacitors);
            }
            registered = false;
        }

        public override void SimulateFixedUpdateHost()
        {
            if (registered)
            {
                return;
            }
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Capacitors);
                registered = true;
            }
        }

        // Capacitor detonation (damage-refinement spec): a direct hit, or a missile blast with
        // clear line of sight to this bank (SubsystemDetonation), dumps its whole stored charge at
        // once — a lightning/ion discharge far beyond ordinary blast damage. Same debris-not-erased
        // pattern as SpaceShipCore.Detonate(): own joints zeroed + explosion force, IsAlive false
        // immediately drops it out of SpaceCombatRuntime's capacity sum.
        public void Detonate()
        {
            if (detonated || !NetAuthority.IsAuthority)
            {
                return;
            }
            detonated = true;
            // Diagnostic (MP playtest: "ship energy dead" traced to subsystem detonations — dead
            // capacitors are skipped by the energy tick, so a ship that lost its banks reads 0%).
            Debug.Log("[BeyondSpider] capacitor DETONATED on player " + PlayerID + " ship ("
                + Bus + " bank, " + Capacity.ToString("F0") + " MJ) at " + transform.position);

            Vector3 position = transform.position;
            Vector3 driftVelocity = Body != null ? Body.velocity : Vector3.zero;

            if (StatMaster.isMP)
            {
                ModNetworking.SendToAll(SubsystemDetonationNet.DetonateMsg.CreateMessage(
                    position, driftVelocity, Capacity, 1));
            }

            SpaceEffectAssets.PlayCapacitorExplosion(position, driftVelocity, Capacity);
            BreakOwnConnectionJoints();
            SubsystemDetonation.ApplySecondaryBlast(position,
                SpaceBalance.CapacitorBlastRadius(Capacity),
                SpaceBalance.CapacitorBlastDamage(Capacity),
                SpaceBalance.CapacitorBlastForce(Capacity));
        }
    }
}
