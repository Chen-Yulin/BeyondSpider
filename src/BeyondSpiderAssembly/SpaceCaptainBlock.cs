using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class SpaceCaptainBlock : SpaceBlock
    {
        public MToggle Iff;
        // Names the ship on the info panel's ship tabs (ADR-0011). Read once per simulation —
        // MText can't change mid-simulation (see the guide's 输入采样规则).
        public MText ShipName;

        // Helm (ADR-0018 §4): three translation axes and three rotation axes, two keys each, plus
        // the attitude stabilizer and the propulsion emergency cutoff. Fourteen bindings on one
        // block is a lot, but the gunner already carries five including four directional ones, and
        // keeping them here means a ship needs exactly one command block rather than two.
        //
        // Axis mapping is the captain block's own frame: the block sits flat on the deck, so its
        // up points along the hull (fore/aft) and its forward points out of the deck (up/down).
        public MKey StrafeRightKey;
        public MKey StrafeLeftKey;
        public MKey AscendKey;
        public MKey DescendKey;
        public MKey ForwardKey;
        public MKey ReverseKey;
        public MKey PitchUpKey;
        public MKey PitchDownKey;
        public MKey YawRightKey;
        public MKey YawLeftKey;
        public MKey RollRightKey;
        public MKey RollLeftKey;
        public MKey StabilizerKey;
        public MKey ThrustCutoffKey;

        // Toggle state owned by whoever is flying; mirrored onto the ship each tick (and up to the
        // host in MP). Stabilizer defaults on — an unassisted ship tumbles forever after one tap.
        private bool attitudeHold = true;
        private bool thrustCutoff;
        private Vector3 lastSentTranslation = Vector3.one * -99f;
        private Vector3 lastSentRotation = Vector3.one * -99f;
        private bool lastSentHold;
        private bool lastSentCutoff;

        private const float FireControlPositionBucket = 10f;
        private const float FireControlVelocityBucket = 10f;

        // Channel-0 (air-defence) threat gate: a contact counts as a threat only if, continuing
        // straight along its current velocity (relative to the ship), it would enter the ship's
        // hull bounding box inside this window — 按抛射物的速度矢量，5s 内会命中本舰包络盒.
        // Threats are ranked by 威胁度 = sqrt(预估伤害) / (预计命中时间 + 0.5s) — the square
        // root keeps one huge warhead from permanently drowning out a swarm of small closers,
        // the +0.5s softening keeps an on-top-of-us contact finite (ADR-0014, user-confirmed
        // formula). The top Channel0MaxTargets threats form the channel-0 lock list.
        private const float AutoLockImpactWindow = 5f;
        private const float ThreatScoreTimeSoftening = 0.5f;
        // Estimated damage for an ammunition kind nothing recognizes: a token light kinetic body.
        private const float UnknownThreatMass = 0.1f;

        // Membership + order hysteresis for the threat list (the ADR-0014 "deferred" item, added
        // after playtest showed the radar's lock lines flapping between two similar threats):
        // an incumbent's score is scaled up before re-ranking, weighted by its previous rank —
        // prev rank 0 gets ×(1+Stickiness), prev rank 3 barely anything — so a challenger (or a
        // lower-ranked member) must beat a member by a real margin to displace it, and #1/#2
        // don't swap every scan as their impact times cross. Incumbents also keep threat status
        // slightly beyond the 5s window (×WindowGrace) so a contact hovering right on the window
        // boundary doesn't strobe in and out of the list.
        private const float IncumbentStickiness = 0.3f;
        private const float IncumbentWindowGrace = 1.3f;

        // Qualification hold: an incumbent that FAILS the hull-impact gate keeps its list spot
        // (at its last passing score) until it has failed continuously for this long. The gate
        // is a binary line-vs-box test, and a PN-guided missile's velocity vector weaves
        // (single-nozzle bang-bang steering, ADR-0008) — so an approaching salvo's extrapolated
        // lines swing on and off the hull box edge roughly in phase, which strobed the whole
        // threat list (and every lock line on the radar) in and out several times a second.
        // Score hysteresis alone can't absorb that: the gate doesn't lower the score, it
        // returns "not a threat" outright. Held targets still need to be alive, tracked and
        // filter-allowed — the hold only bridges gate flicker, it never resurrects anything.
        private const float ThreatHoldSeconds = 0.6f;

        private struct ThreatMemory
        {
            public float LastQualifiedTime;
            public float LastScore;
        }

        private readonly Dictionary<string, FireSolution> channelFireSolutionCache = new Dictionary<string, FireSolution>();
        private float channelFireSolutionCacheTime = -1f;

        // Scratch + last-broadcast state for the channel-0 threat list rebuild (host only).
        private struct ThreatEntry
        {
            public ITrackable Target;
            public float Score;
        }

        private static readonly List<ThreatEntry> threatBuffer = new List<ThreatEntry>();
        private static readonly List<ITrackable> previousThreatsScratch = new List<ITrackable>();
        private static readonly List<ITrackable> threatMemoryPruneScratch = new List<ITrackable>();
        private static readonly System.Comparison<ThreatEntry> threatComparison = CompareThreatEntries;
        private readonly List<ITrackable> broadcastThreats = new List<ITrackable>();
        // Per-target gate bookkeeping for the qualification hold (see ThreatHoldSeconds).
        private readonly Dictionary<ITrackable, ThreatMemory> threatMemory = new Dictionary<ITrackable, ThreatMemory>();

        private static int CompareThreatEntries(ThreatEntry a, ThreatEntry b)
        {
            return b.Score.CompareTo(a.Score);
        }

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Captain";
            Iff = AddToggle("IFF", "BSCaptainIFF", true);
            ShipName = AddText("Ship Name", "BSCaptainShipName", "");
            StrafeRightKey = AddKey("Strafe Right", "BSHelmStrafeRight", KeyCode.None);
            StrafeLeftKey = AddKey("Strafe Left", "BSHelmStrafeLeft", KeyCode.None);
            AscendKey = AddKey("Ascend", "BSHelmAscend", KeyCode.None);
            DescendKey = AddKey("Descend", "BSHelmDescend", KeyCode.None);
            ForwardKey = AddKey("Thrust Forward", "BSHelmForward", KeyCode.None);
            ReverseKey = AddKey("Thrust Reverse", "BSHelmReverse", KeyCode.None);
            PitchUpKey = AddKey("Pitch Up", "BSHelmPitchUp", KeyCode.None);
            PitchDownKey = AddKey("Pitch Down", "BSHelmPitchDown", KeyCode.None);
            YawRightKey = AddKey("Yaw Right", "BSHelmYawRight", KeyCode.None);
            YawLeftKey = AddKey("Yaw Left", "BSHelmYawLeft", KeyCode.None);
            RollRightKey = AddKey("Roll Right", "BSHelmRollRight", KeyCode.None);
            RollLeftKey = AddKey("Roll Left", "BSHelmRollLeft", KeyCode.None);
            StabilizerKey = AddKey("Toggle Attitude Hold", "BSHelmStabilizer", KeyCode.None);
            ThrustCutoffKey = AddKey("Thrust Cutoff", "BSHelmCutoff", KeyCode.None);
        }

        // The captain's Ship Name text, trimmed; "" when the player left it blank (the ship then
        // falls back to a per-player ordinal name — see ShipPartition.ResolveShipName).
        public string ShipDisplayName
        {
            get
            {
                string value = ShipName == null ? null : ShipName.Value;
                return value == null ? "" : value.Trim();
            }
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            // Usually a no-op at simulate start (the tick-3 partition hasn't run, OwnShip() is
            // null and the partition assigns captains itself); this covers a captain placed onto
            // an already-simulating ship. The radar dock is no longer opened here — the view
            // polls the active ship's captain every frame (CaptainRadarView.Update).
            TryClaimShip();
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null && ship.Captain == this)
            {
                ship.Captain = null;
                for (int channel = 0; channel < FireChannels.Count; channel++)
                {
                    ship.ChannelTargets[channel] = null;
                }
                ship.Channel0Threats.Clear();
                ship.Channel0ManualLock = false;
            }
            broadcastThreats.Clear();
            threatMemory.Clear();
        }

        // Claim-if-vacant, never steal: the partition already assigned each ship its
        // deterministic captain, and two captains on one hull must not fight over the seat every
        // tick. Must run on host and clients alike so a captain placed mid-simulation (or a spare
        // taking over a vacated seat) seats itself everywhere — driven from a plain FixedUpdate
        // because the engine does NOT dispatch the fixed-step Simulate hooks on network clients
        // (first MP playtest; same reason RadarPanelBlock self-drives its scan). Idempotent, so
        // also running alongside SimulateFixedUpdateHost on the host is harmless.
        private void FixedUpdate()
        {
            if (BlockBehaviour != null && BlockBehaviour.isSimulating)
            {
                TryClaimShip();
                SampleHelm();
            }
        }

        // The two helm TOGGLES are edge-triggered, so they're read in Update rather than the fixed
        // step — a tap between two fixed ticks must not be swallowed (same convention as the
        // shield's power key and the turrets' active switch).
        private void Update()
        {
            if (BlockBehaviour == null || !BlockBehaviour.isSimulating || !IsHelmOperator())
            {
                return;
            }
            if (StabilizerKey != null && StabilizerKey.IsPressed)
            {
                attitudeHold = !attitudeHold;
            }
            if (ThrustCutoffKey != null && ThrustCutoffKey.IsPressed)
            {
                thrustCutoff = !thrustCutoff;
            }
        }

        // Reads the twelve driving keys into the ship's command vector. On the host that lands
        // straight in ShipState; on an MP client the host owns all physics (ADR-0015), so only the
        // intent travels — and only when it changes, so a coasting ship costs nothing.
        //
        // IsHelmOperator is what stops the host's own keyboard from flying every remote player's
        // ship: input is read only on the machine that actually owns this captain.
        private void SampleHelm()
        {
            ShipState ship = OwnShip();
            if (ship == null || !ReferenceEquals(ship.Captain, this) || !IsHelmOperator())
            {
                return;
            }

            Vector3 translation = new Vector3(
                Axis(StrafeRightKey, StrafeLeftKey),
                Axis(AscendKey, DescendKey),
                Axis(ForwardKey, ReverseKey));
            Vector3 rotation = new Vector3(
                Axis(PitchUpKey, PitchDownKey),
                Axis(YawRightKey, YawLeftKey),
                Axis(RollRightKey, RollLeftKey));

            if (NetAuthority.IsAuthority)
            {
                ship.DriveTranslation = translation;
                ship.DriveRotation = rotation;
                ship.AttitudeHold = attitudeHold;
                ship.ThrustCutoff = thrustCutoff;
                return;
            }

            if (translation == lastSentTranslation && rotation == lastSentRotation
                && attitudeHold == lastSentHold && thrustCutoff == lastSentCutoff)
            {
                return;
            }
            lastSentTranslation = translation;
            lastSentRotation = rotation;
            lastSentHold = attitudeHold;
            lastSentCutoff = thrustCutoff;
            ModNetworking.SendToAll(ThrusterNet.DriveMsg.CreateMessage(
                PlayerID, ship.CoreGuidHash, translation, rotation, attitudeHold, thrustCutoff));
        }

        private bool IsHelmOperator()
        {
            return ThrusterNet.IsLocallyOwned(BlockBehaviour);
        }

        private static float Axis(MKey positive, MKey negative)
        {
            float value = 0f;
            if (positive != null && positive.IsHeld)
            {
                value += 1f;
            }
            if (negative != null && negative.IsHeld)
            {
                value -= 1f;
            }
            return value;
        }

        private void TryClaimShip()
        {
            ShipState ship = OwnShip();
            if (ship == null || ship.Captain != null)
            {
                return;
            }
            ship.Captain = this;
            if (ShipDisplayName.Length > 0)
            {
                ship.Name = ShipDisplayName;
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            ShipState ship = OwnShip();
            // Only the seated captain runs fire control — a spare captain on the same hull stays
            // passive until the seat frees up (TryClaimShip above).
            if (ship == null || !ReferenceEquals(ship.Captain, this))
            {
                return;
            }

            PruneChannelTargets(ship);
            UpdateChannel0Threats(ship);
        }

        // Host-authoritative cleanup: a channel drops its lock when the target dies, or when the
        // channel's own 雷达筛选 stops allowing that kind of contact (ADR-0013) — a lock the player
        // can no longer see on the radar must not keep quietly directing weapons. Either way the
        // clear reaches every machine via the unlock broadcast, and a dropped manual channel-0
        // lock hands the channel back to the auto threat selection.
        private void PruneChannelTargets(ShipState ship)
        {
            for (int channel = 0; channel < FireChannels.Count; channel++)
            {
                ITrackable target = ship.ChannelTargets[channel];
                if (target == null)
                {
                    continue;
                }
                if (channel == 0 && !ship.Channel0ManualLock)
                {
                    // Auto channel-0 mirror: UpdateChannel0Threats rebuilds it (and the whole
                    // threat list) right after this, so there is nothing to prune or broadcast.
                    continue;
                }
                if (target.IsAlive && RadarFilter.Allows(ship.ChannelFilters[channel], target))
                {
                    continue;
                }

                ship.ChannelTargets[channel] = null;
                if (channel == 0)
                {
                    ship.Channel0ManualLock = false;
                }
                ModNetworking.SendToAll(CaptainLockNet.LockMsg.CreateMessage(PlayerID, ship.CoreGuidHash, channel, 0, 0, false, false));
            }
        }

        // Channel 0 is the air-defence channel and now holds a multi-target THREAT LIST of up to
        // FireChannels.Channel0MaxTargets locks (ADR-0014), rebuilt host-side every tick; every
        // anti-air / point-defense weapon draws its own target from this list through its
        // FireChannelAssignment lottery (the old DefenseDirectorBlock stays retired — ADR-0012).
        // Auto candidates are AMMUNITION ONLY (通道0只锁弹药 — missiles and bare cannon shells,
        // never ships; a ship can still enter the list as the player's manual pin) whose
        // straight-line extrapolation would hit the ship's hull box within AutoLockImpactWindow
        // seconds, ranked by 威胁度 = sqrt(damage)/(impactTime+0.5). Hostility gating follows
        // the same IFF switch that gates firing permission (CanCommandLockedFireAt): with IFF
        // on (default) only genuinely hostile contacts are auto-selected; with IFF off, ANY
        // qualifying contact counts — including a friendly's stray shot or a projectile from
        // one of this player's OTHER ships (ADR-0011 multi-ship). A manual channel-0 lock
        // (Channel0ManualLock) is pinned at rank 0 (手动目标置顶) and the remaining slots keep
        // auto-filling around it. ChannelTargets[0] always mirrors rank 0. Channel 0's own
        // 雷达筛选 row narrows the auto pool the same way it narrows a manual lock (ADR-0013) —
        // except shells: their SHL row is display-only (default off, >=76mm shells only), so
        // they stay eligible for point defense regardless of it (RadarFilter.Allows).
        // The list is broadcast (Channel0ListMsg) when its ILockable membership/order changes so
        // every machine's radar shows the same channel-0 reticles; bare shells have no
        // cross-client identity and stay host-local — fine, the radar draws no marker for them.
        private void UpdateChannel0Threats(ShipState ship)
        {
            int filter = ship.ChannelFilters[0];
            ITrackable manual = ship.Channel0ManualLock ? ship.ChannelTargets[0] : null;

            // Snapshot last tick's list for the incumbent hysteresis below, BEFORE rebuilding it.
            previousThreatsScratch.Clear();
            previousThreatsScratch.AddRange(ship.Channel0Threats);

            threatBuffer.Clear();
            for (int i = 0; i < ship.Tracks.Count; i++)
            {
                SensorTrack track = ship.Tracks[i];
                ITrackable target = track.Target;
                if (target == null || !target.IsAlive || ReferenceEquals(target, manual))
                {
                    continue;
                }
                if (target.Kind == TrackKind.Ship)
                {
                    continue; // 通道0只锁弹药 — ships never auto-enter the threat list
                }
                if (Iff.IsActive && !SpaceBallistics.IsHostile(this, target))
                {
                    continue;
                }
                if (!RadarFilter.Allows(filter, target))
                {
                    continue;
                }

                int previousRank = previousThreatsScratch.IndexOf(target);
                float window = previousRank >= 0 ? AutoLockImpactWindow * IncumbentWindowGrace : AutoLockImpactWindow;
                float score = ThreatScore(ship, track, window);
                if (score != float.MinValue)
                {
                    if (previousRank >= 0)
                    {
                        score *= 1f + IncumbentStickiness
                            * (FireChannels.Channel0MaxTargets - previousRank) / (float)FireChannels.Channel0MaxTargets;
                    }
                    ThreatMemory memory;
                    memory.LastQualifiedTime = Time.time;
                    memory.LastScore = score;
                    threatMemory[target] = memory;
                }
                else if (previousRank >= 0)
                {
                    // Qualification hold: bridge the gate's flicker (weaving missile velocity)
                    // by keeping a recent incumbent at its last passing score.
                    ThreatMemory memory;
                    if (threatMemory.TryGetValue(target, out memory)
                        && Time.time - memory.LastQualifiedTime <= ThreatHoldSeconds)
                    {
                        score = memory.LastScore;
                    }
                }
                if (score == float.MinValue)
                {
                    continue;
                }

                ThreatEntry entry;
                entry.Target = target;
                entry.Score = score;
                threatBuffer.Add(entry);
            }
            threatBuffer.Sort(threatComparison);
            PruneThreatMemory();

            List<ITrackable> threats = ship.Channel0Threats;
            threats.Clear();
            if (manual != null)
            {
                threats.Add(manual);
            }
            for (int i = 0; i < threatBuffer.Count && threats.Count < FireChannels.Channel0MaxTargets; i++)
            {
                threats.Add(threatBuffer[i].Target);
            }
            ship.ChannelTargets[0] = threats.Count > 0 ? threats[0] : null;

            BroadcastChannel0List(ship);
        }

        // Drops hold bookkeeping for targets that have been unqualified longer than the hold
        // (or died) so the dictionary doesn't accumulate every projectile the ship ever saw.
        private void PruneThreatMemory()
        {
            threatMemoryPruneScratch.Clear();
            foreach (KeyValuePair<ITrackable, ThreatMemory> pair in threatMemory)
            {
                if (!pair.Key.IsAlive || Time.time - pair.Value.LastQualifiedTime > ThreatHoldSeconds * 2f)
                {
                    threatMemoryPruneScratch.Add(pair.Key);
                }
            }
            for (int i = 0; i < threatMemoryPruneScratch.Count; i++)
            {
                threatMemory.Remove(threatMemoryPruneScratch[i]);
            }
        }

        // Sends the threat list's ILockable members (rank order preserved, shells skipped — no
        // cross-client identity) whenever that projection changed since the last send.
        private void BroadcastChannel0List(ShipState ship)
        {
            if (!StatMaster.isMP)
            {
                return;
            }

            List<ITrackable> threats = ship.Channel0Threats;
            bool changed = false;
            int lockableCount = 0;
            for (int i = 0; i < threats.Count; i++)
            {
                if (!(threats[i] is ILockable))
                {
                    continue;
                }
                if (lockableCount >= broadcastThreats.Count || !ReferenceEquals(broadcastThreats[lockableCount], threats[i]))
                {
                    changed = true;
                }
                lockableCount++;
            }
            if (lockableCount != broadcastThreats.Count)
            {
                changed = true;
            }
            if (!changed)
            {
                return;
            }

            broadcastThreats.Clear();
            int[] players = new int[FireChannels.Channel0MaxTargets];
            int[] guids = new int[FireChannels.Channel0MaxTargets];
            int count = 0;
            for (int i = 0; i < threats.Count && count < FireChannels.Channel0MaxTargets; i++)
            {
                ILockable lockable = threats[i] as ILockable;
                if (lockable == null)
                {
                    continue;
                }
                players[count] = threats[i].PlayerID;
                guids[count] = lockable.GuidHash;
                broadcastThreats.Add(threats[i]);
                count++;
            }

            ModNetworking.SendToAll(CaptainLockNet.Channel0ListMsg.CreateMessage(
                PlayerID, ship.CoreGuidHash, count,
                players[0], guids[0], players[1], guids[1],
                players[2], guids[2], players[3], guids[3]));
        }

        // 威胁度 = sqrt(预估伤害) / (预计命中时间 + 0.5s). The gate inside PredictHullImpact:
        // extrapolate the contact along its ship-relative velocity and ask whether that straight
        // line enters the ship's own hull bounding box (包络盒) within AutoLockImpactWindow
        // seconds. A contact flying past (line misses the box), away (intersection lies in the
        // past), or too slowly to arrive in time all score float.MinValue (not a threat).
        private float ThreatScore(ShipState ship, SensorTrack track, float impactWindow)
        {
            if (ship.Core == null)
            {
                return float.MinValue;
            }

            Vector3 relativeVelocity = track.Velocity - ship.Core.Velocity;
            if (relativeVelocity.sqrMagnitude < 0.01f)
            {
                return float.MinValue;
            }

            float impactTime;
            if (!PredictHullImpact(ship, track, relativeVelocity, impactWindow, out impactTime))
            {
                return float.MinValue;
            }

            float damage = EstimateThreatDamage(track.Target, relativeVelocity.sqrMagnitude);
            return Mathf.Sqrt(Mathf.Max(0f, damage)) / (impactTime + ThreatScoreTimeSoftening);
        }

        // Estimated on-hit damage per ammunition kind: a missile promises its warhead blast plus
        // the direct-hit bonus; a kinetic shell promises its relative-speed KE damage (the same
        // mass·v²·coeff budget the penetration model charges, ADR-0007); anything unrecognized
        // is priced as a token light kinetic body so it can still make the list when nothing
        // bigger is inbound.
        private static float EstimateThreatDamage(ITrackable target, float relativeSpeedSq)
        {
            MissileProjectile missile = target as MissileProjectile;
            if (missile != null)
            {
                return missile.EstimatedThreatDamage;
            }
            SpaceKineticRound round = target as SpaceKineticRound;
            float mass = round != null ? round.MassEstimate : UnknownThreatMass;
            return mass * relativeSpeedSq * SpaceBalance.KineticDamagePerKE;
        }

        // Ray-vs-hull-OBB slab test in the core's orientation-only local frame — the exact frame
        // ShipPartition.ComputeHullBounds measured HullLocalCenter/HullSize in (rotate by the
        // core's rotation about the core's position, no scale), so the box rides the ship's
        // live pose for free. The box is expanded on every axis (Minkowski approximation) by the
        // contact's danger radius — for a missile, twice its warhead blast radius (a proximity
        // detonation that far out still washes the hull with blast, so a path that never touches
        // the hull itself can still be a hit); for anything else, its body radius. `impactTime`
        // is the entry time; 0 when the contact is already inside the box. Falls back to the
        // core's sphere radius when the partition measured no colliders.
        private const float MissileBlastPaddingFactor = 2f;

        private static float ThreatPaddingRadius(ITrackable target)
        {
            if (target == null)
            {
                return 0f;
            }
            MissileProjectile missile = target as MissileProjectile;
            if (missile != null)
            {
                return Mathf.Max(target.Radius, missile.BlastRadius * MissileBlastPaddingFactor);
            }
            return Mathf.Max(0f, target.Radius);
        }

        private static bool PredictHullImpact(ShipState ship, SensorTrack track, Vector3 relativeVelocity, float impactWindow, out float impactTime)
        {
            impactTime = 0f;
            float targetRadius = ThreatPaddingRadius(track.Target);

            if (ship.HullSize == Vector3.zero)
            {
                // No measured hull (partition found no solid colliders) — closest-approach test
                // against the core's nominal sphere, same as before the box existed.
                Vector3 toShip = ship.Core.Position - track.Position;
                float hitRadius = ship.Core.Radius + targetRadius;
                if (toShip.sqrMagnitude <= hitRadius * hitRadius)
                {
                    return false; // already inside the envelope — never an auto-attack target
                }
                float time = Vector3.Dot(toShip, relativeVelocity) / relativeVelocity.sqrMagnitude;
                if (time < 0f || time > impactWindow)
                {
                    return false;
                }
                Vector3 missVector = toShip - relativeVelocity * time;
                if (missVector.sqrMagnitude > hitRadius * hitRadius)
                {
                    return false;
                }
                impactTime = time;
                return true;
            }

            Quaternion toLocal = Quaternion.Inverse(ship.Core.transform.rotation);
            Vector3 localPosition = toLocal * (track.Position - ship.Core.Position) - ship.HullLocalCenter;
            Vector3 localVelocity = toLocal * relativeVelocity;
            Vector3 halfSize = ship.HullSize * 0.5f + Vector3.one * targetRadius;

            float entryTime = 0f;
            float exitTime = impactWindow;
            for (int axis = 0; axis < 3; axis++)
            {
                float position = localPosition[axis];
                float velocity = localVelocity[axis];
                if (Mathf.Abs(velocity) < 0.0001f)
                {
                    // Moving parallel to this slab: hits only if already between its faces.
                    if (Mathf.Abs(position) > halfSize[axis])
                    {
                        return false;
                    }
                    continue;
                }

                float t1 = (-halfSize[axis] - position) / velocity;
                float t2 = (halfSize[axis] - position) / velocity;
                if (t1 > t2)
                {
                    float swap = t1;
                    t1 = t2;
                    t2 = swap;
                }
                entryTime = Mathf.Max(entryTime, t1);
                exitTime = Mathf.Min(exitTime, t2);
                if (entryTime > exitTime)
                {
                    return false;
                }
            }

            // A contact ALREADY inside the (blast-padded) envelope is never an auto-attack
            // target — entryTime stayed at its 0 floor only when every axis was entered in the
            // past. This is what keeps the ship's own just-launched interceptors (born inside
            // the padding, IFF off) from being locked and shot by their own point defence; the
            // flip side, accepted deliberately, is that a hostile missile that slips inside the
            // envelope also stops being auto-engaged (firing at it there would detonate it
            // against the hull anyway — it can still be locked manually).
            if (entryTime <= 0f)
            {
                return false;
            }

            impactTime = entryTime;
            return true;
        }

        public bool CanCommandLockedFireAt(ITrackable target)
        {
            if (target == null)
            {
                return false;
            }

            return !Iff.IsActive || SpaceBallistics.IsHostile(this, target);
        }

        // Successor of the old per-channel TryGetChannelFireSolution: a lead solution against an
        // arbitrary tracked target, bucketed/cached exactly as before — with the TARGET folded
        // into the key instead of a channel index, because channel 0 now hands different targets
        // to different weapons (ADR-0014) while two channels locking the same target can still
        // share one cached solution.
        public bool TryGetTargetFireSolution(ITrackable target, Vector3 approximateMuzzlePosition, float muzzleVelocity, out FireSolution solution)
        {
            solution = default(FireSolution);
            ShipState ship = OwnShip();
            if (ship == null || target == null || !target.IsAlive)
            {
                return false;
            }

            SensorTrack lockedTrack;
            if (!TryGetTrackFor(ship, target, out lockedTrack))
            {
                return false;
            }

            if (!Mathf.Approximately(channelFireSolutionCacheTime, Time.fixedTime))
            {
                channelFireSolutionCache.Clear();
                channelFireSolutionCacheTime = Time.fixedTime;
            }

            Vector3 localPosition = transform.InverseTransformPoint(approximateMuzzlePosition);
            int bucketX = Mathf.RoundToInt(localPosition.x / FireControlPositionBucket);
            int bucketY = Mathf.RoundToInt(localPosition.y / FireControlPositionBucket);
            int bucketZ = Mathf.RoundToInt(localPosition.z / FireControlPositionBucket);
            int velocityBucket = Mathf.Max(1, Mathf.RoundToInt(muzzleVelocity / FireControlVelocityBucket));
            string key = target.GetHashCode().ToString() + "|" + bucketX.ToString() + ":" + bucketY.ToString() + ":" + bucketZ.ToString() + ":" + velocityBucket.ToString();

            if (channelFireSolutionCache.TryGetValue(key, out solution))
            {
                return solution.Target != null;
            }

            Vector3 bucketLocal = new Vector3(
                bucketX * FireControlPositionBucket,
                bucketY * FireControlPositionBucket,
                bucketZ * FireControlPositionBucket);
            Vector3 bucketWorld = transform.TransformPoint(bucketLocal);
            float quantizedVelocity = velocityBucket * FireControlVelocityBucket;
            // Every barrel is assumed to share the ship's own linear velocity (the core's), so the
            // lead is solved in that frame rather than per-turret — see ADR 0006. The core is present
            // whenever a ShipState exists, but guard anyway.
            Vector3 shipVelocity = ship.Core != null ? ship.Core.Velocity : Vector3.zero;
            float time = SpaceBallistics.EstimateInterceptTime(bucketWorld, lockedTrack.Position, lockedTrack.Velocity, quantizedVelocity, shipVelocity);

            solution.Target = lockedTrack.Target;
            solution.TimeToImpact = time;
            solution.AimPoint = lockedTrack.Position + (lockedTrack.Velocity - shipVelocity) * Mathf.Clamp(time, 0f, 8f);
            solution.Priority = 1f;
            channelFireSolutionCache.Add(key, solution);
            return true;
        }

        private static bool TryGetTrackFor(ShipState ship, ITrackable target, out SensorTrack track)
        {
            track = default(SensorTrack);
            for (int i = 0; i < ship.Tracks.Count; i++)
            {
                if (ReferenceEquals(ship.Tracks[i].Target, target))
                {
                    track = ship.Tracks[i];
                    return true;
                }
            }
            return false;
        }
    }

    public class CaptainLockNet : SingleInstance<CaptainLockNet>
    {
        public override string Name { get { return "BeyondSpider Captain Lock Net"; } }

        // (shipPlayerId, shipCoreGuidHash, channel, targetPlayerId, targetGuidHash, hasLock, manual)
        // shipCoreGuidHash identifies WHICH of the player's ships holds the lock (ADR-0011 — one
        // player may field several; PlayerID alone no longer names a ship). MP protocol break
        // against pre-multi-ship builds. `manual` matters only on channel 0: a manual lock pauses
        // the captain's auto threat selection on every machine (Channel0ManualLock), an auto
        // broadcast doesn't.
        public static MessageType LockMsg = ModNetworking.CreateMessageType(
            DataType.Integer, DataType.Integer, DataType.Integer, DataType.Integer, DataType.Integer, DataType.Boolean, DataType.Boolean);

        // (shipPlayerId, shipCoreGuidHash, channel, categoryMask) — a 雷达筛选 row the player
        // toggled on the radar screen (ADR-0013). The radar screen is client-local but the auto
        // air-defence selection it now gates runs host-side (SimulateFixedUpdateHost), so a
        // client's filter has to travel to the host to mean anything.
        public static MessageType FilterMsg = ModNetworking.CreateMessageType(
            DataType.Integer, DataType.Integer, DataType.Integer, DataType.Integer);

        // (shipPlayerId, shipCoreGuidHash, count, p0, g0, p1, g1, p2, g2, p3, g3) — the host's
        // channel-0 threat list (ADR-0014), rank-ordered, ILockable members only (bare shells
        // have no cross-client identity and are skipped; the radar draws no marker for them
        // anyway). Sent by the seated captain whenever that projection changes, so every
        // machine's radar rings the same channel-0 threats. Fixed 4 slots — unused ones ride as
        // zeros.
        public static MessageType Channel0ListMsg = ModNetworking.CreateMessageType(
            DataType.Integer, DataType.Integer, DataType.Integer,
            DataType.Integer, DataType.Integer, DataType.Integer, DataType.Integer,
            DataType.Integer, DataType.Integer, DataType.Integer, DataType.Integer);

        public void LockReceiver(Message msg)
        {
            int shipPlayerId = (int)msg.GetData(0);
            int shipCoreGuidHash = (int)msg.GetData(1);
            int channel = Mathf.Clamp((int)msg.GetData(2), 0, FireChannels.Count - 1);
            int targetPlayerId = (int)msg.GetData(3);
            int targetGuidHash = (int)msg.GetData(4);
            bool hasLock = (bool)msg.GetData(5);
            bool manual = (bool)msg.GetData(6);

            ShipState ship = SpaceCombatRegistry.FindShip(shipPlayerId, shipCoreGuidHash);
            if (ship == null)
            {
                return;
            }
            if (!hasLock)
            {
                ship.ChannelTargets[channel] = null;
                if (channel == 0)
                {
                    ship.Channel0ManualLock = false;
                }
                return;
            }

            IList<ITrackable> all = SpaceCombatRegistry.Trackables;
            for (int i = 0; i < all.Count; i++)
            {
                ILockable candidate = all[i] as ILockable;
                if (candidate != null && candidate.PlayerID == targetPlayerId && candidate.GuidHash == targetGuidHash)
                {
                    ship.ChannelTargets[channel] = candidate;
                    if (channel == 0)
                    {
                        ship.Channel0ManualLock = manual;
                    }
                    return;
                }
            }
        }

        // Rebuilds the local copy of a ship's channel-0 threat list from the host's broadcast.
        // Runs on every machine except the sender; purely display/engagement-authorization state
        // (fire control itself is host-only). Unresolvable entries (target already dead locally)
        // are skipped rather than aborting the whole list.
        public void Channel0ListReceiver(Message msg)
        {
            int shipPlayerId = (int)msg.GetData(0);
            int shipCoreGuidHash = (int)msg.GetData(1);
            int count = Mathf.Clamp((int)msg.GetData(2), 0, FireChannels.Channel0MaxTargets);

            ShipState ship = SpaceCombatRegistry.FindShip(shipPlayerId, shipCoreGuidHash);
            if (ship == null)
            {
                return;
            }

            ship.Channel0Threats.Clear();
            for (int slot = 0; slot < count; slot++)
            {
                int targetPlayerId = (int)msg.GetData(3 + slot * 2);
                int targetGuidHash = (int)msg.GetData(4 + slot * 2);
                ILockable resolved = FindLockable(targetPlayerId, targetGuidHash);
                if (resolved != null && resolved.IsAlive)
                {
                    ship.Channel0Threats.Add(resolved);
                }
            }
            ship.ChannelTargets[0] = ship.Channel0Threats.Count > 0 ? ship.Channel0Threats[0] : null;
        }

        private static ILockable FindLockable(int playerId, int guidHash)
        {
            IList<ITrackable> all = SpaceCombatRegistry.Trackables;
            for (int i = 0; i < all.Count; i++)
            {
                ILockable candidate = all[i] as ILockable;
                if (candidate != null && candidate.PlayerID == playerId && candidate.GuidHash == guidHash)
                {
                    return candidate;
                }
            }
            return null;
        }

        public void FilterReceiver(Message msg)
        {
            int shipPlayerId = (int)msg.GetData(0);
            int shipCoreGuidHash = (int)msg.GetData(1);
            int channel = Mathf.Clamp((int)msg.GetData(2), 0, FireChannels.Count - 1);
            int mask = (int)msg.GetData(3) & RadarFilter.AllMask;

            ShipState ship = SpaceCombatRegistry.FindShip(shipPlayerId, shipCoreGuidHash);
            if (ship == null)
            {
                return;
            }
            ship.ChannelFilters[channel] = mask;
            // No lock pruning here: the seated captain re-checks every channel against its filter
            // each host tick (PruneChannelTargets), which is also the only side allowed to
            // broadcast the resulting unlock.
        }
    }
}
