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
        private readonly float[] reactorShare = new float[4];
        private readonly float[] spentThisSecond = new float[4];
        private readonly bool[] capacitorInitialized = new bool[4];

        public float ReactorOutput;

        public void Configure(float totalOutput, float armorShare, float shieldShare, float weaponShare)
        {
            ReactorOutput = Mathf.Max(0f, totalOutput);
            float sum = Mathf.Max(0.001f, armorShare + shieldShare + weaponShare);
            reactorShare[(int)EnergyBus.Armor] = armorShare / sum;
            reactorShare[(int)EnergyBus.Shield] = shieldShare / sum;
            reactorShare[(int)EnergyBus.Weapon] = weaponShare / sum;
            reactorShare[(int)EnergyBus.Universal] = 0f;
        }

        public void ClearCapacitors()
        {
            for (int i = 0; i < capacitor.Length; i++)
            {
                capacity[i] = 0f;
            }
        }

        public void AddCapacity(EnergyBus bus, float amount)
        {
            int index = (int)bus;
            capacity[index] += Mathf.Max(0f, amount);
            if (!capacitorInitialized[index])
            {
                capacitor[index] = capacity[index] * 0.5f;
                capacitorInitialized[index] = true;
            }
            capacitor[index] = Mathf.Min(capacity[index], capacitor[index]);
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
            Charge(EnergyBus.Armor, ReactorOutput * reactorShare[0] * deltaTime);
            Charge(EnergyBus.Shield, ReactorOutput * reactorShare[1] * deltaTime);
            Charge(EnergyBus.Weapon, ReactorOutput * reactorShare[2] * deltaTime);
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

        private void Charge(EnergyBus bus, float amount)
        {
            int index = (int)bus;
            capacitor[index] = Mathf.Min(capacity[index], capacitor[index] + amount);
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
        public readonly List<SpaceGunBlock> Guns = new List<SpaceGunBlock>();
        public readonly List<SpaceInterceptorLauncherBlock> Launchers = new List<SpaceInterceptorLauncherBlock>();
        public readonly List<SpaceGunnerBlock> Gunners = new List<SpaceGunnerBlock>();
        public readonly List<SensorTrack> Tracks = new List<SensorTrack>();
        public FireSolution DefensiveSolution;
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
            GUILayout.Label("Energy MW: " + ship.Energy.ReactorOutput.ToString("0"));
            GUILayout.Label("Cap A/S/W/U: "
                + FormatBus(ship, EnergyBus.Armor) + " / "
                + FormatBus(ship, EnergyBus.Shield) + " / "
                + FormatBus(ship, EnergyBus.Weapon) + " / "
                + FormatBus(ship, EnergyBus.Universal));
            GUILayout.Label("Tracks: " + ship.Tracks.Count + "  CIWS: " + ship.Ciws.Count + "  Shields: " + ship.Shields.Count);
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
    }
}
