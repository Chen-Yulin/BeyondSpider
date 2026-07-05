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

    public class NanoArmorBehaviour : MonoBehaviour
    {
        private const float IntegrityPool = 420f;
        private const float StructuralPool = 620f;
        private const float RepairRatePerSecond = 0.05f;
        private const float EnergyPerRepairPoint = 4f;
        private const float HealthyJointForce = 45000f;
        private const float MaxOverlayAlpha = 0.6f;

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

        public float LaserDamageMultiplier
        {
            get { return Mathf.Clamp01(0.05f + 0.95f * (1f - Integrity)); }
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
            ship = SpaceCombatRegistry.FindShip(playerID);
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(playerID, this, ship.Armor);
            }
            InitVisual();
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

            bool hostAuthoritative = !StatMaster.isMP || !StatMaster.isClient;
            if (hostAuthoritative && ship != null && Integrity < 1f)
            {
                float wantedRepair = RepairRatePerSecond * Time.fixedDeltaTime;
                float energyNeeded = wantedRepair * IntegrityPool * EnergyPerRepairPoint;
                float ratio = ship.Energy.Request(EnergyBus.Armor, energyNeeded);
                Integrity = Mathf.Clamp01(Integrity + wantedRepair * ratio * ratio);
            }

            UpdateVisual();
        }

        public void ApplyPhysicalDamage(float damage)
        {
            if (damage <= 0f)
            {
                return;
            }
            if (Integrity > 0f)
            {
                Integrity = Mathf.Clamp01(Integrity - damage / IntegrityPool);
                return;
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

            if (StructuralValue <= 0f && body != null)
            {
                body.AddExplosionForce(400f, transform.position, 3f);
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

            bool show = ship != null && ship.Core != null && ship.Core.ShowArmorHP.IsActive;
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
