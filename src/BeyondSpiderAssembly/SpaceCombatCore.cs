using System;
using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public enum EnergyBus
    {
        Armor = 0,
        Shield = 1,
        Weapon = 2,
        Universal = 3,
        // Propulsion (ADR-0018). Deliberately appended AFTER Universal rather than inserted next
        // to the other three "consumer" buses: the capacitor's Energy Bus menu serializes its
        // selection as an int, so renumbering Universal would silently re-bus every capacitor in
        // every saved machine.
        Thrust = 4
    }

    public enum TrackKind
    {
        Ship,
        HeavyMissile,
        DefensiveMissile,
        LargeProjectile
    }

    public interface ITrackable
    {
        int PlayerID { get; }
        MPTeam Team { get; }
        TrackKind Kind { get; }
        Vector3 Position { get; }
        Vector3 Velocity { get; }
        float Radius { get; }
        // Must stay callable on an ALREADY-DESTROYED object — that is the question it answers.
        // Consumers hold trackables across frames (ship.Tracks between radar scans, ship.ChannelTargets,
        // a missile's chase target) and rounds/missiles die mid-flight, so a stale reference asking
        // "are you still there?" is the normal case, not an error. A Unity-backed implementor must
        // therefore test `this != null` (Unity's == reports a destroyed object as null) BEFORE it
        // touches gameObject/transform, or the managed-to-native getter throws NullReference.
        // Position/Velocity carry no such promise: read them only once IsAlive has returned true.
        bool IsAlive { get; }
    }

    // Only trackables with a stable, Guid-backed identity can be locked from the radar screen —
    // that identity is what CaptainLockNet resolves across clients (see Task 7). Ships and heavy
    // missiles are the only ITrackable kinds that are also placed blocks with a BuildingBlock.Guid.
    public interface ILockable : ITrackable
    {
        int GuidHash { get; }
    }

    public struct SensorTrack
    {
        public ITrackable Target;
        public Vector3 Position;
        public Vector3 Velocity;
        public float Distance;
        public float TimeToImpact;
        // Time.time of the scan that produced this record. The radar screen dead-reckons a blip
        // from Position/Velocity over the age of its track, so it keeps moving smoothly between
        // scans instead of stepping once per scan interval.
        public float ScanTime;
        public TrackKind Kind;
    }

    public struct FireSolution
    {
        public ITrackable Target;
        public Vector3 AimPoint;
        public float TimeToImpact;
        public float Priority;
    }

    // Anything the SpaceGunner can aim and trigger. The kinetic RailgunBarrelBlock and the beam
    // LaserLanceBlock both implement it, so the gunner (and the captain's fire-control lead
    // prediction it queries) treat every barrel in ship.Guns uniformly. CurrentMuzzleVelocity()
    // is the one axis fire-control cares about that they differ on: a railgun returns its finite
    // slug speed, a laser returns an effectively-infinite value so the intercept solution
    // collapses to "aim straight at the target" (no lead) — see LaserLanceBlock.
    public interface IShipGun
    {
        MText GunGroup { get; }
        MKey FireKey { get; }
        MToggle FireControl { get; }
        Transform transform { get; }
        Vector3 ApproximateMuzzlePosition();
        float CurrentMuzzleVelocity();
    }

    // Every weapon's hit resolution (cannon/flak RaycastAll march, missile sweep-fuze raycast,
    // missile contact-fuze OnTriggerEnter, laser RaycastAll) explicitly recognizes a fixed set of
    // gameplay hit types first (IKineticTarget armor, MissileProjectile, BlockBehaviour, subsystem
    // direct-hit) and only then falls back to treating whatever's left as solid terrain/obstacle.
    // Every trigger collider this mod itself creates for gameplay (missile capsules, the flak
    // turret's own hitbox) is attached to one of those recognized types and gets caught upstream of
    // that fallback — solid terrain/hull never uses a trigger collider here. So a collider that is
    // still a trigger by the time a caller reaches the fallback isn't real geometry at all (most
    // commonly Besiege's own free-camera rig, which carries a collider unrelated to this mod); treat
    // it as empty space instead of stopping/detonating on it. Callers must only call this AFTER
    // ruling out the recognized types — calling it up front would also skip legitimate hits on
    // missile capsules and the like.
    public static class SpaceCombatUtil
    {
        public static bool IsUnrecognizedObstacle(Collider collider)
        {
            return collider != null && collider.isTrigger;
        }
    }

    public sealed class EnergyGrid
    {
        public const int BusCount = 5;

        // Propulsion may only borrow from the Universal pool while Universal is above this
        // fraction (ADR-0018: "thrust has the lowest discharge priority"). Weapons and shields
        // keep an untouchable reserve, so a ship under fire loses maneuvering before it loses
        // guns — a dramatic normal state, not an error.
        private const float ThrustUniversalReserve = 0.25f;

        private readonly float[] capacitor = new float[BusCount];
        private readonly float[] capacity = new float[BusCount];
        private readonly float[] previousCapacity = new float[BusCount];
        private readonly float[] reactorShare = new float[BusCount];
        private readonly float[] spentThisSecond = new float[BusCount];

        public float ReactorOutput;

        public void Configure(float totalPower, float armorPowerShare, float shieldPowerShare, float weaponPowerShare, float thrustPowerShare)
        {
            ReactorOutput = Mathf.Max(0f, totalPower);
            float sum = Mathf.Max(0.001f, armorPowerShare + shieldPowerShare + weaponPowerShare + thrustPowerShare);
            reactorShare[(int)EnergyBus.Armor] = armorPowerShare / sum;
            reactorShare[(int)EnergyBus.Shield] = shieldPowerShare / sum;
            reactorShare[(int)EnergyBus.Weapon] = weaponPowerShare / sum;
            reactorShare[(int)EnergyBus.Universal] = 0f;
            reactorShare[(int)EnergyBus.Thrust] = thrustPowerShare / sum;
        }

        public void ClearCapacitors()
        {
            for (int i = 0; i < capacitor.Length; i++)
            {
                previousCapacity[i] = capacity[i];
                capacity[i] = 0f;
            }
        }

        public void AddCapacity(EnergyBus bus, float amount)
        {
            int index = (int)bus;
            amount = Mathf.Max(0f, amount);
            float newCapacity = capacity[index] + amount;
            float knownCapacity = Mathf.Max(previousCapacity[index], capacity[index]);
            if (newCapacity > knownCapacity)
            {
                capacitor[index] += (newCapacity - knownCapacity) * 0.5f;
            }
            capacity[index] = newCapacity;
        }

        public void Tick(float deltaTime)
        {
            for (int i = 0; i < capacitor.Length; i++)
            {
                capacitor[i] = Mathf.Min(capacity[i], capacitor[i]);
            }
            for (int i = 0; i < spentThisSecond.Length; i++)
            {
                spentThisSecond[i] = Mathf.Lerp(spentThisSecond[i], 0f, Mathf.Clamp01(deltaTime * 2f));
            }
            float overflow = Charge(EnergyBus.Armor, ReactorOutput * reactorShare[0] * deltaTime)
                           + Charge(EnergyBus.Shield, ReactorOutput * reactorShare[1] * deltaTime)
                           + Charge(EnergyBus.Weapon, ReactorOutput * reactorShare[2] * deltaTime)
                           + Charge(EnergyBus.Thrust, ReactorOutput * reactorShare[4] * deltaTime);
            if (overflow > 0f)
            {
                Charge(EnergyBus.Universal, overflow);
            }
        }

        public float Request(EnergyBus bus, float amount)
        {
            amount = Mathf.Max(0f, amount);
            if (amount <= 0f)
            {
                return 1f;
            }

            float supplied = Draw(bus, amount);
            if (bus != EnergyBus.Universal && supplied < amount)
            {
                supplied += Draw(EnergyBus.Universal, amount - supplied, UniversalFloorFor(bus));
            }

            spentThisSecond[(int)bus] += supplied;
            return Mathf.Clamp01(supplied / amount);
        }

        public bool CanSupply(EnergyBus bus, float amount)
        {
            amount = Mathf.Max(0f, amount);
            if (amount <= 0f)
            {
                return true;
            }

            float available = capacitor[(int)bus];
            if (bus != EnergyBus.Universal)
            {
                available += Mathf.Max(0f, capacitor[(int)EnergyBus.Universal] - UniversalFloorFor(bus));
            }
            return available >= amount;
        }

        // How much of the Universal pool a bus may NOT touch. Zero for everyone except Thrust,
        // which is the designed first casualty of a power shortfall (ADR-0018).
        private float UniversalFloorFor(EnergyBus bus)
        {
            if (bus != EnergyBus.Thrust)
            {
                return 0f;
            }
            return capacity[(int)EnergyBus.Universal] * ThrustUniversalReserve;
        }

        public float ChargeLevel(EnergyBus bus)
        {
            int index = (int)bus;
            if (capacity[index] <= 0f)
            {
                return 0f;
            }
            return capacitor[index] / capacity[index];
        }

        public float Capacity(EnergyBus bus)
        {
            return capacity[(int)bus];
        }

        public float DrawRate(EnergyBus bus)
        {
            return spentThisSecond[(int)bus];
        }

        private float Charge(EnergyBus bus, float amount)
        {
            int index = (int)bus;
            float room = Mathf.Max(0f, capacity[index] - capacitor[index]);
            float applied = Mathf.Min(room, amount);
            capacitor[index] += applied;
            return amount - applied;
        }

        private float Draw(EnergyBus bus, float amount)
        {
            return Draw(bus, amount, 0f);
        }

        private float Draw(EnergyBus bus, float amount, float floor)
        {
            int index = (int)bus;
            float drawable = Mathf.Max(0f, capacitor[index] - floor);
            float supplied = Mathf.Min(drawable, amount);
            capacitor[index] -= supplied;
            return supplied;
        }
    }

    public sealed class ShipState
    {
        // Which machine this ship was partitioned out of, and its cross-client identity: the
        // primary core's GuidHash (ADR-0011). PlayerID alone no longer identifies a ship — one
        // player may field several.
        public Machine Machine;
        public int PlayerID;
        public int CoreGuidHash;
        // Captain's Ship Name text when set, otherwise a per-player ordinal fallback ("SHIP 2").
        public string Name = "";
        // Core-local OBB of the whole hull, measured once by the tick-3 partition (orientation-
        // only frame: rotate by the core's rotation about the core's position, no scale).
        // HullLocalCenter is the box center's offset from the core in that frame — the hull is
        // not necessarily centered on the core block. The captain's channel-0 threat gate tests
        // predicted impacts against this box (包络盒), see SpaceCaptainBlock.PredictHullImpact.
        public Vector3 HullSize;
        public Vector3 HullLocalCenter;
        public float HullVolume;
        // Primary core (identity, power-share sliders); Cores holds every core in the component —
        // a multi-core hull merges reactor output (合并出力).
        public SpaceShipCore Core;
        public readonly List<SpaceShipCore> Cores = new List<SpaceShipCore>();
        public SpaceCaptainBlock Captain;
        public readonly EnergyGrid Energy = new EnergyGrid();
        public readonly List<SuperCapacitorBlock> Capacitors = new List<SuperCapacitorBlock>();
        public readonly List<RadarPanelBlock> Radars = new List<RadarPanelBlock>();
        public readonly List<ShieldProjectorBlock> Shields = new List<ShieldProjectorBlock>();
        public readonly List<SpaceFlakTurretBlock> FlakTurrets = new List<SpaceFlakTurretBlock>();
        // Kinetic railgun barrels and beam laser lances alike (both IShipGun) — the SpaceGunner
        // binds and aims whatever lands here; see IShipGun.
        public readonly List<IShipGun> Guns = new List<IShipGun>();
        public readonly List<NanoArmorBehaviour> Armor = new List<NanoArmorBehaviour>();
        public readonly List<MissileLauncherBlock> Launchers = new List<MissileLauncherBlock>();
        public readonly List<SpaceGunnerBlock> Gunners = new List<SpaceGunnerBlock>();
        // Propulsion (ADR-0018). Thrusters in "advanced control" mode are driven as a group by the
        // 6-DOF allocator below; the rest run off their own keys. Sorted by BuildingBlock.Guid so
        // host and clients agree on each thruster's slot index in the output batch (ThrusterNet).
        public readonly List<ThrusterBlock> Thrusters = new List<ThrusterBlock>();
        public readonly ThrustAllocator Allocator = new ThrustAllocator();
        // Every block the tick-3 partition put in this ship — the allocator needs it to find the
        // hull's live center of mass, which no other subsystem cared about before.
        public readonly List<BlockBehaviour> Blocks = new List<BlockBehaviour>();
        // The captain's driving command, in the captain's own axes, each component in [-1, 1]
        // (ADR-0018 §4 mapping: x = right, y = forward "up/down", z = up "fore/aft").
        // Host-authored in SP; on an MP client the local captain sends it up via ThrusterNet.DriveMsg.
        public Vector3 DriveTranslation;
        public Vector3 DriveRotation;
        // Attitude stabilizer: rotation runs as a rate loop that auto-nulls spin when released.
        // Off means fully open-loop on all six axes (ADR-0018 §5).
        public bool AttitudeHold = true;
        // Propulsion emergency cutoff — every thruster on the ship goes dark.
        public bool ThrustCutoff;
        // Distinct hull rigidbodies, cached so the per-tick center-of-mass doesn't sweep a
        // GetComponent<Rigidbody> over every block — the one non-trivial cost in the propulsion
        // tick (ADR-0018). The LIST is rebuilt once a second (that GetComponent sweep, plus the
        // weld-cluster dedup); COM and mass are recomputed fresh EVERY tick from it, so they track
        // the ship moving, flexing and losing blocks immediately (a destroyed body reads as Unity-
        // null and drops out) — only a change in which rigidbodies exist waits up to a second.
        public readonly List<Rigidbody> ThrustBodies = new List<Rigidbody>();
        public float NextBodyRefresh;
        public bool BodiesValid;
        public readonly List<SensorTrack> Tracks = new List<SensorTrack>();
        // Multi-channel fire control (ADR-0010): one independent lock per fire channel.
        // Channel 0 is the air-defence channel (captain auto-locks the highest closing threat,
        // player may override); channels 1-3 are player-assigned from the radar screen. Every
        // anti-air / point-defense weapon reads channel 0 directly (ADR-0012 retired the
        // separate DefenseDirectorBlock/DefensiveSolution field).
        public readonly ITrackable[] ChannelTargets = new ITrackable[FireChannels.Count];
        // Channel-0 multi-target threat list (ADR-0014): up to FireChannels.Channel0MaxTargets
        // contacts, rank-ordered by threat score sqrt(damage) / (impactTime + 0.5s), rebuilt
        // host-side every tick by the seated captain (ammunition only — ships never auto-enter).
        // A manual channel-0 lock is pinned at rank 0. ChannelTargets[0] always mirrors rank 0;
        // channel-0 weapons draw their individual target from this list via their own
        // FireChannelAssignment lottery.
        public readonly List<ITrackable> Channel0Threats = new List<ITrackable>();
        // Per-channel 雷达筛选 (ADR-0013): a RadarFilter category bitmask deciding which kinds of
        // contact this channel draws on the radar screen and may lock — channel 0's automatic
        // air-defence selection obeys its own row too. Player-set from the radar's filter bar.
        public readonly int[] ChannelFilters = RadarFilter.NewChannelFilters();
        // True while the player holds a hand-placed lock on channel 0 — the captain's auto
        // threat selection pauses until the lock is cleared or its target dies.
        public bool Channel0ManualLock;
        public float LastRefreshTime;

        // MP display sync (ShipEnergyNet): energy is spent host-only, so a client's local grid
        // drifts toward 100%. The host streams the four bus charge levels; client info panels read
        // them through DisplayChargeLevel while fresh, falling back to the local grid otherwise.
        public readonly float[] NetEnergyLevels = new float[EnergyGrid.BusCount];
        public float NetEnergyTime = -999f;
        internal float NextEnergySync;
        internal float NextThrustSync;
        // Suppresses the propulsion output stream while the whole ship is coasting and the clients
        // already know it (ThrusterNet.BroadcastOutputs) — the common case in flight.
        internal bool ThrustWasIdle;

        public float DisplayChargeLevel(EnergyBus bus)
        {
            if (NetAuthority.IsClient && Time.time - NetEnergyTime < 1f)
            {
                return NetEnergyLevels[(int)bus];
            }
            return Energy.ChargeLevel(bus);
        }
    }

    // MP replication of per-ship energy display state (host → clients, 0.25 s per ship). Pure
    // display: clients never spend or charge authoritatively — this only keeps the capacitor bars
    // honest. Ships are addressed by PlayerID + CoreGuidHash (ADR-0011 multi-ship identity).
    public class ShipEnergyNet : SingleInstance<ShipEnergyNet>
    {
        public override string Name { get { return "BeyondSpider Ship Energy Net"; } }

        // (playerId, coreGuidHash, armorLevel, shieldLevel, weaponLevel, universalLevel, thrustLevel)
        public static MessageType EnergyMsg = ModNetworking.CreateMessageType(
            DataType.Integer, DataType.Integer, DataType.Single, DataType.Single, DataType.Single, DataType.Single, DataType.Single);

        public void EnergyReceiver(Message msg)
        {
            ShipState ship = SpaceCombatRegistry.FindShip((int)msg.GetData(0), (int)msg.GetData(1));
            if (ship == null)
            {
                return;
            }
            ship.NetEnergyLevels[(int)EnergyBus.Armor] = (float)msg.GetData(2);
            ship.NetEnergyLevels[(int)EnergyBus.Shield] = (float)msg.GetData(3);
            ship.NetEnergyLevels[(int)EnergyBus.Weapon] = (float)msg.GetData(4);
            ship.NetEnergyLevels[(int)EnergyBus.Universal] = (float)msg.GetData(5);
            ship.NetEnergyLevels[(int)EnergyBus.Thrust] = (float)msg.GetData(6);
            ship.NetEnergyTime = Time.time;
        }
    }

    public static class SpaceCombatRegistry
    {
        private static readonly List<ShipState> ships = new List<ShipState>();
        private static readonly List<ITrackable> trackables = new List<ITrackable>();
        // Ship membership derived by the tick-3 partition (ShipPartition, ADR-0011). A null value
        // is a real entry: "this block is known to belong to no ship" (debris, coreless builds),
        // so repeated OwnShip() lookups on it stay a single dictionary hit.
        private static readonly Dictionary<BlockBehaviour, ShipState> blockToShip = new Dictionary<BlockBehaviour, ShipState>();
        private static readonly List<BlockBehaviour> blockKeyBuffer = new List<BlockBehaviour>();
        private static ShipState activeLocalShip;

        public static IList<ITrackable> Trackables
        {
            get { return trackables; }
        }

        public static IEnumerable<ShipState> Ships
        {
            get { return ships; }
        }

        // The local player's network id, null-hardened. PlayerData.localPlayer CAN be null while
        // StatMaster.isMP is still true — observed as a 1300-deep NRE spam from every per-frame
        // UI consumer (radar view, info panel) after a session disconnect, killing their whole
        // Update/OnGUI each frame. Treat "no local player" as id 0 (the host's id, also the SP
        // value) instead of throwing.
        public static int LocalPlayerId()
        {
            if (!StatMaster.isMP || PlayerData.localPlayer == null)
            {
                return 0;
            }
            return PlayerData.localPlayer.networkId;
        }

        // Which of the local player's ships the HUD (info-panel tabs + captain radar) currently
        // shows. Set by clicking a tab; falls back to the player's first ship whenever the
        // selection is stale (ship removed, sim restarted).
        public static ShipState ActiveLocalShip
        {
            get
            {
                int localPlayer = LocalPlayerId();
                if (activeLocalShip != null && activeLocalShip.PlayerID == localPlayer && ships.Contains(activeLocalShip))
                {
                    return activeLocalShip;
                }
                activeLocalShip = FindShip(localPlayer);
                return activeLocalShip;
            }
            set { activeLocalShip = value; }
        }

        // A core registering no longer creates the ShipState — ships are minted by the tick-3
        // connectivity partition. This just makes the core radar-visible and books its machine
        // for partitioning.
        public static void RegisterCore(SpaceShipCore core)
        {
            RegisterTrackable(core);
            if (core.BlockBehaviour != null)
            {
                ShipPartition.QueueMachine(core.BlockBehaviour.ParentMachine);
            }
        }

        public static void UnregisterCore(SpaceShipCore core)
        {
            UnregisterTrackable(core);
            for (int i = 0; i < ships.Count; i++)
            {
                if (ships[i].Cores.Contains(core))
                {
                    RemoveShip(ships[i]);
                    break;
                }
            }
        }

        // Legacy single-ship lookup: the player's first ship. Only the IMGUI fallback HUD still
        // wants "the" ship of a player; everything identity-sensitive uses the GuidHash overload.
        public static ShipState FindShip(int playerId)
        {
            for (int i = 0; i < ships.Count; i++)
            {
                if (ships[i].PlayerID == playerId)
                {
                    return ships[i];
                }
            }
            return null;
        }

        public static ShipState FindShip(int playerId, int coreGuidHash)
        {
            for (int i = 0; i < ships.Count; i++)
            {
                if (ships[i].PlayerID == playerId && ships[i].CoreGuidHash == coreGuidHash)
                {
                    return ships[i];
                }
            }
            return null;
        }

        public static void GetShips(int playerId, List<ShipState> buffer)
        {
            buffer.Clear();
            for (int i = 0; i < ships.Count; i++)
            {
                if (ships[i].PlayerID == playerId)
                {
                    buffer.Add(ships[i]);
                }
            }
        }

        // The ship a block belongs to, per the tick-3 connectivity partition. Blocks placed onto
        // an already-simulating machine miss the map and go through ShipPartition.LateResolve
        // (a bounded neighbor walk to the nearest block with known membership).
        public static ShipState ShipOf(BlockBehaviour block)
        {
            if (block == null || !block.isSimulating)
            {
                return null;
            }
            ShipState ship;
            if (blockToShip.TryGetValue(block, out ship))
            {
                return ship;
            }
            return ShipPartition.LateResolve(block);
        }

        public static void AddShip(ShipState ship)
        {
            if (ship != null && !ships.Contains(ship))
            {
                ships.Add(ship);
            }
        }

        public static void MapBlock(BlockBehaviour block, ShipState ship)
        {
            if (block != null)
            {
                blockToShip[block] = ship;
            }
        }

        public static bool TryGetMappedShip(BlockBehaviour block, out ShipState ship)
        {
            return blockToShip.TryGetValue(block, out ship);
        }

        // Forgets every ship and membership entry derived from `machine` — repartition safety,
        // and teardown when a machine stops simulating.
        public static void DropMachine(Machine machine)
        {
            for (int i = ships.Count - 1; i >= 0; i--)
            {
                if (ships[i].Machine == machine)
                {
                    RemoveShip(ships[i]);
                }
            }
            blockKeyBuffer.Clear();
            foreach (KeyValuePair<BlockBehaviour, ShipState> pair in blockToShip)
            {
                if (pair.Key == null || pair.Key.ParentMachine == machine)
                {
                    blockKeyBuffer.Add(pair.Key);
                }
            }
            for (int i = 0; i < blockKeyBuffer.Count; i++)
            {
                blockToShip.Remove(blockKeyBuffer[i]);
            }
        }

        private static void RemoveShip(ShipState ship)
        {
            ships.Remove(ship);
            if (activeLocalShip == ship)
            {
                activeLocalShip = null;
            }
            blockKeyBuffer.Clear();
            foreach (KeyValuePair<BlockBehaviour, ShipState> pair in blockToShip)
            {
                if (pair.Value == ship)
                {
                    blockKeyBuffer.Add(pair.Key);
                }
            }
            for (int i = 0; i < blockKeyBuffer.Count; i++)
            {
                blockToShip.Remove(blockKeyBuffer[i]);
            }
            if (ships.Count == 0)
            {
                // Last ship gone (simulation over): also purge the "known shipless" entries and
                // any pending partition/late-resolve state, so nothing leaks across sim sessions.
                blockToShip.Clear();
                ShipPartition.Reset();
            }
        }

        public static void RegisterTrackable(ITrackable trackable)
        {
            if (trackable != null && !trackables.Contains(trackable))
            {
                trackables.Add(trackable);
            }
        }

        public static void UnregisterTrackable(ITrackable trackable)
        {
            if (trackable != null)
            {
                trackables.Remove(trackable);
            }
        }

        public static void RegisterSubsystem<T>(int playerId, T subsystem, List<T> list)
        {
            if (subsystem != null && !list.Contains(subsystem))
            {
                list.Add(subsystem);
            }
        }

        public static void RemoveSubsystem<T>(T subsystem, List<T> list)
        {
            if (subsystem != null)
            {
                list.Remove(subsystem);
            }
        }
    }

    public sealed class SpaceCombatRuntime : MonoBehaviour
    {
        public static bool ShowArmorHP;

        private readonly Rect window = new Rect(14f, 80f, 330f, 420f);
        // MP diagnosis (no-combat energy-zero bug): one census line per ship every 5 s, showing
        // exactly which ingredient of the grid is missing (dead core skips the tick entirely;
        // missing/dead capacitors leave capacity at 0, which ChargeLevel reports as an exact 0%).
        private float nextEnergyCensus;
        private const float EnergyCensusInterval = 5f;

        private void FixedUpdate()
        {
            // Runs the tick-3 connectivity partition for machines whose cores just started
            // simulating (ADR-0011) — on host and clients alike.
            ShipPartition.Tick();

            foreach (ShipState ship in SpaceCombatRegistry.Ships)
            {
                if (ship.Core == null || !ship.Core.IsAlive)
                {
                    continue;
                }

                ship.Energy.ClearCapacitors();
                for (int i = 0; i < ship.Capacitors.Count; i++)
                {
                    SuperCapacitorBlock capacitor = ship.Capacitors[i];
                    if (capacitor != null && capacitor.IsAlive)
                    {
                        ship.Energy.AddCapacity(capacitor.Bus, capacitor.Capacity);
                    }
                }

                // Every core in the component contributes output (合并出力); the power-share
                // sliders always come from the primary core.
                float totalPower = 0f;
                for (int i = 0; i < ship.Cores.Count; i++)
                {
                    SpaceShipCore core = ship.Cores[i];
                    if (core != null && core.IsAlive)
                    {
                        totalPower += core.CurrentTotalPower;
                    }
                }
                ship.Energy.Configure(totalPower,
                    ship.Core.ArmorPowerShare.Value,
                    ship.Core.ShieldPowerShare.Value,
                    ship.Core.WeaponPowerShare.Value,
                    ship.Core.ThrustPowerShare.Value);
                ship.Energy.Tick(Time.fixedDeltaTime);

                // Propulsion (ADR-0018) is solved and applied per SHIP, not per block: the 6-DOF
                // allocator has to see every enrolled thruster at once to split one commanded
                // wrench between them. Host-only — a client's thrusters never touch AddForce
                // (ADR-0015), they only render the outputs the host streams back.
                if (NetAuthority.IsAuthority)
                {
                    ShipThrustControl.Tick(ship, Time.fixedDeltaTime);
                }

                // Energy display stream to clients (see ShipEnergyNet) — only the host's grid sees
                // real consumption, so it's the one whose levels are worth showing.
                if (StatMaster.isMP && NetAuthority.IsAuthority && Time.time >= ship.NextEnergySync)
                {
                    ship.NextEnergySync = Time.time + 0.25f;
                    ModNetworking.SendToAll(ShipEnergyNet.EnergyMsg.CreateMessage(
                        ship.PlayerID, ship.CoreGuidHash,
                        ship.Energy.ChargeLevel(EnergyBus.Armor),
                        ship.Energy.ChargeLevel(EnergyBus.Shield),
                        ship.Energy.ChargeLevel(EnergyBus.Weapon),
                        ship.Energy.ChargeLevel(EnergyBus.Universal),
                        ship.Energy.ChargeLevel(EnergyBus.Thrust)));
                }
            }

            if (Time.time >= nextEnergyCensus)
            {
                nextEnergyCensus = Time.time + EnergyCensusInterval;
                string role = !StatMaster.isMP ? "sp" : (StatMaster.isClient ? "client" : "host");
                foreach (ShipState ship in SpaceCombatRegistry.Ships)
                {
                    int capsAlive = 0;
                    for (int i = 0; i < ship.Capacitors.Count; i++)
                    {
                        if (ship.Capacitors[i] != null && ship.Capacitors[i].IsAlive)
                        {
                            capsAlive++;
                        }
                    }
                    Debug.Log("[BeyondSpider] energy census (" + role + "): ship P" + ship.PlayerID
                        + ":" + ship.CoreGuidHash
                        + " coreAlive=" + (ship.Core != null && ship.Core.IsAlive)
                        + " cores=" + ship.Cores.Count
                        + " caps=" + capsAlive + "/" + ship.Capacitors.Count
                        + " reactor=" + ship.Energy.ReactorOutput.ToString("F0")
                        + " capW=" + ship.Energy.Capacity(EnergyBus.Weapon).ToString("F0")
                        + " capU=" + ship.Energy.Capacity(EnergyBus.Universal).ToString("F0")
                        + " W=" + (ship.Energy.ChargeLevel(EnergyBus.Weapon) * 100f).ToString("F0") + "%"
                        + " U=" + (ship.Energy.ChargeLevel(EnergyBus.Universal) * 100f).ToString("F0") + "%");
                }
            }
        }

        private void OnGUI()
        {
            // BeyondSpiderInfoPanel is the real Besiege-styled replacement for this window; this
            // stays as the automatic fallback whenever that uGUI panel failed to build (see its
            // own try/catch), not dead code.
            if (StatMaster.hudHidden || BeyondSpiderInfoPanel.PanelReady)
            {
                return;
            }

            int localPlayer = SpaceCombatRegistry.LocalPlayerId();
            ShipState ship = SpaceCombatRegistry.FindShip(localPlayer);
            if (ship == null || ship.Core == null)
            {
                return;
            }

            GUILayout.BeginArea(window, "BeyondSpider Space Combat", GUI.skin.window);
            GUILayout.Label("Core: " + ship.Core.DisplayName);
            if (ship.Captain != null)
            {
                GUILayout.Label("Captain IFF: " + (ship.Captain.Iff.IsActive ? "on" : "off"));
            }
            GUILayout.Label("Total power MW: " + ship.Energy.ReactorOutput.ToString("0"));
            GUILayout.Label("Power share A/S/W/T: " + FormatPowerShare(ship, EnergyBus.Armor)
                + " / " + FormatPowerShare(ship, EnergyBus.Shield)
                + " / " + FormatPowerShare(ship, EnergyBus.Weapon)
                + " / " + FormatPowerShare(ship, EnergyBus.Thrust));
            GUILayout.Label("Cap A/S/W/U/T: "
                + FormatBus(ship, EnergyBus.Armor) + " / "
                + FormatBus(ship, EnergyBus.Shield) + " / "
                + FormatBus(ship, EnergyBus.Weapon) + " / "
                + FormatBus(ship, EnergyBus.Universal) + " / "
                + FormatBus(ship, EnergyBus.Thrust));
            GUILayout.Label("Tracks: " + ship.Tracks.Count + "  Shields: " + ship.Shields.Count);
            GUILayout.Label("Armor blocks: " + ship.Armor.Count + "  " + FormatArmor(ship));
            ShowArmorHP = GUILayout.Toggle(ShowArmorHP, "Show Armor HP");
            ITrackable channel0 = ship.ChannelTargets[0];
            GUILayout.Label(channel0 != null ? "Channel 0 target: " + channel0.Kind : "Channel 0 target: none");
            GUILayout.EndArea();
        }

        private static string FormatBus(ShipState ship, EnergyBus bus)
        {
            return (ship.DisplayChargeLevel(bus) * 100f).ToString("0") + "%";
        }

        private static string FormatPowerShare(ShipState ship, EnergyBus bus)
        {
            if (ship.Core == null)
            {
                return "0%";
            }

            float armor = ship.Core.ArmorPowerShare.Value;
            float shield = ship.Core.ShieldPowerShare.Value;
            float weapon = ship.Core.WeaponPowerShare.Value;
            float thrust = ship.Core.ThrustPowerShare.Value;
            float total = Mathf.Max(0.001f, armor + shield + weapon + thrust);

            switch (bus)
            {
                case EnergyBus.Armor:
                    return (armor / total * 100f).ToString("0") + "%";
                case EnergyBus.Shield:
                    return (shield / total * 100f).ToString("0") + "%";
                case EnergyBus.Weapon:
                    return (weapon / total * 100f).ToString("0") + "%";
                case EnergyBus.Thrust:
                    return (thrust / total * 100f).ToString("0") + "%";
                default:
                    return "0%";
            }
        }

        private static string FormatArmor(ShipState ship)
        {
            if (ship.Armor.Count == 0)
            {
                return "Integrity n/a";
            }

            float integritySum = 0f;
            float structuralSum = 0f;
            for (int i = 0; i < ship.Armor.Count; i++)
            {
                NanoArmorBehaviour armor = ship.Armor[i];
                if (armor == null)
                {
                    continue;
                }
                integritySum += armor.Integrity;
                structuralSum += armor.StructuralValue;
            }

            float count = Mathf.Max(1, ship.Armor.Count);
            return "Integrity " + (integritySum / count * 100f).ToString("0") + "%  Structural " + (structuralSum / count * 100f).ToString("0") + "%";
        }
    }
}
