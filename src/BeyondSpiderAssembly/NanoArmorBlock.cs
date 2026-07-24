using System.Collections.Generic;
using Modding;
using Modding.Blocks;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // MP replication of armor pools (dirty-flag batches, the WW2-Naval CrewUpdateMessage shape):
    // damage/repair only ever mutate armor on the host (NanoArmorBehaviour.ApplyPhysicalDamage is
    // fenced), so the host batches changed blocks' Integrity/Structural every 0.2 s and clients
    // assign the values directly — overlay colors, info-panel aggregates, and the client rounds'
    // breach pass-through all follow. Batches (not per-hit events) because hits are bursty (one
    // blast dirties dozens of blocks in a tick) and repair re-dirties every damaged block every
    // tick; IntegerArray/SingleArray payloads make a batch one message.
    public class NanoArmorNet : SingleInstance<NanoArmorNet>
    {
        public override string Name { get { return "BeyondSpider Nano Armor Net"; } }

        // (playerId, guidHashes[], interleaved [integrity0, structural0, integrity1, structural1, …])
        public static MessageType BatchMsg = ModNetworking.CreateMessageType(
            DataType.Integer, DataType.IntegerArray, DataType.SingleArray);

        private const float FlushInterval = 0.2f;
        // Loss insurance: every touched block is re-sent on this cadence even when unchanged, so a
        // dropped batch corrects itself (state is absolute, resends are idempotent).
        private const float FullSweepInterval = 5f;
        private const int MaxBlocksPerMessage = 60;

        private readonly Dictionary<string, NanoArmorBehaviour> armors = new Dictionary<string, NanoArmorBehaviour>();
        private readonly HashSet<NanoArmorBehaviour> dirty = new HashSet<NanoArmorBehaviour>();
        private readonly Dictionary<int, List<NanoArmorBehaviour>> flushGroups = new Dictionary<int, List<NanoArmorBehaviour>>();
        private float nextFlush;
        private float nextFullSweep;

        public void Register(NanoArmorBehaviour armor)
        {
            armors[Key(armor.PlayerID, armor.GuidHash)] = armor;
        }

        public void Unregister(NanoArmorBehaviour armor)
        {
            string key = Key(armor.PlayerID, armor.GuidHash);
            NanoArmorBehaviour current;
            if (armors.TryGetValue(key, out current) && ReferenceEquals(current, armor))
            {
                armors.Remove(key);
            }
            dirty.Remove(armor);
        }

        public void MarkDirty(NanoArmorBehaviour armor)
        {
            if (armor != null)
            {
                dirty.Add(armor);
                armor.everTouched = true;
            }
        }

        private void FixedUpdate()
        {
            if (!StatMaster.isMP || !NetAuthority.IsAuthority || !StatMaster.levelSimulating)
            {
                return;
            }

            if (Time.time >= nextFullSweep)
            {
                nextFullSweep = Time.time + FullSweepInterval;
                foreach (KeyValuePair<string, NanoArmorBehaviour> pair in armors)
                {
                    NanoArmorBehaviour armor = pair.Value;
                    if (armor != null && armor.everTouched)
                    {
                        armor.forceSync = true;
                        dirty.Add(armor);
                    }
                }
            }

            if (Time.time < nextFlush || dirty.Count == 0)
            {
                return;
            }
            nextFlush = Time.time + FlushInterval;
            Flush();
        }

        private void Flush()
        {
            foreach (KeyValuePair<int, List<NanoArmorBehaviour>> group in flushGroups)
            {
                group.Value.Clear();
            }

            foreach (NanoArmorBehaviour armor in dirty)
            {
                // The tiny epsilon only filters exact no-ops (e.g. a sweep on an already-synced
                // block); real damage and even trickling repair always pass — the flush cadence is
                // the throttle, not the delta.
                if (armor == null || (!armor.forceSync
                    && Mathf.Abs(armor.Integrity - armor.lastSentIntegrity) < 0.0001f
                    && Mathf.Abs(armor.StructuralValue - armor.lastSentStructural) < 0.0001f))
                {
                    continue;
                }
                List<NanoArmorBehaviour> list;
                if (!flushGroups.TryGetValue(armor.PlayerID, out list))
                {
                    list = new List<NanoArmorBehaviour>();
                    flushGroups.Add(armor.PlayerID, list);
                }
                list.Add(armor);
            }
            dirty.Clear();

            foreach (KeyValuePair<int, List<NanoArmorBehaviour>> group in flushGroups)
            {
                List<NanoArmorBehaviour> list = group.Value;
                int index = 0;
                while (index < list.Count)
                {
                    int count = Mathf.Min(MaxBlocksPerMessage, list.Count - index);
                    int[] guids = new int[count];
                    float[] values = new float[count * 2];
                    for (int i = 0; i < count; i++)
                    {
                        NanoArmorBehaviour armor = list[index + i];
                        guids[i] = armor.GuidHash;
                        values[i * 2] = armor.Integrity;
                        values[i * 2 + 1] = armor.StructuralValue;
                        armor.lastSentIntegrity = armor.Integrity;
                        armor.lastSentStructural = armor.StructuralValue;
                        armor.forceSync = false;
                    }
                    ModNetworking.SendToAll(BatchMsg.CreateMessage(group.Key, guids, values));
                    index += count;
                }
            }
        }

        public void BatchReceiver(Message msg)
        {
            int playerId = (int)msg.GetData(0);
            int[] guids = (int[])msg.GetData(1);
            float[] values = (float[])msg.GetData(2);
            if (guids == null || values == null || values.Length < guids.Length * 2)
            {
                return;
            }
            for (int i = 0; i < guids.Length; i++)
            {
                NanoArmorBehaviour armor;
                if (armors.TryGetValue(Key(playerId, guids[i]), out armor) && armor != null)
                {
                    armor.ReceiveNetworkState(values[i * 2], values[i * 2 + 1]);
                }
            }
        }

        private static string Key(int playerId, int guid)
        {
            return playerId + ":" + guid;
        }
    }

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

        // Net identity + host-side sync bookkeeping (owned by NanoArmorNet's flush loop).
        public int PlayerID { get { return playerID; } }
        public int GuidHash { get; private set; }
        internal float lastSentIntegrity = 1f;
        internal float lastSentStructural = 1f;
        internal bool everTouched;
        internal bool forceSync;

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
            GuidHash = bb.BuildingBlock.Guid.GetHashCode();
            NanoArmorNet.Instance.Register(this);
            InitVisual();
            ColliderOptimize();
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
            NanoArmorNet.Instance.Unregister(this);
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

            // Self-repair only while the structural layer is pristine: the first hit that spills
            // past integrity into structure permanently disables regen for this block (ADR-0007).
            // StructuralValue only ever decreases, so this latches off for good once it drops.
            if (NetAuthority.IsAuthority && ship != null && Integrity < 1f && StructuralValue >= 1f)
            {
                float integrityFactor = Mathf.Lerp(MinRepairSpeedFactor, 1f, Integrity);
                float wantedRepair = RepairRatePerSecond * integrityFactor * Time.fixedDeltaTime;
                float energyNeeded = wantedRepair * IntegrityPool * EnergyPerRepairPoint;
                float ratio = ship.Energy.Request(EnergyBus.Armor, energyNeeded);
                Integrity = Mathf.Clamp01(Integrity + wantedRepair * ratio * ratio);
                if (StatMaster.isMP)
                {
                    NanoArmorNet.Instance.MarkDirty(this);
                }
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
            // Single choke point for the MP damage rule: every armor damage path (kinetic, laser,
            // blast) funnels through here, and structural weakening / breach / joint changes have
            // no other caller — so this one fence keeps clients from ever mutating armor state.
            // Client armor pools are driven by the host's NanoArmorNet batches instead.
            if (!NetAuthority.IsAuthority || damage <= 0f)
            {
                return;
            }

            if (StatMaster.isMP)
            {
                NanoArmorNet.Instance.MarkDirty(this);
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

        // Host BatchMsg landed on this client replica: assign the pools directly — no
        // ApplyStructuralWeakening, no joint changes, no breach explosion force. The physical
        // consequences (joints snapping, the plate flying off) reach clients through vanilla
        // machine sync; this only keeps the overlay/info-panel/breach-transparency state honest.
        public void ReceiveNetworkState(float integrity, float structural)
        {
            Integrity = Mathf.Clamp01(integrity);
            StructuralValue = Mathf.Clamp01(structural);
            breached = StructuralValue <= StructuralBreachEpsilon;
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

        // Wood/log blocks carry more than one BoxCollider in their child hierarchy (one per
        // visual segment); Besiege welds a separate physical joint wherever a neighboring
        // block's collider touches one of them, so leaving every one of them enabled lets a
        // single oversized brace spanning the block weld several redundant joints to it at
        // once. Collapsing down to one collider, resized to the block's full occupied volume,
        // is WW2-Naval's WoodenArmour.ColliderOptimize -- same fix, same stock block types
        // (this mod's hull uses the identical SingleWoodenBlock/DoubleWoodenBlock/Log prefabs),
        // ported here rather than the joint-level cleanup tried earlier.
        private void ColliderOptimize()
        {
            Transform joint1 = transform.Find("Joint1");
            BoxCollider primary = joint1 == null ? null : joint1.GetComponent<BoxCollider>();
            if (primary == null)
            {
                return;
            }

            BoxCollider[] colliders = GetComponentsInChildren<BoxCollider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                colliders[i].enabled = false;
            }
            primary.enabled = true;

            // Same half/full read InitVisual already uses for this block (Vis/HalfVis renderer
            // state), so the collider always matches whichever length variant is showing.
            Transform vis = transform.Find("Vis");
            Transform halfVis = vis == null ? null : vis.Find("HalfVis");
            MeshRenderer halfRenderer = halfVis == null ? null : halfVis.GetComponent<MeshRenderer>();
            bool half = halfRenderer != null && halfRenderer.enabled;

            switch (bb.BlockID)
            {
                case (int)BlockType.SingleWoodenBlock:
                    primary.center = new Vector3(0f, 0f, 0f);
                    primary.size = new Vector3(0.8f, 0.8f, 1f);
                    break;
                case (int)BlockType.DoubleWoodenBlock:
                    if (half)
                    {
                        primary.center = new Vector3(0f, 0f, 0f);
                        primary.size = new Vector3(0.95f, 0.95f, 1f);
                    }
                    else
                    {
                        primary.center = new Vector3(0f, 0f, 0.5f);
                        primary.size = new Vector3(0.95f, 0.95f, 2f);
                    }
                    break;
                case (int)BlockType.Log:
                    if (half)
                    {
                        primary.center = new Vector3(0f, 0f, 0.5f);
                        primary.size = new Vector3(0.95f, 0.95f, 2f);
                    }
                    else
                    {
                        primary.center = new Vector3(0f, 0f, 1f);
                        primary.size = new Vector3(0.95f, 0.95f, 3f);
                    }
                    break;
                default:
                    break;
            }
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
