using Modding;
using Modding.Blocks;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class NanoArmorController : SingleInstance<NanoArmorController>
    {
        public override string Name { get { return "BeyondSpider Nano Armor Controller"; } }

        private void Awake()
        {
            Events.OnBlockInit += AddArmor;
        }

        private void AddArmor(Block block)
        {
            BlockBehaviour blockBehaviour = block.GameObject.GetComponent<BlockBehaviour>();
            if (blockBehaviour == null)
            {
                return;
            }

            switch (blockBehaviour.BlockID)
            {
                case (int)BlockType.SingleWoodenBlock:
                case (int)BlockType.DoubleWoodenBlock:
                case (int)BlockType.Log:
                    if (blockBehaviour.gameObject.GetComponent<NanoArmorBehaviour>() == null)
                    {
                        blockBehaviour.gameObject.AddComponent<NanoArmorBehaviour>();
                    }
                    break;
                default:
                    break;
            }
        }
    }

    internal static class NanoArmorAssets
    {
        private static GameObject singleVisPrefab;
        private static bool loadAttempted;

        public static GameObject SingleVisPrefab
        {
            get
            {
                if (singleVisPrefab == null && !loadAttempted)
                {
                    loadAttempted = true;
                    singleVisPrefab = ModResource.GetAssetBundle("space-armourvis").LoadAsset<GameObject>("SingleVis");
                }
                return singleVisPrefab;
            }
        }
    }

    public class NanoArmorBehaviour : MonoBehaviour, IKineticTarget
    {
        // HP pools, repair rate and repair energy all come from the shared balance model so this
        // block stays in lockstep with weapon damage and the energy exchange rates — see
        // SpaceBalance. IntegrityPool/StructuralPool define 1 Armor Unit; EnergyPerRepairPoint is
        // the Armor-bus MJ per integrity HP; RepairRatePerSecond is the fraction of the integrity
        // pool restored per second at full power.
        private const float IntegrityPool = SpaceBalance.ArmorIntegrityPool;
        private const float StructuralPool = SpaceBalance.ArmorStructuralPool;
        private const float RepairRatePerSecond = SpaceBalance.ArmorRepairFraction;
        // static readonly (not const): ArmorEnergyPerHP is macro-derived at load, not a compile-time
        // constant, so this alias can't be const. Pools/rate above stay const — the macro layer
        // leaves them fixed.
        private static readonly float EnergyPerRepairPoint = SpaceBalance.ArmorEnergyPerHP;
        private const float HealthyJointForce = 45000f;
        private const float MaxOverlayAlpha = 0.6f;
        private const float MinRepairSpeedFactor = SpaceBalance.ArmorMinRepairSpeedFactor;
        // A hit that fully clears the structural bar lands it at exactly 0 after Clamp01, but
        // float rounding on a budget==HP breach can leave a hair above; treat anything at/below
        // this as breached so a penetrating round always flags it (ADR-0007).
        private const float StructuralBreachEpsilon = 0.002f;

        public float Integrity = 1f;
        public float StructuralValue = 1f;

        private BlockBehaviour bb;
        private Rigidbody body;
        private int playerID;
        private MPTeam team;
        private ShipState ship;
        private GameObject overlay;
        private MeshRenderer overlayRenderer;
        private GameObject normalVis;
        private bool breached;

        public float LaserDamageMultiplier
        {
            get { return Mathf.Clamp01(0.05f + 0.95f * (1f - Integrity)); }
        }

        // Visual split for laser impacts (design brief: 命中完整装甲反射、不完整装甲火花四溅).
        // The reflected fraction is the complement of LaserDamageMultiplier, i.e. 0.95*Integrity,
        // so reflection stops dominating below Integrity ~0.53; the threshold sits slightly above
        // that so the impact flips to sparks just before the beam actually starts winning —
        // sparks are the player's cue that this coating is worth continuing to burn.
        private const float LaserMirrorIntegrityThreshold = 0.6f;

        public bool ReflectsLaser
        {
            get { return Integrity >= LaserMirrorIntegrityThreshold; }
        }

        public float LaserReflectivity
        {
            get { return Mathf.Clamp01(1f - LaserDamageMultiplier); }
        }

        // ---- IKineticTarget (ADR-0007) ----
        // Total structural HP standing between a round and passing through: the integrity layer
        // (spent first) plus the permanent structural layer.
        public float RemainingKineticHP
        {
            get { return Integrity * IntegrityPool + StructuralValue * StructuralPool; }
        }
        public Vector3 KineticVelocity
        {
            get { return body != null ? body.velocity : Vector3.zero; }
        }
        public bool CanRicochet { get { return true; } }
        public bool IsBreached { get { return breached; } }
        public void ApplyKineticDamage(float damage)
        {
            ApplyPhysicalDamage(damage);
        }

        private void Awake()
        {
            bb = GetComponent<BlockBehaviour>();
            body = GetComponent<Rigidbody>();
            playerID = bb.ParentMachine.PlayerID;
            team = bb.Team;
        }

        private void Start()
        {
            if (bb == null || !bb.isSimulating)
            {
                return;
            }
            // Membership comes from the tick-3 connectivity partition (ADR-0011): usually null
            // here (partition hasn't run yet at simulate start) — the FixedUpdate retry below
            // picks it up; the partition also calls AssignShip directly on every armor block it
            // finds in a ship's component.
            AssignShip(SpaceCombatRegistry.ShipOf(bb));
            InitVisual();
        }

        public void AssignShip(ShipState assigned)
        {
            if (assigned == null || ship == assigned)
            {
                return;
            }
            ship = assigned;
            SpaceCombatRegistry.RegisterSubsystem(playerID, this, ship.Armor);
        }

        private void OnDestroy()
        {
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Armor);
            }
        }

        private void FixedUpdate()
        {
            if (bb == null || !bb.isSimulating)
            {
                return;
            }

            if (ship == null)
            {
                AssignShip(SpaceCombatRegistry.ShipOf(bb));
            }

            bool hostAuthoritative = !StatMaster.isMP || !StatMaster.isClient;
            // Self-repair only while the structural layer is pristine: the first hit that spills
            // past integrity into structure permanently disables regen for this block (ADR-0007).
            // StructuralValue only ever decreases, so this latches off for good once it drops.
            if (hostAuthoritative && ship != null && Integrity < 1f && StructuralValue >= 1f)
            {
                float integrityFactor = Mathf.Lerp(MinRepairSpeedFactor, 1f, Integrity);
                float wantedRepair = RepairRatePerSecond * integrityFactor * Time.fixedDeltaTime;
                float energyNeeded = wantedRepair * IntegrityPool * EnergyPerRepairPoint;
                float ratio = ship.Energy.Request(EnergyBus.Armor, energyNeeded);
                Integrity = Mathf.Clamp01(Integrity + wantedRepair * ratio * ratio);
            }

            UpdateVisual();
        }

        // Laser damage is gated by how intact this block still is: a pristine nano coating
        // refracts most of it away (LaserDamageMultiplier ~0.05 at full Integrity), so lasers
        // barely scratch fresh armor and only bite once kinetic/blast hits have stripped
        // Integrity down — the intended firepower synergy from the design brief. The surviving
        // fraction then runs through the exact same two-stage Integrity->Structural pipeline as
        // a physical hit.
        public void ApplyLaserDamage(float damage)
        {
            if (damage <= 0f)
            {
                return;
            }

            ApplyPhysicalDamage(damage * LaserDamageMultiplier);
        }

        public void ApplyPhysicalDamage(float damage)
        {
            if (damage <= 0f)
            {
                return;
            }

            if (Integrity > 0f)
            {
                float integrityCapacity = Integrity * IntegrityPool;
                if (damage <= integrityCapacity)
                {
                    Integrity -= damage / IntegrityPool;
                    return;
                }

                damage -= integrityCapacity;
                Integrity = 0f;
            }

            StructuralValue = Mathf.Clamp01(StructuralValue - damage / StructuralPool);
            ApplyStructuralWeakening();
        }

        private void ApplyStructuralWeakening()
        {
            if (StructuralValue >= 1f || bb == null)
            {
                return;
            }

            float scaled = Mathf.Lerp(0f, HealthyJointForce, Mathf.Clamp01(StructuralValue));
            foreach (var joint in bb.iJointTo)
            {
                if (joint == null)
                {
                    continue;
                }
                joint.breakForce = scaled;
                joint.breakTorque = scaled;
            }
            foreach (var joint in bb.jointsToMe)
            {
                if (joint == null)
                {
                    continue;
                }
                joint.breakForce = scaled;
                joint.breakTorque = scaled;
            }

            if (!breached && StructuralValue <= StructuralBreachEpsilon)
            {
                // Both bars cleared: the block is breached — it breaks off from the hull AND
                // becomes permanently transparent to every round from now on (ADR-0007).
                breached = true;
                StructuralValue = 0f;
                if (body != null)
                {
                    body.AddExplosionForce(400f, transform.position, 3f);
                }
            }
        }

        private void InitVisual()
        {
            GameObject prefab = NanoArmorAssets.SingleVisPrefab;
            if (prefab == null)
            {
                return;
            }

            overlay = (GameObject)Instantiate(prefab, transform);
            overlay.name = "NanoArmorVis";
            overlay.transform.localRotation = Quaternion.identity;
            overlay.layer = 25;

            bool halfVisEnabled = false;
            Transform vis = transform.Find("Vis");
            Transform halfVis = vis == null ? null : vis.Find("HalfVis");
            if (halfVis != null)
            {
                MeshRenderer halfRenderer = halfVis.GetComponent<MeshRenderer>();
                halfVisEnabled = halfRenderer != null && halfRenderer.enabled;
            }

            switch (bb.BlockID)
            {
                case (int)BlockType.SingleWoodenBlock:
                    overlay.transform.localPosition = new Vector3(0f, 0f, 0.5f);
                    overlay.transform.localScale = new Vector3(0.8f, 0.8f, 1f);
                    break;
                case (int)BlockType.DoubleWoodenBlock:
                    if (halfVisEnabled)
                    {
                        overlay.transform.localPosition = new Vector3(0f, 0f, 0.5f);
                        overlay.transform.localScale = new Vector3(0.95f, 0.95f, 1f);
                    }
                    else
                    {
                        overlay.transform.localPosition = new Vector3(0f, 0f, 1f);
                        overlay.transform.localScale = new Vector3(0.95f, 0.95f, 2f);
                    }
                    break;
                case (int)BlockType.Log:
                    if (halfVisEnabled)
                    {
                        overlay.transform.localPosition = new Vector3(0f, 0f, 1f);
                        overlay.transform.localScale = new Vector3(0.95f, 0.95f, 2f);
                    }
                    else
                    {
                        overlay.transform.localPosition = new Vector3(0f, 0f, 1.5f);
                        overlay.transform.localScale = new Vector3(0.95f, 0.95f, 3f);
                    }
                    break;
                default:
                    overlay.transform.localPosition = Vector3.zero;
                    overlay.transform.localScale = Vector3.one;
                    break;
            }

            overlayRenderer = overlay.GetComponent<MeshRenderer>();
            normalVis = vis == null ? null : vis.gameObject;
            overlay.SetActive(false);
        }

        private void UpdateVisual()
        {
            if (overlay == null || overlayRenderer == null)
            {
                return;
            }

            bool show = SpaceCombatRuntime.ShowArmorHP;
            overlay.SetActive(show);
            if (normalVis != null)
            {
                normalVis.SetActive(!show);
            }

            if (!show)
            {
                return;
            }

            if (Integrity > 0f)
            {
                overlayRenderer.material.color = new Color(0f, 1f, 0f, MaxOverlayAlpha * Integrity);
            }
            else
            {
                overlayRenderer.material.color = new Color(1f, 0f, 0f, MaxOverlayAlpha * StructuralValue);
            }
        }
    }
}
