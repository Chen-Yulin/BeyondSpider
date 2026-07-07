using System.Collections.Generic;
using Modding.Blocks;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class SuperCapacitorBlock : SpaceBlock
    {
        public MMenu BusMenu;
        public MSlider CapacityScale;
        public EnergyBus Bus;
        public float Capacity;

        // See agent-besiege-mod-guide.md's "注册时序规范" — retried each tick until it
        // succeeds, since ShipState may not exist yet when this OnSimulateStart runs.
        private bool registered;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Super Capacitor";
            BusMenu = AddMenu("Energy Bus", 3, new List<string> { "Armor", "Shield", "Weapon", "Universal" }, false);
            CapacityScale = AddSlider("Capacity", "BSCapacity", 600f, 100f, 5000f);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            Bus = (EnergyBus)Mathf.Clamp(BusMenu.Value, 0, 3);
            Capacity = CapacityScale.Value * Mathf.Max(0.2f, transform.lossyScale.magnitude);
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
