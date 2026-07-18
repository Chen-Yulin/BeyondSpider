using System.Collections.Generic;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class RadarPanelBlock : SpaceBlock
    {
        public MSlider Range;
        public MSlider ConeAngle;
        public MToggle Iff;

        // 20 Hz, up from the old 4 Hz. Sensor tracks feed both the radar screen's blips and the
        // captain's lead solutions; the screen dead-reckons between scans (CaptainRadarView) so it
        // no longer needs the rate to look smooth, but a 250ms-stale track still shows up as a
        // stale aim point on a fast crosser. A scan is a distance + cone-angle test over the
        // trackable registry, so the extra rate costs almost nothing.
        private const float ScanInterval = 0.05f;
        // How long after one radar refreshes ship.Tracks its sisters may still append to that same
        // list rather than clearing it again. Every radar scans on the same grid tick (see
        // SimulateFixedUpdateHost), so this only has to absorb float noise while staying well
        // under ScanInterval.
        private const float TrackRefreshWindow = ScanInterval * 0.5f;

        private float nextScanTime;
        // See agent-besiege-mod-guide.md's "注册时序规范" — retried each tick until it
        // succeeds, since ShipState may not exist yet when this OnSimulateStart runs.
        private bool registered;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Radar Panel";
            Range = AddSlider("Range", "BSRadarRange", 850f, 100f, 3000f);
            ConeAngle = AddSlider("Cone Angle", "BSConeAngle", 65f, 10f, 170f);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            ShipState ship = OwnShip();
            if (ship != null)
            {
                OnAssignedToShip(ship);
            }
        }

        public override void OnAssignedToShip(ShipState ship)
        {
            SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Radars);
            registered = true;
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Radars);
            }
            registered = false;
        }

        public override void SimulateFixedUpdateHost()
        {
            ShipState ship = OwnShip();
            if (ship != null && !registered)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Radars);
                registered = true;
            }

            if (Time.time < nextScanTime)
            {
                return;
            }
            // Phase-locked to a shared global grid rather than "now + interval": every radar on a
            // ship has to scan in the SAME FixedUpdate for the clear-then-append merge below to
            // keep all of their tracks. On its own phase, a radar placed mid-simulation would fall
            // outside TrackRefreshWindow and wipe its sisters' tracks every round.
            nextScanTime = (Mathf.Floor(Time.time / ScanInterval) + 1f) * ScanInterval;

            if (ship == null)
            {
                return;
            }

            if (Time.time - ship.LastRefreshTime > TrackRefreshWindow)
            {
                ship.Tracks.Clear();
                ship.LastRefreshTime = Time.time;
            }
            IList<ITrackable> targets = SpaceCombatRegistry.Trackables;
            for (int i = 0; i < targets.Count; i++)
            {
                ITrackable target = targets[i];
                if (target == null || !target.IsAlive)
                {
                    continue;
                }
                // Skip only THIS ship's own core (redundant with CaptainRadarView's always-on self
                // marker at the origin). Filtering by PlayerID here would also hide the player's
                // OTHER ships (ADR-0011 multi-ship) — sister ships must show up as friendly blips.
                // Own launched heavy missiles pass through too, so the captain can watch them fly.
                if (target.Kind == TrackKind.Ship && ReferenceEquals(target, ship.Core))
                {
                    continue;
                }

                Vector3 delta = target.Position - transform.position;
                float distance = delta.magnitude;
                if (distance > Range.Value)
                {
                    continue;
                }
                if (Vector3.Angle(transform.forward, delta) > ConeAngle.Value * 0.5f)
                {
                    continue;
                }

                SensorTrack track = new SensorTrack();
                track.Target = target;
                track.Position = target.Position;
                track.Velocity = target.Velocity;
                track.Distance = distance;
                track.Kind = target.Kind;
                track.ScanTime = Time.time;
                track.TimeToImpact = EstimateTimeToImpact(target, ship.Core);
                ship.Tracks.Add(track);
            }
        }

        private static float EstimateTimeToImpact(ITrackable target, SpaceShipCore core)
        {
            if (core == null)
            {
                return 999f;
            }
            Vector3 delta = core.Position - target.Position;
            float closing = Vector3.Dot(target.Velocity - core.Velocity, delta.normalized);
            if (closing <= 1f)
            {
                return 999f;
            }
            return delta.magnitude / closing;
        }
    }
}
