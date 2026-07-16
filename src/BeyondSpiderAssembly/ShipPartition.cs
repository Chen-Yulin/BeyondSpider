using System;
using System.Collections.Generic;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // Multi-ship partition (ADR-0011): one player may field several ships in one machine. A ship
    // is a connectivity component of the machine's block graph that contains at least one ship
    // core; membership is decided once, on the 3rd FixedUpdate after the machine starts
    // simulating, and stays fixed for the whole simulation (a hull chunk blown off later still
    // counts as part of its original ship).
    //
    // The block graph is an undirected graph WITH CYCLES (braces, closed hulls), so this is a
    // visited-set BFS over connectivity components, not a literal tree walk. Two edge sources,
    // both verified against the game's Assembly-CSharp:
    //   * rigid: Machine.simClusters — blocks welded into one SimCluster share a rigidbody, all
    //     of them are pairwise same-ship;
    //   * articulated: BlockBehaviour.BlockJoints[].connectedBlock (structural joint table) plus
    //     the physical Joint lists iJointTo/jointsToMe, so hinge turrets, steering-wheel mounts,
    //     ropes and springs all count as "connected" (绳索连接算一艘, per design decision).
    //
    // The partition runs locally on host and clients alike; determinism across machines comes
    // from sorting cores/captains by BuildingBlock.Guid, so every machine derives the same
    // primary core — whose GuidHash is the ship's cross-client identity in CaptainLockNet.
    public static class ShipPartition
    {
        // "第3帧": run the partition on the 3rd FixedUpdate after the machine's cores start
        // simulating — late enough for simClusters and all joints to exist.
        private const int PartitionDelayTicks = 3;
        // Safety bound for the late-resolve BFS; real machines are a few hundred blocks.
        private const int LateResolveVisitBudget = 4096;
        // A late resolve that found no ship retries this often — a block placed mid-simulation
        // may not have its joints wired yet on the very tick its OnSimulateStart runs, so a
        // failure must not be cached forever.
        private const float LateResolveRetrySeconds = 0.5f;

        private static readonly Dictionary<Machine, int> pendingMachines = new Dictionary<Machine, int>();
        private static readonly Dictionary<BlockBehaviour, float> lateRetryAt = new Dictionary<BlockBehaviour, float>();

        // Shared scratch buffers — partition and late-resolve are one-shot walks, never re-entrant.
        private static readonly List<Machine> machineBuffer = new List<Machine>();
        private static readonly List<BlockBehaviour> neighborBuffer = new List<BlockBehaviour>();
        private static readonly HashSet<BlockBehaviour> visited = new HashSet<BlockBehaviour>();
        private static readonly Queue<BlockBehaviour> frontier = new Queue<BlockBehaviour>();
        private static readonly List<BlockBehaviour> componentBuffer = new List<BlockBehaviour>();
        private static readonly List<SpaceShipCore> coreBuffer = new List<SpaceShipCore>();
        private static readonly List<SpaceCaptainBlock> captainBuffer = new List<SpaceCaptainBlock>();

        public static void QueueMachine(Machine machine)
        {
            if (machine != null && !pendingMachines.ContainsKey(machine))
            {
                pendingMachines.Add(machine, PartitionDelayTicks);
            }
        }

        public static bool IsPending(Machine machine)
        {
            return machine != null && pendingMachines.ContainsKey(machine);
        }

        public static void Reset()
        {
            pendingMachines.Clear();
            lateRetryAt.Clear();
        }

        // Pumped by SpaceCombatRuntime.FixedUpdate on every machine (host and clients).
        public static void Tick()
        {
            if (pendingMachines.Count == 0)
            {
                return;
            }

            machineBuffer.Clear();
            foreach (KeyValuePair<Machine, int> pair in pendingMachines)
            {
                machineBuffer.Add(pair.Key);
            }

            for (int i = 0; i < machineBuffer.Count; i++)
            {
                Machine machine = machineBuffer[i];
                if (machine == null || !machine.isSimulating)
                {
                    pendingMachines.Remove(machine);
                    continue;
                }

                int remaining = pendingMachines[machine] - 1;
                if (remaining > 0)
                {
                    pendingMachines[machine] = remaining;
                    continue;
                }

                pendingMachines.Remove(machine);
                try
                {
                    Partition(machine);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("BeyondSpider: ship partition failed for machine of player "
                        + machine.PlayerID + ": " + ex);
                }
            }
        }

        private static void Partition(Machine machine)
        {
            List<BlockBehaviour> blocks = machine.SimulationBlocks;
            if (blocks == null || blocks.Count == 0)
            {
                return;
            }

            // Repartition safety: forget everything previously derived for this machine.
            SpaceCombatRegistry.DropMachine(machine);

            coreBuffer.Clear();
            for (int i = 0; i < blocks.Count; i++)
            {
                BlockBehaviour block = blocks[i];
                if (block == null)
                {
                    continue;
                }
                SpaceShipCore core = block.GetComponent<SpaceShipCore>();
                if (core != null)
                {
                    coreBuffer.Add(core);
                }
            }
            // Deterministic seed order (largest reactor first, Guid tie-break) so every machine in
            // MP walks components in the same order and derives the same primary core per ship.
            coreBuffer.Sort(CompareCores);

            visited.Clear();
            for (int i = 0; i < coreBuffer.Count; i++)
            {
                BlockBehaviour seed = coreBuffer[i].BlockBehaviour;
                if (seed == null || visited.Contains(seed))
                {
                    continue; // already swallowed by an earlier core's component (合并出力)
                }
                CollectComponent(seed);
                BuildShip(machine, componentBuffer);
            }

            // Blocks no core can reach belong to no ship. Map them explicitly so every later
            // OwnShip() lookup on them is a single dictionary hit instead of a re-walk.
            for (int i = 0; i < blocks.Count; i++)
            {
                BlockBehaviour block = blocks[i];
                if (block != null && !visited.Contains(block))
                {
                    SpaceCombatRegistry.MapBlock(block, null);
                }
            }
            visited.Clear();
        }

        // BFS from seed into componentBuffer, marking the shared `visited` set (which persists
        // across the per-core loop in Partition so components never overlap).
        private static void CollectComponent(BlockBehaviour seed)
        {
            componentBuffer.Clear();
            frontier.Clear();
            frontier.Enqueue(seed);
            visited.Add(seed);
            while (frontier.Count > 0)
            {
                BlockBehaviour block = frontier.Dequeue();
                componentBuffer.Add(block);
                NeighborsOf(block, neighborBuffer);
                for (int i = 0; i < neighborBuffer.Count; i++)
                {
                    BlockBehaviour neighbor = neighborBuffer[i];
                    if (neighbor != null && !visited.Contains(neighbor))
                    {
                        visited.Add(neighbor);
                        frontier.Enqueue(neighbor);
                    }
                }
            }
        }

        // All blocks directly connected to `block`, from every connectivity source at once:
        // structural BlockJoints (both directions come out of symmetrized traversal since each
        // side's own table lists the other), physical joints in both directions, and the whole
        // SimCluster the block is welded into.
        private static void NeighborsOf(BlockBehaviour block, List<BlockBehaviour> buffer)
        {
            buffer.Clear();

            BlockJoint[] structural = block.BlockJoints;
            if (structural != null)
            {
                for (int i = 0; i < structural.Length; i++)
                {
                    if (structural[i] != null && structural[i].connectedBlock != null)
                    {
                        buffer.Add(structural[i].connectedBlock);
                    }
                }
            }

            List<Joint> outgoing = block.iJointTo;
            if (outgoing != null)
            {
                for (int i = 0; i < outgoing.Count; i++)
                {
                    Joint joint = outgoing[i];
                    if (joint != null && joint.connectedBody != null)
                    {
                        BlockBehaviour other = joint.connectedBody.GetComponent<BlockBehaviour>();
                        if (other != null)
                        {
                            buffer.Add(other);
                        }
                    }
                }
            }

            List<Joint> incoming = block.jointsToMe;
            if (incoming != null)
            {
                for (int i = 0; i < incoming.Count; i++)
                {
                    Joint joint = incoming[i];
                    if (joint != null)
                    {
                        BlockBehaviour other = joint.GetComponent<BlockBehaviour>();
                        if (other != null)
                        {
                            buffer.Add(other);
                        }
                    }
                }
            }

            Machine machine = block.ParentMachine;
            Machine.SimCluster[] clusters = machine != null ? machine.simClusters : null;
            if (clusters != null)
            {
                int index = block.ClusterIndex;
                if (index >= 0 && index < clusters.Length && clusters[index] != null)
                {
                    BlockBehaviour[] members = clusters[index].Blocks;
                    if (members != null)
                    {
                        for (int i = 0; i < members.Length; i++)
                        {
                            if (members[i] != null && !ReferenceEquals(members[i], block))
                            {
                                buffer.Add(members[i]);
                            }
                        }
                    }
                }
            }
        }

        private static void BuildShip(Machine machine, List<BlockBehaviour> component)
        {
            coreBufferLocal.Clear();
            captainBuffer.Clear();
            for (int i = 0; i < component.Count; i++)
            {
                SpaceShipCore core = component[i].GetComponent<SpaceShipCore>();
                if (core != null)
                {
                    coreBufferLocal.Add(core);
                }
                SpaceCaptainBlock captain = component[i].GetComponent<SpaceCaptainBlock>();
                if (captain != null)
                {
                    captainBuffer.Add(captain);
                }
            }
            if (coreBufferLocal.Count == 0)
            {
                return; // unreachable: components are only ever seeded from cores
            }
            coreBufferLocal.Sort(CompareCores);
            captainBuffer.Sort(CompareCaptains);

            SpaceShipCore primary = coreBufferLocal[0];
            ShipState ship = new ShipState();
            ship.Machine = machine;
            ship.PlayerID = primary.PlayerID;
            ship.Core = primary;
            ship.CoreGuidHash = primary.GuidHash;
            ship.Cores.AddRange(coreBufferLocal);
            ship.Captain = captainBuffer.Count > 0 ? captainBuffer[0] : null;
            ship.Name = ResolveShipName(ship);
            SpaceCombatRegistry.AddShip(ship);

            // Extra cores merge their output into this ship's grid (合并出力) but must not show up
            // as separate radar blips — one vessel, one trackable identity (the primary core).
            for (int i = 1; i < coreBufferLocal.Count; i++)
            {
                SpaceCombatRegistry.UnregisterTrackable(coreBufferLocal[i]);
            }

            for (int i = 0; i < component.Count; i++)
            {
                BlockBehaviour block = component[i];
                SpaceCombatRegistry.MapBlock(block, ship);

                // Central subsystem slotting: this runs on clients too, unlike the blocks' own
                // host-gated per-tick registration retries (which stay, as self-healing for blocks
                // placed onto an already-simulating ship).
                SpaceBlock spaceBlock = block.GetComponent<SpaceBlock>();
                if (spaceBlock != null)
                {
                    spaceBlock.OnAssignedToShip(ship);
                }
                NanoArmorBehaviour armor = block.GetComponent<NanoArmorBehaviour>();
                if (armor != null)
                {
                    armor.AssignShip(ship);
                }
            }

            ComputeHullBounds(ship, component);
        }

        // Ship display name: the (deterministically first) captain's Ship Name text when set,
        // otherwise a per-player ordinal fallback.
        private static string ResolveShipName(ShipState ship)
        {
            if (ship.Captain != null)
            {
                string named = ship.Captain.ShipDisplayName;
                if (named.Length > 0)
                {
                    return named;
                }
            }
            int ordinal = 1;
            foreach (ShipState other in SpaceCombatRegistry.Ships)
            {
                if (other != ship && other.PlayerID == ship.PlayerID)
                {
                    ordinal++;
                }
            }
            return "SHIP " + ordinal;
        }

        // Core-local OBB (design decision: 核心本地系, orientation-only — deliberately NOT
        // InverseTransformPoint, which would also divide by the core's build scale and skew the
        // measured meters). Computed once here; the ship's pose never changes it afterwards.
        private static void ComputeHullBounds(ShipState ship, List<BlockBehaviour> component)
        {
            Transform coreTransform = ship.Core.transform;
            Quaternion toLocal = Quaternion.Inverse(coreTransform.rotation);
            Vector3 origin = coreTransform.position;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;

            for (int i = 0; i < component.Count; i++)
            {
                Collider[] colliders = component[i].GetComponentsInChildren<Collider>();
                for (int c = 0; c < colliders.Length; c++)
                {
                    Collider collider = colliders[c];
                    if (collider == null || collider.isTrigger || !collider.enabled)
                    {
                        continue;
                    }
                    Bounds bounds = collider.bounds;
                    Vector3 extents = bounds.extents;
                    Vector3 center = bounds.center;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vector3 world = center + new Vector3(
                            (corner & 1) == 0 ? -extents.x : extents.x,
                            (corner & 2) == 0 ? -extents.y : extents.y,
                            (corner & 4) == 0 ? -extents.z : extents.z);
                        Vector3 local = toLocal * (world - origin);
                        min = Vector3.Min(min, local);
                        max = Vector3.Max(max, local);
                        any = true;
                    }
                }
            }

            if (!any)
            {
                ship.HullSize = Vector3.zero;
                ship.HullVolume = 0f;
                return;
            }
            ship.HullSize = max - min;
            ship.HullVolume = ship.HullSize.x * ship.HullSize.y * ship.HullSize.z;
        }

        // Resolves ship membership for a block that wasn't part of any tick-3 partition — i.e. a
        // block placed onto an already-simulating machine. Walks outward until it reaches a block
        // with a known ship. A miss is retried (joints may not be wired yet on the placement
        // tick) instead of cached, but only every LateResolveRetrySeconds so shipless debris
        // doesn't re-walk its component on every OwnShip() call.
        public static ShipState LateResolve(BlockBehaviour block)
        {
            Machine machine = block.ParentMachine;
            if (machine == null || IsPending(machine))
            {
                return null; // the real partition hasn't run yet — don't derive (or cache) anything
            }

            float retryAt;
            if (lateRetryAt.TryGetValue(block, out retryAt) && Time.time < retryAt)
            {
                return null;
            }

            visited.Clear();
            frontier.Clear();
            componentBuffer.Clear();
            frontier.Enqueue(block);
            visited.Add(block);
            ShipState found = null;
            while (frontier.Count > 0 && componentBuffer.Count < LateResolveVisitBudget)
            {
                BlockBehaviour current = frontier.Dequeue();
                ShipState mapped;
                if (SpaceCombatRegistry.TryGetMappedShip(current, out mapped))
                {
                    if (mapped != null)
                    {
                        found = mapped;
                        break;
                    }
                    continue; // known shipless region — terminal, don't expand through it
                }
                componentBuffer.Add(current);
                NeighborsOf(current, neighborBuffer);
                for (int i = 0; i < neighborBuffer.Count; i++)
                {
                    BlockBehaviour neighbor = neighborBuffer[i];
                    if (neighbor != null && neighbor.isSimulating && !visited.Contains(neighbor))
                    {
                        visited.Add(neighbor);
                        frontier.Enqueue(neighbor);
                    }
                }
            }

            if (found != null)
            {
                for (int i = 0; i < componentBuffer.Count; i++)
                {
                    SpaceCombatRegistry.MapBlock(componentBuffer[i], found);
                }
                lateRetryAt.Remove(block);
            }
            else
            {
                lateRetryAt[block] = Time.time + LateResolveRetrySeconds;
            }
            visited.Clear();
            frontier.Clear();
            componentBuffer.Clear();
            return found;
        }

        private static int CompareCores(SpaceShipCore a, SpaceShipCore b)
        {
            float powerA = a.CurrentTotalPower;
            float powerB = b.CurrentTotalPower;
            if (powerA != powerB)
            {
                return powerB.CompareTo(powerA);
            }
            return GuidOf(a.BlockBehaviour).CompareTo(GuidOf(b.BlockBehaviour));
        }

        private static int CompareCaptains(SpaceCaptainBlock a, SpaceCaptainBlock b)
        {
            return GuidOf(a.BlockBehaviour).CompareTo(GuidOf(b.BlockBehaviour));
        }

        private static Guid GuidOf(BlockBehaviour block)
        {
            return block != null && block.BuildingBlock != null ? block.BuildingBlock.Guid : Guid.Empty;
        }

        // BuildShip needs its own core list because ResolveShipName/CompareCores run while the
        // Partition-level coreBuffer is still being iterated.
        private static readonly List<SpaceShipCore> coreBufferLocal = new List<SpaceShipCore>();
    }
}
