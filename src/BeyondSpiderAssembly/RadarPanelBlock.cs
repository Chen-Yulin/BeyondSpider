using System.Collections.Generic;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class RadarPanelBlock : SpaceBlock
    {
        public MSlider Range;
        public MSlider ConeAngle;
        public MToggle Iff;

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
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Radars);
                registered = true;
            }
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
            nextScanTime = Time.time + 0.25f;

            if (ship == null)
            {
                return;
            }

            if (Time.time - ship.LastRefreshTime > 0.05f)
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
                // Skip our own ship (redundant with CaptainRadarView's always-on self marker at the
                // origin), but let our own launched heavy missiles through so the captain can watch
                // them fly toward the target on the radar screen.
                if (target.PlayerID == PlayerID && target.Kind == TrackKind.Ship)
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
