using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // MP replication of the shield field's state (the flak StateMsg pattern): authoritative
    // interception and energy pricing stay host-only; the host streams (active, upkeepRatio,
    // intercepted, hitLocalPos) on a heartbeat. Clients feed the values into UpdateVisual AND into
    // a cosmetic interception pass (ClientIntercept) that bleeds their own mirror rounds/missiles
    // through the same gates — so a client sees shells slow inside a shield instead of sailing
    // through, with active/upkeepRatio (including the host's overload drain) taken from the host's
    // stream. hitLocalPos (vis-mesh local space, so latency can't smear it off a moving shield)
    // lets the client drop its hex ripple on the same patch of the field even when its own mirror
    // round diverged or is missing. overloaded/overloadLocalPos do the same for the foreign-shield
    // overlap overload — that gate runs host-only, so the alarm-red ripple has to be streamed.
    public class ShieldNet : SingleInstance<ShieldNet>
    {
        public override string Name { get { return "BeyondSpider Shield Net"; } }

        // playerId, guidHash, active, upkeepRatio, interceptedThisTick, hitLocalPos (vis-mesh
        // space), overloadedThisTick, overloadLocalPos (vis-mesh space)
        public static MessageType StateMsg = ModNetworking.CreateMessageType(
            DataType.Integer, DataType.Integer, DataType.Boolean, DataType.Single, DataType.Boolean, DataType.Vector3,
            DataType.Boolean, DataType.Vector3);

        private readonly Dictionary<string, ShieldProjectorBlock> projectors = new Dictionary<string, ShieldProjectorBlock>();

        public void Register(ShieldProjectorBlock projector)
        {
            projectors[Key(projector.PlayerID, projector.GuidHash)] = projector;
        }

        public void Unregister(ShieldProjectorBlock projector)
        {
            projectors.Remove(Key(projector.PlayerID, projector.GuidHash));
        }

        public void StateReceiver(Message msg)
        {
            ShieldProjectorBlock projector;
            if (projectors.TryGetValue(Key((int)msg.GetData(0), (int)msg.GetData(1)), out projector) && projector != null)
            {
                projector.ReceiveState((bool)msg.GetData(2), (float)msg.GetData(3), (bool)msg.GetData(4), (Vector3)msg.GetData(5),
                    (bool)msg.GetData(6), (Vector3)msg.GetData(7));
            }
        }

        private static string Key(int playerId, int guid)
        {
            return playerId + ":" + guid;
        }
    }

    public class ShieldProjectorBlock : SpaceBlock
    {
        // The relative-speed gate (m/s): a round/missile is bled only while its speed RELATIVE to
        // this shield generator exceeds this, and only down to this same (absolute) floor. See
        // docs/adr/0006-relative-velocity-after-inheritance.md.
        private const float HyperVelocityThreshold = 600f;
        // How hard the field bites per tick — only sets how many ticks it takes to bleed a round
        // down to the threshold, not the total energy (that is priced by velocity-damage removed,
        // see Decelerate). Big enough that most rounds reach the threshold within a tick or two.
        private const float DecelCoefficient = 80f;
        private const float UpkeepBase = 6f;
        private const float UpkeepPerVolume = 0.02f;
        // Spin-up surge: re-energizing the field costs a lump sum on top of steady-state upkeep,
        // priced as this many ticks' worth of it. Paid once per false->true toggle edge, not
        // per-tick — keeps players from treating the shield as a free instant on/off toggle.
        private const float StartupUpkeepTicks = 5f;
        // A field reaching into another ship's field (friend or foe — the overlap itself is what
        // matters, not IFF) is unstable and drinks the bus dry rather than degrading gracefully:
        // 100x base upkeep so two overlapping shields blow through a capacitor in a couple seconds.
        private const float OverloadUpkeepMultiplier = 100f;
        // Tessellation is sized for the per-vertex hex-ripple FX (see ShieldHexFx): the ripple
        // wavelength is ~0.14x the aperture radius, and at the rim the circumferential vertex
        // spacing is 2*pi*R/SegmentCount — 112 segments keeps that above 2 samples per wavelength
        // so the rings read as rings instead of aliasing into shimmer.
        private const int RingCount = 20;
        private const int SegmentCount = 112;
        // Idle glow tracks how much of its requested upkeep the shield is actually getting (see
        // lastUpkeepRatio) — a starved bus visibly dims the field instead of an all-or-nothing cutoff.
        private const float MinIdleAlpha = 0.05f;
        private const float MaxIdleAlpha = 0.2f;

        public int GuidHash { get; private set; }

        private float lastUpkeepRatio;
        private bool interceptedThisTick;
        private bool active = true;
        // Mirrors `active` one tick behind, purely to catch the false->true edge for the startup
        // surge below. Starts equal to `active` so a field that's already on at spawn doesn't pay it.
        private bool wasActive = true;
        // Cached overload verdict, refreshed at 5 Hz (see SimulateFixedUpdateHost).
        private bool fieldOverloaded;
        private float nextOverlapCheck;
        private const float OverlapCheckInterval = 0.2f;
        // Where the fields touch, in vis-mesh local space — the converged alternating-projection
        // point out of FieldsIntersect, captured on the same 5 Hz cadence as the verdict. Feeds
        // the alarm-red overload ripple (refreshed every tick while overloaded, so it doesn't
        // pulse at the check cadence) and the client state stream.
        private Vector3 lastOverloadLocalPos;
        // Host-side state-stream throttle (the flak SyncStateIfNeeded cadence).
        private float nextStateSync;
        private bool lastSentActive = true;
        private float lastSentUpkeep;
        private bool lastSentOverloaded;
        // See agent-besiege-mod-guide.md's "注册时序规范" — retried each tick until it
        // succeeds, since ShipState may not exist yet when this OnSimulateStart runs.
        private bool registered;

        // Client-side generator velocity estimate for the cosmetic interception pass (replica
        // rigidbody velocity is meaningless — vanilla sync drives the transform).
        private Vector3 clientShieldVelocity;
        private Vector3 clientLastPos;
        private bool clientPosValid;

        private GameObject visObject;
        private MeshFilter visMeshFilter;
        private MeshRenderer visRenderer;
        private ShieldHexFx hexFx;
        private float builtRadius = -1f;
        private float builtDepth = -1f;
        private float builtHeight = -1f;
        // Most recent intercept position in vis-mesh local space, streamed to clients so their
        // ripple lands on the same patch of the field (see ShieldNet).
        private Vector3 lastHitLocalPos;
        // Rotating identity keys for host-reported (net) ripples on clients: each state packet's
        // hit gets its own ripple source, up to this many concurrent before the oldest is reused.
        // Local cosmetic intercepts key ripples by projectile instead (see RegisterHitFx); when
        // both report the same physical hit the two ripples just saturate the same patch.
        private static readonly object[] NetHitKeys = { new object(), new object(), new object(), new object() };
        private int netHitKeyIndex;
        // Dedicated ripple-source identities for the overload effect (one sustained source per
        // projector is enough — the state stream only carries one contact point anyway). Static is
        // fine: keys only need to be unique within one projector's own ShieldHexFx source list.
        private static readonly object OverloadFxKey = new object();
        private static readonly object NetOverloadKey = new object();

        public MSlider Radius;
        public MSlider Depth;
        public MSlider Strength;
        public MSlider ColorHue;
        public MSlider Height;
        public MToggle AlwaysVisible;
        public MKey PowerToggle;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Shield Projector";
            Radius = AddSlider("Shield Aperture Radius", "BSShieldRadius", 120f, 20f, 500f);
            Depth = AddSlider("Shield Depth", "BSShieldDepth", 70f, 15f, 250f);
            Height = AddSlider("Shield Height", "BSShieldHeight", 70f, 15f, 250f);
            Strength = AddSlider("Strength", "BSShieldStrength", 1f, 0.2f, 5f);
            ColorHue = AddSlider("Shield Color", "BSShieldColorHue", 0.55f, 0f, 1f);
            AlwaysVisible = AddToggle("Always Show Shield", "BSShieldAlwaysVisible", false);
            PowerToggle = AddKey("Toggle Shield Power", "BSShieldPower", KeyCode.None);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            GuidHash = BlockBehaviour.BuildingBlock.Guid.GetHashCode();
            ShieldNet.Instance.Register(this);
            ShipState ship = OwnShip();
            if (ship != null)
            {
                OnAssignedToShip(ship);
            }
            InitVisual();
        }

        public override void OnAssignedToShip(ShipState ship)
        {
            SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Shields);
            registered = true;
        }

        public override void OnSimulateStop()
        {
            ShieldNet.Instance.Unregister(this);
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Shields);
            }
            registered = false;
            if (visObject != null)
            {
                Destroy(visObject);
                visObject = null;
                hexFx = null;
            }
        }

        // Key is read here (SimulateUpdateHost, not the fixed step) so a tap in between fixed ticks
        // isn't missed — same convention as the other MKey toggles in this codebase.
        public override void SimulateUpdateHost()
        {
            if (PowerToggle.IsPressed)
            {
                active = !active;
            }
        }

        // Local responsiveness for the owner's key tap (the flak/gunner pattern): the host still
        // decides authoritatively — its state heartbeat corrects any divergence within 0.25 s.
        public override void SimulateUpdateClient()
        {
            if (PowerToggle.IsPressed)
            {
                active = !active;
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            ShipState ship = OwnShip();
            if (ship != null && !registered)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Shields);
                registered = true;
            }
            if (ship == null)
            {
                return;
            }

            if (!active)
            {
                lastUpkeepRatio = 0f;
                interceptedThisTick = false;
                // No field while powered off — also clears a stale verdict so re-enabling doesn't
                // pay (or show) up to 0.2 s of phantom overload from the 5 Hz cache.
                fieldOverloaded = false;
                UpdateVisual();
                SyncStateIfNeeded();
                wasActive = false;
                return;
            }

            float upkeepCost = UpkeepBase + UpkeepPerVolume * Radius.Value * Depth.Value;
            if (!wasActive)
            {
                ship.Energy.Request(EnergyBus.Shield, upkeepCost * Time.fixedDeltaTime * StartupUpkeepTicks);
                wasActive = true;
            }
            // Overload verdict refreshed at 5 Hz (the exact intersection test doesn't need
            // per-tick freshness — ships don't close 0.2 s worth of distance meaningfully),
            // then applied every tick from the cache.
            if (Time.time >= nextOverlapCheck)
            {
                nextOverlapCheck = Time.time + OverlapCheckInterval;
                Vector3 contactWorld;
                fieldOverloaded = OverlapsForeignShield(ship, out contactWorld);
                if (fieldOverloaded)
                {
                    lastOverloadLocalPos = visObject.transform.InverseTransformPoint(contactWorld);
                }
            }
            if (fieldOverloaded)
            {
                upkeepCost *= OverloadUpkeepMultiplier;
                // Refresh the alarm-red ripple every tick (not just at the 5 Hz verdict) so it
                // burns steadily instead of pulsing at the check cadence.
                if (hexFx != null)
                {
                    hexFx.RegisterHit(OverloadFxKey, lastOverloadLocalPos, true);
                }
            }
            lastUpkeepRatio = ship.Energy.Request(EnergyBus.Shield, upkeepCost * Time.fixedDeltaTime);
            float effectiveStrength = Strength.Value * lastUpkeepRatio;
            interceptedThisTick = false;
            // Reference for the relative-speed gate below: the shield generator's own motion
            // (≈ ship velocity). Gate on speed relative to this, but decelerate absolute speed toward
            // the absolute HyperVelocityThreshold floor — see ADR 0006.
            Vector3 shieldVelocity = Body != null ? Body.velocity : Vector3.zero;

            IList<ITrackable> targets = SpaceCombatRegistry.Trackables;
            for (int i = 0; i < targets.Count; i++)
            {
                MissileProjectile missile = targets[i] as MissileProjectile;
                if (missile != null)
                {
                    // Check current position (keeps decelerating every tick the missile is actually
                    // inside) and the next-tick predicted position (catches fast entries the same
                    // tick they'd otherwise cross the field), so slow-moving intercepts aren't missed
                    // either way. The speed gate is the missile's speed RELATIVE to the shield
                    // generator (ADR 0006): like a round it's bled only while closing faster than the
                    // threshold, and only down to that same floor — the field no longer stops it dead.
                    Vector3 missilePredictedPos = missile.Position + missile.Velocity * Time.fixedDeltaTime;
                    if (!missile.IsAlive || (missile.Velocity - shieldVelocity).magnitude <= HyperVelocityThreshold ||
                        (!Contains(missile.Position) && !Contains(missilePredictedPos)))
                    {
                        continue;
                    }

                    float missileDeltaV = Decelerate(ship, effectiveStrength, missile.Velocity, missile.ThreatMass, HyperVelocityThreshold);
                    if (missileDeltaV > 0f)
                    {
                        float missileSpeed = missile.Velocity.magnitude;
                        Vector3 missileNewVelocity = missile.Velocity * ((missileSpeed - missileDeltaV) / missileSpeed);
                        missile.ApplyShieldDeceleration(missileNewVelocity, missileDeltaV);
                        interceptedThisTick = true;
                        RegisterHitFx(missile, missile.Position);
                    }
                    continue;
                }

                SpaceKineticRound round = targets[i] as SpaceKineticRound;
                if (round != null)
                {
                    // Eligible only while the round's speed RELATIVE to the shield generator exceeds
                    // HyperVelocityThreshold (ADR 0006); the field caps that closing speed at the
                    // threshold, it doesn't fully stop the round.
                    Vector3 roundPredictedPos = round.Position + round.Velocity * Time.fixedDeltaTime;
                    if (!round.IsAlive || (round.Velocity - shieldVelocity).magnitude <= HyperVelocityThreshold ||
                        (!Contains(round.Position) && !Contains(roundPredictedPos)))
                    {
                        continue;
                    }

                    float roundDeltaV = Decelerate(ship, effectiveStrength, round.Velocity, round.MassEstimate, HyperVelocityThreshold);
                    if (roundDeltaV > 0f)
                    {
                        float roundSpeed = round.Velocity.magnitude;
                        Vector3 roundNewVelocity = round.Velocity * ((roundSpeed - roundDeltaV) / roundSpeed);
                        round.ApplyShieldDeceleration(roundNewVelocity, roundDeltaV);
                        interceptedThisTick = true;
                        RegisterHitFx(round, round.Position);
                    }
                }
            }

            UpdateVisual();
            SyncStateIfNeeded();
        }

        // Host → clients state stream (flak cadence: 0.08 s while something visible is happening,
        // 0.25 s heartbeat otherwise; the heartbeat always sends, so a lost packet self-heals).
        private void SyncStateIfNeeded()
        {
            if (!StatMaster.isMP || Time.time < nextStateSync)
            {
                return;
            }
            bool changed = active != lastSentActive || Mathf.Abs(lastUpkeepRatio - lastSentUpkeep) > 0.05f ||
                fieldOverloaded != lastSentOverloaded;
            // Overload keeps the fast cadence: the client's red ripple source only stays alive
            // (FadeAfterHit) while these packets keep refreshing it.
            nextStateSync = Time.time + ((interceptedThisTick || fieldOverloaded || changed) ? 0.08f : 0.25f);
            lastSentActive = active;
            lastSentUpkeep = lastUpkeepRatio;
            lastSentOverloaded = fieldOverloaded;
            ModNetworking.SendToAll(ShieldNet.StateMsg.CreateMessage(
                PlayerID, GuidHash, active, lastUpkeepRatio, interceptedThisTick, lastHitLocalPos,
                fieldOverloaded, lastOverloadLocalPos));
        }

        // Host StateMsg landed on this client replica: feed the values UpdateVisual and
        // ClientIntercept read. Host-side intercepts drop a hex ripple at the streamed local
        // position (local cosmetic intercepts register their own ripples in ClientIntercept,
        // keyed by projectile — a double report of the same hit just saturates the same patch).
        // The overload flag has no client-side mirror at all (the gate is host-only), so the
        // alarm-red ripple here is purely stream-driven: one sustained source, kept alive by the
        // fast sync cadence refreshing NetOverloadKey.
        public void ReceiveState(bool netActive, float upkeepRatio, bool intercepted, Vector3 hitLocalPos,
            bool overloaded, Vector3 overloadLocalPos)
        {
            active = netActive;
            lastUpkeepRatio = upkeepRatio;
            if (hexFx == null)
            {
                return;
            }
            if (intercepted)
            {
                hexFx.RegisterHit(NetHitKeys[netHitKeyIndex], hitLocalPos);
                netHitKeyIndex = (netHitKeyIndex + 1) % NetHitKeys.Length;
            }
            if (overloaded)
            {
                hexFx.RegisterHit(NetOverloadKey, overloadLocalPos, true);
            }
        }

        // Client-side ticking: SimulateFixedUpdateHost never runs here, so run the cosmetic
        // interception pass and the same UpdateVisual off the received state. Sliders/toggles are
        // vanilla-synced, so the mesh rebuild reads identical dimensions on every machine.
        private void FixedUpdate()
        {
            if (BlockBehaviour == null || !BlockBehaviour.isSimulating || !StatMaster.isClient)
            {
                return;
            }
            // Replica rigidbody velocity is not meaningful (vanilla sync moves the transform), so
            // estimate the generator's motion from its own transform for the relative-speed gate.
            Vector3 pos = transform.position;
            clientShieldVelocity = clientPosValid ? (pos - clientLastPos) / Time.fixedDeltaTime : Vector3.zero;
            clientLastPos = pos;
            clientPosValid = true;

            ClientIntercept();
            UpdateVisual();
        }

        // Cosmetic interception on clients (MP rule: display and smoothing — every round/missile
        // here is a visual mirror, so bleeding its speed is smoothing it toward what the host's
        // authoritative field is doing to the real one). Same gates and deceleration math as the
        // host loop; the synced upkeepRatio stands in for energy (it already carries starvation
        // and the overload drain), so no bus is ever charged here. Missiles additionally get
        // corrected by their 10 Hz state stream; rounds live or die on this pass alone.
        private void ClientIntercept()
        {
            float effectiveStrength = Strength.Value * lastUpkeepRatio;
            if (!active || effectiveStrength <= 0f || OwnShip() == null)
            {
                return;
            }

            IList<ITrackable> targets = SpaceCombatRegistry.Trackables;
            for (int i = 0; i < targets.Count; i++)
            {
                MissileProjectile missile = targets[i] as MissileProjectile;
                if (missile != null)
                {
                    Vector3 missilePredictedPos = missile.Position + missile.Velocity * Time.fixedDeltaTime;
                    if (!missile.IsAlive || (missile.Velocity - clientShieldVelocity).magnitude <= HyperVelocityThreshold ||
                        (!Contains(missile.Position) && !Contains(missilePredictedPos)))
                    {
                        continue;
                    }

                    float missileDeltaV = DesiredDeltaV(effectiveStrength, missile.Velocity.magnitude, HyperVelocityThreshold);
                    if (missileDeltaV > 0f)
                    {
                        float missileSpeed = missile.Velocity.magnitude;
                        missile.ApplyShieldDeceleration(missile.Velocity * ((missileSpeed - missileDeltaV) / missileSpeed), missileDeltaV);
                        RegisterHitFx(missile, missile.Position);
                    }
                    continue;
                }

                SpaceKineticRound round = targets[i] as SpaceKineticRound;
                if (round != null)
                {
                    Vector3 roundPredictedPos = round.Position + round.Velocity * Time.fixedDeltaTime;
                    if (!round.IsAlive || (round.Velocity - clientShieldVelocity).magnitude <= HyperVelocityThreshold ||
                        (!Contains(round.Position) && !Contains(roundPredictedPos)))
                    {
                        continue;
                    }

                    float roundDeltaV = DesiredDeltaV(effectiveStrength, round.Velocity.magnitude, HyperVelocityThreshold);
                    if (roundDeltaV > 0f)
                    {
                        float roundSpeed = round.Velocity.magnitude;
                        round.ApplyShieldDeceleration(round.Velocity * ((roundSpeed - roundDeltaV) / roundSpeed), roundDeltaV);
                        RegisterHitFx(round, round.Position);
                    }
                }
            }
        }

        // Bounding sphere of the field volume — BROAD PHASE ONLY now. A sphere centered on the
        // axis midpoint reaching both the apex and the far disc rim fully encloses the paraboloid,
        // so a miss here is a definitive miss; a hit hands over to the exact narrow phase. (The
        // old sphere-only overload gate flagged fields as "overlapping" while visually ~R meters
        // apart — the MP playtest's no-combat energy drain.)
        private void FieldBounds(out Vector3 center, out float radius)
        {
            Vector3 apex = transform.position + Height.Value * transform.forward;
            center = apex - transform.forward * (Depth.Value * 0.5f);
            radius = Mathf.Sqrt(Depth.Value * Depth.Value * 0.25f + Radius.Value * Radius.Value);
        }

        // Overload gate (host-only): any other ship's active shield field that GENUINELY
        // intersects this one trips it, regardless of IFF — the overlap itself is what makes the
        // field unstable, not who owns the other end. On a hit, contactWorld is the point where
        // the two fields touch (out of FieldsIntersect's converged projection) — the anchor for
        // the alarm-red overload ripple.
        private bool OverlapsForeignShield(ShipState ownShip, out Vector3 contactWorld)
        {
            // A starved field (bus out of energy) is toggled on but projects nothing — same as
            // powered off for this purpose. Gate on last-known upkeep ratio, not just the on/off
            // key, so a dead shield can't trip a genuine one into overload.
            if (lastUpkeepRatio <= 0f)
            {
                contactWorld = Vector3.zero;
                return false;
            }
            foreach (ShipState otherShip in SpaceCombatRegistry.Ships)
            {
                if (otherShip == ownShip)
                {
                    continue;
                }
                List<ShieldProjectorBlock> otherShields = otherShip.Shields;
                for (int i = 0; i < otherShields.Count; i++)
                {
                    ShieldProjectorBlock other = otherShields[i];
                    if (other == null || other == this || !other.active || other.lastUpkeepRatio <= 0f)
                    {
                        continue;
                    }
                    if (FieldsIntersect(this, other, out contactWorld))
                    {
                        return true;
                    }
                }
            }
            contactWorld = Vector3.zero;
            return false;
        }

        // Exact intersection between two shield fields. Load-bearing fact: the field volume is
        // CONVEX — the meridian profile r = R·√(a/D) is concave, so the solid of revolution under
        // it (bowl surface + flat rim disc) is a convex body. Two convex bodies intersect iff
        // alternating projection (ping-ponging a point onto each body's closest point in turn)
        // collapses the gap: intersecting → the point walks into the common region; disjoint → the
        // gap converges to the true separation and stays > epsilon. 24 rounds of two cheap
        // closest-point solves — sub-microsecond, comfortably within the 5 Hz refresh budget.
        // Verified against brute-force volume sampling over 425 fixed + randomized
        // configurations: zero false positives (the failure mode that mattered — the old sphere
        // gate's phantom overloads), one residual false negative on a razor-thin grazing contact
        // (alternating projection converges slowly there; the miss self-corrects as ships close).
        private const int IntersectIterations = 24;
        private const float IntersectEpsilon = 1f; // residual gap (m) still counted as touching

        // On success, contactWorld is the converged projection point — inside (or within epsilon
        // of) both volumes, i.e. where the fields actually touch.
        private static bool FieldsIntersect(ShieldProjectorBlock a, ShieldProjectorBlock b, out Vector3 contactWorld)
        {
            Vector3 centerA, centerB;
            float radiusA, radiusB;
            a.FieldBounds(out centerA, out radiusA);
            b.FieldBounds(out centerB, out radiusB);
            float reach = radiusA + radiusB;
            if ((centerA - centerB).sqrMagnitude > reach * reach)
            {
                contactWorld = Vector3.zero;
                return false;
            }

            Vector3 point = centerB; // axis midpoint — always inside B's volume
            for (int i = 0; i < IntersectIterations; i++)
            {
                Vector3 onA = a.ClosestPointInField(point);
                Vector3 onB = b.ClosestPointInField(onA);
                if ((onB - onA).sqrMagnitude <= IntersectEpsilon * IntersectEpsilon)
                {
                    contactWorld = (onA + onB) * 0.5f;
                    return true;
                }
                point = onB;
            }
            contactWorld = Vector3.zero;
            return false;
        }

        // Closest point of this field's volume to `world` (returns `world` unchanged when it is
        // already inside). Axisymmetry reduces it to 2D in the meridian plane through the query
        // point: region ρ ≤ R·√(a/D), 0 ≤ a ≤ D. For outside points the answer lies on the
        // boundary — either the rim disc (a = D) or the bowl surface; the surface distance is
        // minimized over its natural parameter t (a = D·t², ρ = R·t) with a few clamped Newton
        // steps on the quartic plus both endpoints as safety candidates. Precision only needs to
        // beat the intersection test's 1 m epsilon.
        private Vector3 ClosestPointInField(Vector3 world)
        {
            float depth = Mathf.Max(0.001f, Depth.Value);
            float radius = Mathf.Max(0.001f, Radius.Value);
            Vector3 apex = transform.position + Height.Value * transform.forward;
            Vector3 axis = -transform.forward;
            Vector3 delta = world - apex;
            float a = Vector3.Dot(delta, axis);
            Vector3 radial = delta - a * axis;
            float rho = radial.magnitude;

            if (a >= 0f && a <= depth && rho * rho <= radius * radius * a / depth)
            {
                return world;
            }

            Vector3 radialDir;
            if (rho > 0.001f)
            {
                radialDir = radial / rho;
            }
            else
            {
                // On-axis query: any meridian plane works, the candidates below land on the axis
                // or the disc center anyway.
                radialDir = Vector3.Cross(axis, Vector3.up);
                if (radialDir.sqrMagnitude < 0.001f)
                {
                    radialDir = Vector3.Cross(axis, Vector3.right);
                }
                radialDir.Normalize();
            }

            float bestSq = float.MaxValue;
            float bestA = 0f;
            float bestRho = 0f;

            // Candidate: rim disc (flat cap at a = D, ρ ≤ R).
            float discRho = Mathf.Min(rho, radius);
            float discSq = (depth - a) * (depth - a) + (discRho - rho) * (discRho - rho);
            if (discSq < bestSq)
            {
                bestSq = discSq;
                bestA = depth;
                bestRho = discRho;
            }

            // Candidate: bowl surface — minimize F(t) = (D·t² − a)² + (R·t − ρ)², t ∈ [0, 1].
            float t = Mathf.Clamp01(rho / radius);
            for (int i = 0; i < 4; i++)
            {
                float f1 = 4f * depth * t * (depth * t * t - a) + 2f * radius * (radius * t - rho);
                float f2 = 12f * depth * depth * t * t - 4f * depth * a + 2f * radius * radius;
                if (Mathf.Abs(f2) < 0.0001f)
                {
                    break;
                }
                t = Mathf.Clamp01(t - f1 / f2);
            }
            SurfaceCandidate(t, depth, radius, a, rho, ref bestSq, ref bestA, ref bestRho);
            SurfaceCandidate(0f, depth, radius, a, rho, ref bestSq, ref bestA, ref bestRho); // apex
            SurfaceCandidate(1f, depth, radius, a, rho, ref bestSq, ref bestA, ref bestRho); // rim edge

            return apex + bestA * axis + bestRho * radialDir;
        }

        private static void SurfaceCandidate(float t, float depth, float radius, float a, float rho,
            ref float bestSq, ref float bestA, ref float bestRho)
        {
            float candidateA = depth * t * t;
            float candidateRho = radius * t;
            float dSq = (candidateA - a) * (candidateA - a) + (candidateRho - rho) * (candidateRho - rho);
            if (dSq < bestSq)
            {
                bestSq = dSq;
                bestA = candidateA;
                bestRho = candidateRho;
            }
        }

        private bool Contains(Vector3 position)
        {
            Vector3 delta = position - (transform.position + Height.Value * transform.forward);
            float a = Vector3.Dot(delta, -transform.forward);
            if (a < 0f || a > Depth.Value)
            {
                return false;
            }
            float r2 = delta.sqrMagnitude - a * a;
            float maxR2 = Radius.Value * Radius.Value * a / Depth.Value;
            return r2 <= maxR2;
        }

        // minSpeed is the ABSOLUTE speed this threat may be bled down to but not past in one tick —
        // now the HyperVelocityThreshold for both kinetic rounds and missiles (ADR 0006). The field
        // shaves the hyper-velocity off down to the threshold and lets the round/missile reach the
        // hull for its reduced velocity-damage rather than deleting it, matching "slow/weaken, don't
        // necessarily fully block". The eligibility gate (see the caller) is on RELATIVE speed while
        // this floor is absolute, so a threat whose absolute speed is already below the floor is a
        // no-op here even if its closing speed cleared the gate.
        private float Decelerate(ShipState ship, float effectiveStrength, Vector3 velocity, float mass, float minSpeed)
        {
            mass = Mathf.Max(0.05f, mass);
            float speed = velocity.magnitude;
            float desiredDeltaV = DesiredDeltaV(effectiveStrength, speed, minSpeed);
            if (desiredDeltaV <= 0f)
            {
                return 0f;
            }

            // Price the deceleration on the Shield bus by the velocity-damage it removes from the
            // round, using the exact kinetic-damage coefficient the weapon paid to put there —
            // see SpaceBalance. vAfter = speed - desiredDeltaV, so the bracket is the (v_before^2 -
            // v_after^2) worth of potential damage the round no longer carries. This makes a
            // hyper-velocity intercept a genuine capacitor-draining burst (block enough per second
            // and the Shield bus can't keep up — the round leaks through), instead of the near-free
            // deletion the old flat deltaV cost allowed.
            float vAfter = speed - desiredDeltaV;
            float damageRemoved = mass * (speed * speed - vAfter * vAfter) * SpaceBalance.KineticDamagePerKE;
            float energyCost = SpaceBalance.ShieldEnergyPerDamage * damageRemoved;
            float ratio = ship.Energy.Request(EnergyBus.Shield, energyCost);
            return desiredDeltaV * ratio;
        }

        // The un-priced deceleration this field wants to apply per tick — shared between the host
        // loop (which then charges the Shield bus and scales by the granted ratio) and the client's
        // cosmetic pass (which uses it as-is: the synced upkeepRatio inside effectiveStrength is
        // the client's stand-in for energy).
        private static float DesiredDeltaV(float effectiveStrength, float speed, float minSpeed)
        {
            float slowable = speed - minSpeed;
            if (slowable <= 0f)
            {
                return 0f;
            }
            float decelAccel = DecelCoefficient * effectiveStrength * speed;
            return Mathf.Min(slowable, decelAccel * Time.fixedDeltaTime);
        }

        private void InitVisual()
        {
            visObject = new GameObject("BS Shield Field Vis");
            visObject.transform.SetParent(transform);
            visObject.transform.localPosition = Vector3.zero;
            // Mesh is generated along local +Z (see RebuildMesh); rotate 180° so it points along
            // -transform.forward in world space, matching Contains's flipped axis above.
            visObject.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            visObject.transform.localScale = Vector3.one;
            visMeshFilter = visObject.AddComponent<MeshFilter>();
            visRenderer = visObject.AddComponent<MeshRenderer>();
            visRenderer.material = new Material(Shader.Find("Particles/Additive"));
            // Shader-neutral tint (the shader doubles it): hue and intensity now ride entirely in
            // per-vertex colors (ShieldHexFx) so a hit can light a local patch instead of tinting
            // the whole mesh; the hex-cell pattern rides in the texture.
            visRenderer.material.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            visRenderer.material.mainTexture = ShieldHexFx.GetHexTexture();
            visRenderer.enabled = false;
            RebuildMesh();
            PlaceMesh();
            hexFx = visObject.AddComponent<ShieldHexFx>();
            hexFx.Configure(visMeshFilter, Radius.Value);
        }

        // key identifies the projectile so a round being bled over many consecutive ticks keeps
        // refreshing (and moving) one ripple source instead of spawning a new ripple per tick.
        private void RegisterHitFx(object key, Vector3 worldPos)
        {
            if (hexFx == null)
            {
                return;
            }
            Vector3 local = visObject.transform.InverseTransformPoint(worldPos);
            hexFx.RegisterHit(key, local);
            lastHitLocalPos = local;
        }
        private void PlaceMesh()
        {
            visObject.transform.localPosition = new Vector3(0f, 0f, Height.Value);
            builtHeight = Height.Value;
        }

        private void RebuildMesh()
        {
            builtRadius = Radius.Value;
            builtDepth = Depth.Value;
            

            List<Vector3> vertices = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<int> triangles = new List<int>();

            for (int ring = 0; ring <= RingCount; ring++)
            {
                float a = builtDepth * ring / RingCount;
                float r = builtRadius * Mathf.Sqrt(a / builtDepth);
                for (int seg = 0; seg < SegmentCount; seg++)
                {
                    float angle = seg * Mathf.PI * 2f / SegmentCount;
                    float px = r * Mathf.Cos(angle);
                    float py = r * Mathf.Sin(angle);
                    vertices.Add(new Vector3(px, py, a));
                    // Planar (front) projection: hexagons stretch a little on the steep rim but
                    // stay seam-free, unlike a polar mapping.
                    uvs.Add(new Vector2(px / (builtRadius * 2f) + 0.5f, py / (builtRadius * 2f) + 0.5f));
                }
            }

            for (int ring = 0; ring < RingCount; ring++)
            {
                for (int seg = 0; seg < SegmentCount; seg++)
                {
                    int nextSeg = (seg + 1) % SegmentCount;
                    int a0 = ring * SegmentCount + seg;
                    int a1 = ring * SegmentCount + nextSeg;
                    int b0 = (ring + 1) * SegmentCount + seg;
                    int b1 = (ring + 1) * SegmentCount + nextSeg;

                    AddQuad(triangles, a0, a1, b1, b0);
                }
            }

            Mesh mesh = new Mesh();
            mesh.vertices = vertices.ToArray();
            mesh.uv = uvs.ToArray();
            mesh.triangles = triangles.ToArray();
            // Both winding orders share vertices, so normals average toward zero here — harmless
            // under the unlit Particles/Additive material, but would render black under a lit shader.
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            visMeshFilter.sharedMesh = mesh;
            // The planar UVs above span the aperture diameter; tile the hex texture so a cell
            // keeps a fixed world size regardless of the configured radius.
            Vector2 hexWorld = ShieldHexFx.HexTextureWorldSize();
            visRenderer.material.mainTextureScale = new Vector2(
                builtRadius * 2f / hexWorld.x, builtRadius * 2f / hexWorld.y);
            if (hexFx != null)
            {
                hexFx.OnMeshChanged(builtRadius);
            }
        }

        private static void AddQuad(List<int> triangles, int a, int b, int c, int d)
        {
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
            triangles.Add(a);
            triangles.Add(c);
            triangles.Add(d);
            triangles.Add(a);
            triangles.Add(c);
            triangles.Add(b);
            triangles.Add(a);
            triangles.Add(d);
            triangles.Add(c);
        }

        private void UpdateVisual()
        {
            if (visRenderer == null)
            {
                return;
            }

            if (Mathf.Abs(Radius.Value - builtRadius) > 0.1f || Mathf.Abs(Depth.Value - builtDepth) > 0.1f)
            {
                RebuildMesh();
            }
            if (Mathf.Abs(Height.Value - builtHeight) > 0.1f)
            {
                PlaceMesh();
            }

            if (!active)
            {
                visRenderer.enabled = false;
                return;
            }

            // Hits no longer tint the whole mesh: ShieldHexFx paints a local hex ripple per
            // decelerated projectile into the vertex colors, on top of this idle base glow. With
            // AlwaysVisible off the base is zero and only the ripple patches show.
            float baseAlpha = AlwaysVisible.IsActive ? Mathf.Lerp(MinIdleAlpha, MaxIdleAlpha, lastUpkeepRatio) : 0f;
            if (hexFx != null)
            {
                hexFx.SetBase(Color.HSVToRGB(ColorHue.Value, 1f, 1f), baseAlpha);
            }
            visRenderer.enabled = baseAlpha > 0.001f || (hexFx != null && hexFx.HasActiveFx);
        }
    }
}
