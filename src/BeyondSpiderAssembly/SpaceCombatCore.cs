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
        Universal = 3
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

    public sealed class EnergyGrid
    {
        private readonly float[] capacitor = new float[4];
        private readonly float[] capacity = new float[4];
        private readonly float[] previousCapacity = new float[4];
        private readonly float[] reactorShare = new float[4];
        private readonly float[] spentThisSecond = new float[4];

        public float ReactorOutput;

        public void Configure(float totalPower, float armorPowerShare, float shieldPowerShare, float weaponPowerShare)
        {
            ReactorOutput = Mathf.Max(0f, totalPower);
            float sum = Mathf.Max(0.001f, armorPowerShare + shieldPowerShare + weaponPowerShare);
            reactorShare[(int)EnergyBus.Armor] = armorPowerShare / sum;
            reactorShare[(int)EnergyBus.Shield] = shieldPowerShare / sum;
            reactorShare[(int)EnergyBus.Weapon] = weaponPowerShare / sum;
            reactorShare[(int)EnergyBus.Universal] = 0f;
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
                           + Charge(EnergyBus.Weapon, ReactorOutput * reactorShare[2] * deltaTime);
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
                supplied += Draw(EnergyBus.Universal, amount - supplied);
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
                available += capacitor[(int)EnergyBus.Universal];
            }
            return available >= amount;
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
            int index = (int)bus;
            float supplied = Mathf.Min(capacitor[index], amount);
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

        public static int LocalPlayerId()
        {
            return StatMaster.isMP ? PlayerData.localPlayer.networkId : 0;
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
                    if (capacitor != null)
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
                    ship.Core.WeaponPowerShare.Value);
                ship.Energy.Tick(Time.fixedDeltaTime);
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

            int localPlayer = StatMaster.isMP ? PlayerData.localPlayer.networkId : 0;
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
            GUILayout.Label("Power share A/S/W: " + FormatPowerShare(ship, EnergyBus.Armor)
                + " / " + FormatPowerShare(ship, EnergyBus.Shield)
                + " / " + FormatPowerShare(ship, EnergyBus.Weapon));
            GUILayout.Label("Cap A/S/W/U: "
                + FormatBus(ship, EnergyBus.Armor) + " / "
                + FormatBus(ship, EnergyBus.Shield) + " / "
                + FormatBus(ship, EnergyBus.Weapon) + " / "
                + FormatBus(ship, EnergyBus.Universal));
            GUILayout.Label("Tracks: " + ship.Tracks.Count + "  Shields: " + ship.Shields.Count);
            GUILayout.Label("Armor blocks: " + ship.Armor.Count + "  " + FormatArmor(ship));
            ShowArmorHP = GUILayout.Toggle(ShowArmorHP, "Show Armor HP");
            ITrackable channel0 = ship.ChannelTargets[0];
            GUILayout.Label(channel0 != null ? "Channel 0 target: " + channel0.Kind : "Channel 0 target: none");
            GUILayout.EndArea();
        }

        private static string FormatBus(ShipState ship, EnergyBus bus)
        {
            return (ship.Energy.ChargeLevel(bus) * 100f).ToString("0") + "%";
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
            float total = Mathf.Max(0.001f, armor + shield + weapon);

            switch (bus)
            {
                case EnergyBus.Armor:
                    return (armor / total * 100f).ToString("0") + "%";
                case EnergyBus.Shield:
                    return (shield / total * 100f).ToString("0") + "%";
                case EnergyBus.Weapon:
                    return (weapon / total * 100f).ToString("0") + "%";
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
