using System;
using System.Collections.Generic;
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

    public enum CommandPriority
    {
        AntiAir,
        AntiShip
    }

    public interface ITrackable
    {
        int PlayerID { get; }
        MPTeam Team { get; }
        TrackKind Kind { get; }
        Vector3 Position { get; }
        Vector3 Velocity { get; }
        float Radius { get; }
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
        public TrackKind Kind;
    }

    public struct FireSolution
    {
        public ITrackable Target;
        public Vector3 AimPoint;
        public float TimeToImpact;
        public float Priority;
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
        public SpaceShipCore Core;
        public SpaceCaptainBlock Captain;
        public CommandPriority Priority = CommandPriority.AntiAir;
        public readonly EnergyGrid Energy = new EnergyGrid();
        public readonly List<SuperCapacitorBlock> Capacitors = new List<SuperCapacitorBlock>();
        public readonly List<RadarPanelBlock> Radars = new List<RadarPanelBlock>();
        public readonly List<DefenseDirectorBlock> Directors = new List<DefenseDirectorBlock>();
        public readonly List<ShieldProjectorBlock> Shields = new List<ShieldProjectorBlock>();
        public readonly List<CiwsBlock> Ciws = new List<CiwsBlock>();
        public readonly List<SpaceFlakTurretBlock> FlakTurrets = new List<SpaceFlakTurretBlock>();
        public readonly List<RailgunBarrelBlock> Guns = new List<RailgunBarrelBlock>();
        public readonly List<NanoArmorBehaviour> Armor = new List<NanoArmorBehaviour>();
        public readonly List<SpaceInterceptorLauncherBlock> Launchers = new List<SpaceInterceptorLauncherBlock>();
        public readonly List<SpaceGunnerBlock> Gunners = new List<SpaceGunnerBlock>();
        public readonly List<SensorTrack> Tracks = new List<SensorTrack>();
        public FireSolution DefensiveSolution;
        public ITrackable LockedTarget;
        public FireSolution LockedSolution;
        public float LastRefreshTime;
    }

    public static class SpaceCombatRegistry
    {
        private static readonly Dictionary<int, ShipState> ships = new Dictionary<int, ShipState>();
        private static readonly List<ITrackable> trackables = new List<ITrackable>();

        public static IList<ITrackable> Trackables
        {
            get { return trackables; }
        }

        public static IEnumerable<ShipState> Ships
        {
            get { return ships.Values; }
        }

        public static ShipState RegisterCore(SpaceShipCore core)
        {
            ShipState state;
            if (!ships.TryGetValue(core.PlayerID, out state))
            {
                state = new ShipState();
                ships.Add(core.PlayerID, state);
            }
            state.Core = core;
            RegisterTrackable(core);
            return state;
        }

        public static void UnregisterCore(SpaceShipCore core)
        {
            if (ships.ContainsKey(core.PlayerID) && ships[core.PlayerID].Core == core)
            {
                ships.Remove(core.PlayerID);
            }
            UnregisterTrackable(core);
        }

        public static ShipState FindShip(int playerId)
        {
            ShipState state;
            ships.TryGetValue(playerId, out state);
            return state;
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

                ship.Core.ConfigureEnergy(ship.Energy);
                ship.Energy.Tick(Time.fixedDeltaTime);
            }
        }

        private void OnGUI()
        {
            if (StatMaster.hudHidden)
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
            GUILayout.Label("Captain priority: " + ship.Priority);
            GUILayout.Label("Total power MW: " + ship.Energy.ReactorOutput.ToString("0"));
            GUILayout.Label("Power share A/S/W: " + FormatPowerShare(ship, EnergyBus.Armor)
                + " / " + FormatPowerShare(ship, EnergyBus.Shield)
                + " / " + FormatPowerShare(ship, EnergyBus.Weapon));
            GUILayout.Label("Cap A/S/W/U: "
                + FormatBus(ship, EnergyBus.Armor) + " / "
                + FormatBus(ship, EnergyBus.Shield) + " / "
                + FormatBus(ship, EnergyBus.Weapon) + " / "
                + FormatBus(ship, EnergyBus.Universal));
            GUILayout.Label("Tracks: " + ship.Tracks.Count + "  CIWS: " + ship.Ciws.Count + "  Shields: " + ship.Shields.Count);
            GUILayout.Label("Armor blocks: " + ship.Armor.Count + "  " + FormatArmor(ship));
            ShowArmorHP = GUILayout.Toggle(ShowArmorHP, "Show Armor HP");
            if (ship.DefensiveSolution.Target != null)
            {
                GUILayout.Label("Defense target: " + ship.DefensiveSolution.Target.Kind + "  TTI "
                    + ship.DefensiveSolution.TimeToImpact.ToString("0.0") + "s");
            }
            else
            {
                GUILayout.Label("Defense target: none");
            }
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
