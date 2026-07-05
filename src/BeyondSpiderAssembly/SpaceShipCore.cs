using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class SpaceShipCore : SpaceBlock, ITrackable
    {
        public MSlider TotalEnergy;
        public MSlider ArmorShare;
        public MSlider ShieldShare;
        public MSlider WeaponShare;

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
            TotalEnergy = AddSlider("Total Energy", "BSTotalEnergy", 1200f, 200f, 8000f);
            ArmorShare = AddSlider("Armor Share", "BSArmorShare", 1f, 0f, 5f);
            ShieldShare = AddSlider("Shield Share", "BSShieldShare", 2f, 0f, 5f);
            WeaponShare = AddSlider("Weapon Share", "BSWeaponShare", 2f, 0f, 5f);
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
            grid.Configure(TotalEnergy.Value, ArmorShare.Value, ShieldShare.Value, WeaponShare.Value);
        }

        private void ApplyReactorMass()
        {
            if (Body == null)
            {
                return;
            }
            Body.mass = Mathf.Max(0.3f, 0.3f + Mathf.Pow(TotalEnergy.Value / 1000f, 1.25f) * 0.8f);
        }
    }
}
