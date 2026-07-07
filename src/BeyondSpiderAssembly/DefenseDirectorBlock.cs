using System.Collections.Generic;
using Modding.Blocks;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class DefenseDirectorBlock : SpaceBlock
    {
        public MSlider DefendedRadius;
        public MMenu Priority;

        // See agent-besiege-mod-guide.md's "注册时序规范" — retried each tick until it
        // succeeds, since ShipState may not exist yet when this OnSimulateStart runs.
        private bool registered;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Defense Director";
            DefendedRadius = AddSlider("Defended Radius", "BSDefendedRadius", 450f, 50f, 1500f);
            Priority = AddMenu("Target Priority", 0, new List<string> { "Time to impact", "Heavy threats" }, false);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Directors);
                registered = true;
            }
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Directors);
            }
            registered = false;
        }

        public override void SimulateFixedUpdateHost()
        {
            ShipState ship = OwnShip();
            if (ship != null && !registered)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Directors);
                registered = true;
            }
            if (ship == null || ship.Core == null)
            {
                return;
            }

            FireSolution best = new FireSolution();
            float bestScore = -1f;
            for (int i = 0; i < ship.Tracks.Count; i++)
            {
                SensorTrack track = ship.Tracks[i];
                if (!SpaceBallistics.IsHostile(this, track.Target))
                {
                    continue;
                }
                if (track.Distance > DefendedRadius.Value)
                {
                    continue;
                }

                float threat = track.Kind == TrackKind.HeavyMissile ? 1000f : 200f;
                float ttiScore = 100f / Mathf.Max(1f, track.TimeToImpact);
                float score = Priority.Value == 0 ? ttiScore + threat * 0.01f : threat + ttiScore;
                if (score > bestScore)
                {
                    bestScore = score;
                    best.Target = track.Target;
                    best.AimPoint = track.Position + track.Velocity * Mathf.Clamp(track.TimeToImpact, 0f, 2f);
                    best.TimeToImpact = track.TimeToImpact;
                    best.Priority = score;
                }
            }
            ship.DefensiveSolution = best;
        }
    }
}
