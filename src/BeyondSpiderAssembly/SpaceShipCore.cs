using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class SpaceShipCore : SpaceBlock, ILockable
    {
        public MInfo TotalPowerInfo;
        public MSlider ArmorPowerShare;
        public MSlider ShieldPowerShare;
        public MSlider WeaponPowerShare;
        // Propulsion share (ADR-0018). Defaults to 1 — a ship with no thrusters simply leaves this
        // bank uncharged, and one with thrusters starts modest so the guns aren't starved by
        // default. Appended last so existing machines keep their A/S/W settings.
        public MSlider ThrustPowerShare;

        private const float MinScaleComponent = 0.05f;
        // Total reactor output used to be a player-set MSlider; now it's computed directly from the
        // ship core block's own build-mode volume, same idea as RailgunBarrelBlock's caliber/bore
        // length/muzzle velocity — see agent-besiege-mod-guide.md. The per-volume coefficient lives
        // in SpaceBalance.ReactorPowerPerVolume so the 能量充沛度 macro dial scales it (MacroBalance).

        private float totalPower;
        private bool detonated;

        public string DisplayName;

        public int GuidHash { get; private set; }

        public TrackKind Kind { get { return TrackKind.Ship; } }
        public Vector3 Position { get { return transform.position; } }
        public Vector3 Velocity { get { return Body == null ? Vector3.zero : Body.velocity; } }
        public float Radius { get { return Mathf.Max(4f, transform.lossyScale.magnitude * 8f); } }
        public bool IsAlive { get { return !detonated && BlockBehaviour != null && BlockBehaviour.isSimulating; } }

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Ship Core";
            TotalPowerInfo = AddInfo("Total Power", "BSTotalEnergyInfo");
            ArmorPowerShare = AddSlider("Armor Power Share", "BSArmorShare", 1f, 0f, 5f);
            ShieldPowerShare = AddSlider("Shield Power Share", "BSShieldShare", 2f, 0f, 5f);
            WeaponPowerShare = AddSlider("Weapon Power Share", "BSWeaponShare", 2f, 0f, 5f);
            ThrustPowerShare = AddSlider("Thrust Power Share", "BSThrustShare", 1f, 0f, 5f);
            RecomputeReactorOutput();
        }

        private void RecomputeReactorOutput()
        {
            Vector3 scale = transform.localScale;
            float x = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.x));
            float y = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.y));
            float z = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.z));
            totalPower = SpaceBalance.ReactorPowerPerVolume * x * y * z;
            TotalPowerInfo.Set(totalPower.ToString("F0") + " MW");
        }

        public override void BuildingUpdate()
        {
            base.BuildingUpdate();
            RecomputeReactorOutput();
        }

        public override void SimulateFixedUpdateAlways()
        {
            base.SimulateFixedUpdateAlways();
            RecomputeReactorOutput();
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            RecomputeReactorOutput();
            GuidHash = BlockBehaviour.BuildingBlock.Guid.GetHashCode();
            DisplayName = "P" + PlayerID + " Core";
            SpaceCombatRegistry.RegisterCore(this);
            ApplyReactorMass();
        }

        public override void OnSimulateStop()
        {
            SpaceCombatRegistry.UnregisterCore(this);
        }

        // Read by SpaceCombatRuntime, which sums every core in the ship's component (合并出力)
        // before Configure()-ing the grid with the PRIMARY core's share sliders.
        public float CurrentTotalPower
        {
            get { return totalPower; }
        }

        private void ApplyReactorMass()
        {
            if (Body == null)
            {
                return;
            }
            Body.mass = Mathf.Max(0.3f, 0.3f + Mathf.Pow(totalPower / 1000f, 1.25f) * 0.8f);
        }

        // Reactor detonation (damage-refinement spec): a direct hit, or a missile blast with clear
        // line of sight to this core (SubsystemDetonation), blows the whole reactor — a
        // catastrophic secondary explosion far beyond ordinary blast damage. Mirrors
        // NanoArmorBehaviour's breach mechanic (own joints zeroed + explosion force so physics
        // tears it free) rather than destroying the block outright, so a detonated core becomes
        // drifting debris exactly like breached armor instead of vanishing. IsAlive going false
        // immediately drops it out of every power sum (SpaceCombatRuntime, CurrentTotalPower
        // callers) without any registry bookkeeping here.
        public void Detonate()
        {
            if (detonated || !NetAuthority.IsAuthority)
            {
                return;
            }
            detonated = true;
            // Diagnostic (MP playtest: "ship energy dead" traced to subsystem detonations — a dead
            // core silently skips the ship's whole energy tick, reading as 0%/0% forever).
            Debug.Log("[BeyondSpider] reactor DETONATED on player " + PlayerID + " ship (power "
                + totalPower.ToString("F0") + ") at " + transform.position);

            Vector3 position = transform.position;
            Vector3 driftVelocity = Body != null ? Body.velocity : Vector3.zero;

            if (StatMaster.isMP)
            {
                ModNetworking.SendToAll(SubsystemDetonationNet.DetonateMsg.CreateMessage(
                    position, driftVelocity, totalPower, 0));
            }

            SpaceEffectAssets.PlayReactorExplosion(position, driftVelocity, totalPower);
            BreakOwnConnectionJoints();
            SubsystemDetonation.ApplySecondaryBlast(position,
                SpaceBalance.ReactorBlastRadius(totalPower),
                SpaceBalance.ReactorBlastDamage(totalPower),
                SpaceBalance.ReactorBlastForce(totalPower));
        }
    }
}
