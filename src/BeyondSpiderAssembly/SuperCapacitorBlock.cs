using System.Collections.Generic;
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

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Super Capacitor";
            BusMenu = AddMenu("Energy Bus", 3, new List<string> { "Armor", "Shield", "Weapon", "Universal" }, false);
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
            RecomputeCapacity();
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            Bus = (EnergyBus)Mathf.Clamp(BusMenu.Value, 0, 3);
            RecomputeCapacity();
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Capacitors);
                registered = true;
            }
            if (Body != null)
            {
                Body.mass = 0.2f + Capacity / 2500f;
            }
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
    }
}
