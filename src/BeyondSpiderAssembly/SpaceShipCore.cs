using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class SpaceShipCore : SpaceBlock, ITrackable
    {
        public MSlider TotalPower;
        public MSlider ArmorPowerShare;
        public MSlider ShieldPowerShare;
        public MSlider WeaponPowerShare;


        public string DisplayName;

        public TrackKind Kind { get { return TrackKind.Ship; } }
        public Vector3 Position { get { return transform.position; } }
        public Vector3 Velocity { get { return Body == null ? Vector3.zero : Body.velocity; } }
        public float Radius { get { return Mathf.Max(4f, transform.lossyScale.magnitude * 8f); } }
        public bool IsAlive { get { return BlockBehaviour != null && BlockBehaviour.isSimulating; } }

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Ship Core";
            TotalPower = AddSlider("Total Power", "BSTotalEnergy", 1200f, 200f, 8000f);
            ArmorPowerShare = AddSlider("Armor Power Share", "BSArmorShare", 1f, 0f, 5f);
            ShieldPowerShare = AddSlider("Shield Power Share", "BSShieldShare", 2f, 0f, 5f);
            WeaponPowerShare = AddSlider("Weapon Power Share", "BSWeaponShare", 2f, 0f, 5f);

        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            DisplayName = "P" + PlayerID + " Core";
            SpaceCombatRegistry.RegisterCore(this);
            ApplyReactorMass();
        }

        public override void OnSimulateStop()
        {
            SpaceCombatRegistry.UnregisterCore(this);
        }

        public void ConfigureEnergy(EnergyGrid grid)
        {
            grid.Configure(TotalPower.Value, ArmorPowerShare.Value, ShieldPowerShare.Value, WeaponPowerShare.Value);
        }

        private void ApplyReactorMass()
        {
            if (Body == null)
            {
                return;
            }
            Body.mass = Mathf.Max(0.3f, 0.3f + Mathf.Pow(TotalPower.Value / 1000f, 1.25f) * 0.8f);
        }
    }
}
