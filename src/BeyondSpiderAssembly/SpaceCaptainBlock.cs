using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class SpaceCaptainBlock : SpaceBlock
    {
        public MKey SwitchPriority;
        public MToggle DefaultAntiAir;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Captain";
            SwitchPriority = AddKey("Switch Priority", "BSCaptainSwitchPriority", KeyCode.T);
            DefaultAntiAir = AddToggle("Default Anti-Air", "BSCaptainDefaultAntiAir", true);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            ShipState ship = OwnShip();
            if (ship != null)
            {
                ship.Captain = this;
                ship.Priority = DefaultAntiAir.IsActive ? CommandPriority.AntiAir : CommandPriority.AntiShip;
            }
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null && ship.Captain == this)
            {
                ship.Captain = null;
            }
        }

        public override void SimulateUpdateHost()
        {
            ShipState ship = OwnShip();
            if (ship == null)
            {
                return;
            }

            if (SwitchPriority.IsPressed)
            {
                ship.Priority = ship.Priority == CommandPriority.AntiAir
                    ? CommandPriority.AntiShip
                    : CommandPriority.AntiAir;
            }
        }
    }
}
