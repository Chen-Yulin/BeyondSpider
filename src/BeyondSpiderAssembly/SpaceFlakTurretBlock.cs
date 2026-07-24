using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class SpaceFlakTurretNet : SingleInstance<SpaceFlakTurretNet>
    {
        public override string Name { get { return "BeyondSpider Space Flak Turret Net"; } }

        public static MessageType ActiveMsg = ModNetworking.CreateMessageType(DataType.Integer, DataType.Integer, DataType.Boolean);
        public static MessageType StateMsg = ModNetworking.CreateMessageType(DataType.Integer, DataType.Integer, DataType.Single, DataType.Single, DataType.Boolean);
        public static MessageType ShotMsg = ModNetworking.CreateMessageType(DataType.Integer, DataType.Integer, DataType.Vector3, DataType.Vector3, DataType.Vector3, DataType.Single, DataType.Single, DataType.Single);

        private readonly Dictionary<string, SpaceFlakTurretBlock> turrets = new Dictionary<string, SpaceFlakTurretBlock>();

        public void Register(SpaceFlakTurretBlock turret)
        {
            string key = Key(turret.PlayerID, turret.GuidHash);
            if (!turrets.ContainsKey(key))
            {
                turrets.Add(key, turret);
            }
            else
            {
                turrets[key] = turret;
            }
        }

        public void Unregister(SpaceFlakTurretBlock turret)
        {
            turrets.Remove(Key(turret.PlayerID, turret.GuidHash));
        }

        public void ActiveReceiver(Message msg)
        {
            SpaceFlakTurretBlock turret = Find((int)msg.GetData(0), (int)msg.GetData(1));
            if (turret != null)
            {
                turret.ReceiveActive((bool)msg.GetData(2));
            }
        }

        public void StateReceiver(Message msg)
        {
            SpaceFlakTurretBlock turret = Find((int)msg.GetData(0), (int)msg.GetData(1));
            if (turret != null)
            {
                turret.ReceiveNetworkState((float)msg.GetData(2), (float)msg.GetData(3), (bool)msg.GetData(4));
            }
        }

        public void ShotReceiver(Message msg)
        {
            SpaceFlakTurretBlock turret = Find((int)msg.GetData(0), (int)msg.GetData(1));
            if (turret != null)
            {
                turret.ReceiveLargeShot((Vector3)msg.GetData(2), (Vector3)msg.GetData(3), (Vector3)msg.GetData(4), (float)msg.GetData(5), (float)msg.GetData(6), (float)msg.GetData(7));
            }
        }

        private SpaceFlakTurretBlock Find(int playerId, int guid)
        {
            SpaceFlakTurretBlock turret;
            turrets.TryGetValue(Key(playerId, guid), out turret);
            return turret;
        }

        private static string Key(int playerId, int guid)
        {
            return playerId.ToString() + ":" + guid.ToString();
        }
    }

    public class SpaceFlakTurretBlock : SpaceBlock
    {
        public MMenu Type;
        public MKey SwitchActive;
        public MToggle DefaultActive;
        public MSlider EnergyPerSecond;
        public MFireChannel FireChannel;
        public MLimits YawLimit;
        public MInfo CaliberInfo;
        public MInfo TrackingSpeedInfo;
        public MInfo MuzzleVelocityInfo;
        public MInfo RangeInfo;

        public int GuidHash { get; private set; }

        // See agent-besiege-mod-guide.md's "注册时序规范" — retried each tick until it
        // succeeds, since ShipState may not exist yet when this OnSimulateStart runs.
        private bool registered;

        private const float TracerLengthScale = 3f;
        private const float MinScaleComponent = 0.05f;

        // Tracking speed, muzzle velocity and range used to be three independent player-set
        // MSliders. Only Energy Use remains a slider; the other three are now derived and shown
        // read-only via MInfo, recomputed live in build mode — same pattern as
        // RailgunBarrelBlock.RecomputeBallistics/SuperCapacitorBlock.RecomputeCapacity (see
        // agent-besiege-mod-guide.md's "扳手菜单里的只读信息:MInfo"). Caliber itself is now also
        // derived rather than fixed per Type — see RecomputeStats — so tracking speed/muzzle
        // velocity/range all inherit that scale dependency for free by staying downstream of it.
        // Tracking speed follows caliber alone (a thicker/heavier mount slews slower); muzzle
        // velocity follows caliber and Energy Use (spending more power drives the round faster);
        // range follows muzzle velocity at a fixed flight-time budget, so it does not need its
        // own Energy Use term.
        private const float TrackingSpeedCaliberCoefficient = 120f;
        private const float TrackingSpeedMin = 0.2f;
        private const float TrackingSpeedMax = 6f;
        private const float MuzzleVelocityBase = 190f;
        private const float MuzzleVelocityCaliberCoefficient = 42f;
        private const float MuzzleVelocityEnergyCoefficient = 0.5f;
        private const float MuzzleVelocityMin = 280f;
        private const float MuzzleVelocityMax = 1000f;
        private const float RangeFlightTimeBudget = 1.6f;
        private const float RangeMin = 80f;
        private const float RangeMax = 1800f;

        // Index must match SpaceFlakTurretAssets' per-type arrays (BaseCaliber/GunCount/offsets/
        // mesh+texture names) — this replaced the old 13-entry caliber x barrel-count preset
        // list (each entry its own WW2-sourced mesh pair) now that all three models are custom
        // PaoZuo/PaoTa/PaoGuan sets instead of migrated WW2 assets.
        private readonly List<string> typeList = new List<string>
        {
            "单管火炮",
            "多管火炮",
            "近防炮"
        };

        private GameObject originVis;
        private GameObject buildBase;
        private GameObject simRoot;
        private GameObject mountObject;
        private GameObject baseObject;
        private GameObject gunObject;
        private Transform yawTransform;
        private Transform pitchTransform;
        private ParticleSystem smallShoot;

        private bool active;
        private bool hasTarget;
        // The target of the solution this turret is currently steering at (whichever channel
        // TrySelectSolution picked — channel 0's auto air-defence lock or a player-assigned
        // channel, ADR-0010/ADR-0012). ApplyParticleDamage settles the small-caliber stream
        // against THIS, the object the barrel is actually pointing at right now.
        private ITrackable engagedTarget;
        private bool shooting;
        private float caliber = 20f;
        private float trackingSpeed = 1.5f;
        private float muzzleVelocity = 460f;
        private float range = 460f;
        private int gunCount = 1;
        private float gunWidth = 0.1f;
        private float particleRate = 15f;
        private float targetYaw;
        private float targetPitch;
        private float currentYaw;
        private float currentPitch;
        private float realYaw;
        private float realPitch;
        private float errorAngle;
        private float errorDistance;
        private Vector2 randomError;
        private float reload;
        private float reloadTime = 0.12f;
        private bool alternateMuzzle;
        private int previousType = -1;
        private bool previousShowCluster;
        private bool previousSkinEnabled;
        private float nextStateSync;
        private bool previousShooting;
        // Cached delegate for FireChannels.TrySelectSolution's per-candidate 射界 test, so the
        // per-tick channel walk doesn't allocate a fresh delegate every FixedUpdate.
        private FireChannels.ArcCheck channelArcCheck;
        // This turret's personal channel-0 lottery ticket (ADR-0014): which of the ship's up-to-4
        // channel-0 threats it engages, sticky until that target drops off the list.
        private readonly FireChannelAssignment channel0Assignment = new FireChannelAssignment();

        // Rate feed-forward, ADR-0016 (same fix as the gunner's DriveHinges). Two 0.2-per-tick
        // ease stages sit between the computed target angles and the muzzle: StepAngle's ease
        // zone lags a moving setpoint by (1-0.2)/0.2 = 4 ticks of its rate, and the currentYaw/
        // currentPitch pose lerp adds 4 more — so while tracking, the barrel trails the computed
        // lead by 8 ticks of target-angle rate. Leading the commanded angles by exactly that
        // cancels the mean lag. The rate is measured on the RAW solution angles (never on the
        // led ones — that would feed back), clamped to trackingSpeed (leading faster than the
        // mount can slew is meaningless), and reset on target change, lost target, or a jump.
        private const float AngleLagTicks = 8f;
        private const float AngleRateSmoothing = 0.25f;
        private const float AngleRateJumpResetDegrees = 5f;

        private bool hasAngleHistory;
        private Vector2 prevRawTargetAngles;
        private Vector2 targetAngleRate; // deg per tick, smoothed
        private ITrackable angleRateTarget;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Space Flak Turret";
            SwitchActive = AddKey("Switch Active", "BSSpaceFlakSwitch", KeyCode.None);
            Type = AddMenu("AA Type", 0, typeList, false);
            DefaultActive = AddToggle("Default Active", "BSSpaceFlakActive", true);
            EnergyPerSecond = AddSlider("Energy Use", "BSSpaceFlakEnergy", 125f, 10f, 900f);
            FireChannel = AddFireChannel("Fire Channels", "BSSpaceFlakFireChannel");
            channelArcCheck = IsAimPointInArc;
            YawLimit = AddLimits("Turret Orien Limit", "BSSpaceFlakYawLimit", 90f, 90f, 180f, new FauxTransform(new Vector3(0f, -0.5f, -0.5f), Quaternion.Euler(-90f, 0f, 0f), Vector3.one * 0.0001f));
            CaliberInfo = AddInfo("Caliber", "BSSpaceFlakCaliberInfo");
            TrackingSpeedInfo = AddInfo("Tracking Speed", "BSSpaceFlakTrackingInfo");
            MuzzleVelocityInfo = AddInfo("Muzzle Velocity", "BSSpaceFlakVelocityInfo");
            RangeInfo = AddInfo("Range", "BSSpaceFlakRangeInfo");
            InitBuildModel();
        }

        public void Start()
        {
            gameObject.name = "BeyondSpider Space Flak Turret";
        }

        public override void BuildingUpdate()
        {
            HoldAppearance(false);
            RecomputeStats();
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            GuidHash = BlockBehaviour.BuildingBlock.Guid.GetHashCode();
            active = DefaultActive.IsActive;
            channel0Assignment.Clear();
            hasAngleHistory = false;
            targetAngleRate = Vector2.zero;
            angleRateTarget = null;
            ResolveType();
            originVis = transform.Find("Vis") == null ? null : transform.Find("Vis").gameObject;
            InitSimulationModel();
            HoldAppearance(true);
            SpaceFlakTurretNet.Instance.Register(this);

            ShipState ship = OwnShip();
            if (ship != null)
            {
                OnAssignedToShip(ship);
            }

            if (!StatMaster.isClient)
            {
                Transform colliders = transform.Find("Colliders");
                if (colliders != null && colliders.childCount > 0)
                {
                    BoxCollider box = colliders.GetChild(0).GetComponent<BoxCollider>();
                    if (box != null)
                    {
                        box.isTrigger = true;
                    }
                }
            }
        }

        public override void OnAssignedToShip(ShipState ship)
        {
            SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.FlakTurrets);
            registered = true;
        }

        public override void OnSimulateStop()
        {
            SpaceFlakTurretNet.Instance.Unregister(this);
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.FlakTurrets);
            }
            registered = false;

            SpaceFlakTurretBlock buildBlock = BlockBehaviour.BuildingBlock == null ? null : BlockBehaviour.BuildingBlock.GetComponent<SpaceFlakTurretBlock>();
            if (buildBlock != null)
            {
                buildBlock.ResolveType();
                buildBlock.HoldAppearance(false);
            }
        }

        public override void SimulateUpdateAlways()
        {
            HoldAppearance(true);
        }

        public override void SimulateUpdateHost()
        {
            if (SwitchActive.IsPressed)
            {
                active = !active;
                SendActive();
            }
        }

        public override void SimulateUpdateClient()
        {
            if (SwitchActive.IsPressed)
            {
                active = !active;
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            if (!registered)
            {
                ShipState ship = OwnShip();
                if (ship != null)
                {
                    SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.FlakTurrets);
                    registered = true;
                }
            }

            UpdateFireControlTarget();
            DriveTurret();
            SyncStateIfNeeded();
        }

        private void FixedUpdate()
        {
            if (BlockBehaviour != null && BlockBehaviour.isSimulating && StatMaster.isClient)
            {
                // Mirror pose smoothing (MP rule: clients display only). DriveTurret is
                // host-only, so without advancing currentYaw/currentPitch here toward the
                // host-sent pose (ReceiveNetworkState stores it in realYaw/realPitch), a
                // replica turret rendered frozen at its rest pose.
                currentYaw = Mathf.LerpAngle(currentYaw, realYaw, 0.2f);
                currentPitch = Mathf.LerpAngle(currentPitch, realPitch, 0.2f);
                ApplyVisualPose();
            }
        }

        public void ReceiveActive(bool value)
        {
            active = value;
        }

        public void ReceiveNetworkState(float yaw, float pitch, bool shoot)
        {
            realYaw = yaw;
            realPitch = pitch;
            shooting = shoot;
            ApplyVisualPose();
            SetSmallShoot(shooting && caliber < 76f);
        }

        public void ReceiveLargeShot(Vector3 position, Vector3 forward, Vector3 velocity, float shotCaliber, float damage, float lifetime)
        {
            SpaceBallistics.SpawnKineticRound(
                position, forward, velocity, shotCaliber, SpaceBalance.KineticDamagePerKE,
                Mathf.Max(0.05f, shotCaliber / 300f), lifetime, PlayerID, Team, muzzleVelocity);
        }

        private void UpdateFireControlTarget()
        {
            hasTarget = false;
            engagedTarget = null;
            shooting = false;
            if (!active)
            {
                SetSmallShoot(false);
                return;
            }

            ShipState ship = OwnShip();
            if (ship == null)
            {
                SetSmallShoot(false);
                return;
            }

            // Multi-channel fire control (ADR-0010): pick from this turret's enabled channels —
            // channel 0 first, then best angle/distance score — with candidates outside the
            // turret's range or yaw/pitch reach (射界, channelArcCheck) skipped entirely. The
            // captain's IFF override (CanCommandLockedFireAt) is applied per candidate inside
            // TrySelectSolution, so a locked friendly/own-team target stays engageable with IFF
            // off and is never re-filtered by SpaceBallistics.IsHostile() below. Channel 0 is the
            // sole point-defense target source now (ADR-0012 retired the separate
            // DefenseDirectorBlock/DefensiveSolution fallback that used to live here).
            FireSolution solution;
            if (!FireChannels.TrySelectSolution(ship, FireChannel.Value, channel0Assignment, GetMuzzlePosition(), muzzleVelocity,
                    GetMuzzleForward(), range, channelArcCheck, out solution))
            {
                SetSmallShoot(false);
                return;
            }

            Vector3 aimPoint = solution.AimPoint;
            Vector3 delta = aimPoint - GetMuzzlePosition();
            if (delta.magnitude > range)
            {
                SetSmallShoot(false);
                return;
            }

            hasTarget = true;
            engagedTarget = solution.Target;
            AimAnglesFor(aimPoint, out targetYaw, out targetPitch);
            ApplyAngleFeedForward();
        }

        // Leads targetYaw/targetPitch by the tracking loop's known lag (see the AngleLagTicks
        // field comment). Runs only on the success path above; DriveTurret clears the history
        // on any tick without a target, so a re-acquire always starts from zero rate.
        private void ApplyAngleFeedForward()
        {
            float yawDelta = Mathf.DeltaAngle(prevRawTargetAngles.x, targetYaw);
            float pitchDelta = targetPitch - prevRawTargetAngles.y;
            bool continuous = hasAngleHistory && angleRateTarget == engagedTarget
                && Mathf.Abs(yawDelta) < AngleRateJumpResetDegrees
                && Mathf.Abs(pitchDelta) < AngleRateJumpResetDegrees;
            if (continuous)
            {
                Vector2 instantRate = new Vector2(
                    Mathf.Clamp(yawDelta, -trackingSpeed, trackingSpeed),
                    Mathf.Clamp(pitchDelta, -trackingSpeed, trackingSpeed));
                targetAngleRate = Vector2.Lerp(targetAngleRate, instantRate, AngleRateSmoothing);
            }
            else
            {
                targetAngleRate = Vector2.zero;
            }

            hasAngleHistory = true;
            angleRateTarget = engagedTarget;
            prevRawTargetAngles = new Vector2(targetYaw, targetPitch);

            targetYaw += targetAngleRate.x * AngleLagTicks;
            targetPitch += targetAngleRate.y * AngleLagTicks;
        }

        // The Vis hierarchy bakes in a fixed 90 deg X rotation (see InitSimulationModel's
        // simRoot.localEulerAngles = (90,0,0)), so working through that rotation, the gun's
        // mechanical rest direction (yaw=0, pitch=0) is transform's local -up, not forward
        // — and the real yaw axis (the mount's own vertical) is transform.forward. This
        // matches WW2-Naval's AATurretController.GunYaw/TargetYaw/GunPitch (same Vis
        // hierarchy this block was ported from), which uses -transform.up as the yaw-zero
        // reference — generalized here from WW2's world Vector3.up (naval ships are always
        // upright) to this turret's own transform.forward, since a space-combat ship can be
        // arbitrarily oriented. The previous Quaternion.LookRotation(delta, transform.up) +
        // local Euler decomposition implicitly measured yaw/pitch from transform.forward
        // instead, aiming the turret from the wrong reference frame (off by ~90 degrees).
        private void AimAnglesFor(Vector3 aimPoint, out float yawDeg, out float pitchDeg)
        {
            Vector3 delta = aimPoint - GetMuzzlePosition();
            Vector3 yawAxis = transform.forward;
            yawDeg = SignedAngleAroundAxis(-transform.up, delta, yawAxis);
            pitchDeg = 90f - Vector3.Angle(delta, yawAxis);
        }

        // 射界 test for the multi-channel walk: can the mount mechanically point at this aim
        // point at all? Mirrors DriveTurret's clamps exactly — yaw limits only bind while the
        // player has the limits toggle in the same state DriveTurret honors, pitch is always
        // hard-clamped to [-5, 90]. A channel target failing this is skipped outright rather
        // than parking the turret against its stops (ADR-0010's "目标在射界外则不考虑").
        private bool IsAimPointInArc(Vector3 aimPoint)
        {
            float yawDeg;
            float pitchDeg;
            AimAnglesFor(aimPoint, out yawDeg, out pitchDeg);
            if (YawLimit.UseLimitsToggle.isDefaultValue && (yawDeg < -YawLimit.Min || yawDeg > YawLimit.Max))
            {
                return false;
            }
            return pitchDeg >= -5f && pitchDeg <= 90f;
        }

        private static float SignedAngleAroundAxis(Vector3 from, Vector3 to, Vector3 axis)
        {
            axis = axis.normalized;
            Vector3 fromProjected = Vector3.ProjectOnPlane(from, axis);
            Vector3 toProjected = Vector3.ProjectOnPlane(to, axis);
            if (fromProjected.sqrMagnitude < 0.0001f || toProjected.sqrMagnitude < 0.0001f)
            {
                return 0f;
            }
            fromProjected.Normalize();
            toProjected.Normalize();
            return Mathf.Atan2(
                Vector3.Dot(axis, Vector3.Cross(fromProjected, toProjected)),
                Vector3.Dot(fromProjected, toProjected)) * Mathf.Rad2Deg;
        }

        private void DriveTurret()
        {
            bool ready = false;
            if (hasTarget)
            {
                UpdateRandomError();
                float speed = trackingSpeed;
                realYaw = StepAngle(realYaw, targetYaw, speed);
                realPitch = StepAngle(realPitch, targetPitch, speed);
                if (YawLimit.UseLimitsToggle.isDefaultValue)
                {
                    realYaw = Mathf.Clamp(realYaw, -YawLimit.Min, YawLimit.Max);
                }
                realPitch = Mathf.Clamp(realPitch, -5f, 90f);
                ready = Mathf.Abs(Mathf.DeltaAngle(realYaw, targetYaw)) + Mathf.Abs(Mathf.DeltaAngle(realPitch, targetPitch)) < 10f;
            }
            else
            {
                hasAngleHistory = false;
            }

            currentYaw = Mathf.LerpAngle(currentYaw, realYaw + randomError.x, 0.2f);
            currentPitch = Mathf.LerpAngle(currentPitch, realPitch + randomError.y, 0.2f);
            ApplyVisualPose();

            if (!ready)
            {
                shooting = false;
                SetSmallShoot(false);
                return;
            }

            ShipState ship = OwnShip();
            // Active-defence energy draw is scaled by the 主动-被动防御 macro dial (SpaceBalance.
            // ActiveDefenseEnergyScale, 1 at neutral) alongside CIWS and the interceptor launcher.
            float energy = ship == null ? 1f : ship.Energy.Request(EnergyBus.Weapon, EnergyPerSecond.Value * Time.fixedDeltaTime * SpaceBalance.ActiveDefenseEnergyScale);
            shooting = energy > 0.15f;
            if (caliber < 76f)
            {
                SetSmallShoot(shooting);
                if (shooting)
                {
                    ApplyParticleDamage(energy);
                }
            }
            else
            {
                SetSmallShoot(false);
                reload += Time.fixedDeltaTime;
                if (shooting && reload >= reloadTime)
                {
                    FireLargeRound(energy);
                }
            }
        }

        private void ApplyParticleDamage(float energy)
        {
            ShipState ship = OwnShip();
            if (ship == null || engagedTarget == null)
            {
                return;
            }

            MissileProjectile missile = engagedTarget as MissileProjectile;
            if (missile == null)
            {
                return;
            }

            float distance = Vector3.Distance(GetMuzzlePosition(), missile.Position);
            if (distance > range || Vector3.Angle(GetMuzzleForward(), missile.Position - GetMuzzlePosition()) > 10f)
            {
                return;
            }

            // Penalty scales with how fast the missile is closing on THIS ship, not its world speed —
            // two ships flying the same way fast shouldn't read as a hard-to-hit target (ADR 0006).
            Vector3 shipVelocity = ship.Core != null ? ship.Core.Velocity : Vector3.zero;
            float closingSpeed = (missile.Velocity - shipVelocity).magnitude;
            float speedPenalty = Mathf.Clamp01(1f - closingSpeed / 900f);
            float rangeFactor = Mathf.Clamp01(1f - distance / range);
            missile.ApplyDamage((caliber * gunCount) * (0.25f + rangeFactor + speedPenalty) * energy * Time.fixedDeltaTime * 12f);
        }

        private void FireLargeRound(float energy)
        {
            reload = 0f;
            alternateMuzzle = !alternateMuzzle;
            Vector3 forward = GetMuzzleForward();
            Vector3 position = GetMuzzlePosition() + forward + (alternateMuzzle ? -1f : 1f) * gunWidth * pitchTransform.right;
            Vector3 randomForce = RandomForce(caliber);
            Vector3 velocity = forward * muzzleVelocity + randomForce + (Body == null ? Vector3.zero : Body.velocity);
            // Pure flight-time-to-max-range estimate, not a lead solution: shooterVelocity is zero
            // so this stays a world-frame time and is unaffected by the ship-frame change in ADR 0006.
            float lifetime = Mathf.Clamp(SpaceBallistics.EstimateInterceptTime(position, position + forward * range, Vector3.zero, muzzleVelocity, Vector3.zero) + 2f, 2f, 10f);
            float damage = caliber * (1.6f + energy);
            // Flak large rounds are kinetic penetrators too now (ADR-0007): KE from caliber-derived
            // mass and relative speed, not the old flat `damage` (which now only rides the MP
            // message below and is ignored on receive).
            SpaceBallistics.SpawnKineticRound(
                position, forward, velocity, caliber, SpaceBalance.KineticDamagePerKE,
                Mathf.Max(0.05f, caliber / 300f), lifetime, PlayerID, Team, muzzleVelocity);

            if (StatMaster.isMP)
            {
                ModNetworking.SendToAll(SpaceFlakTurretNet.ShotMsg.CreateMessage(PlayerID, GuidHash, position, forward, velocity, caliber, damage, lifetime));
            }
        }

        private void InitBuildModel()
        {
            Transform vis = transform.Find("Vis");
            if (vis == null)
            {
                return;
            }
            buildBase = vis.gameObject;
            EnsureBuildSplitModel(vis);
            ResolveType();
            UpdateAppearance(false);
        }

        private void InitSimulationModel()
        {
            if (simRoot == null)
            {
                simRoot = new GameObject("myVis");
                simRoot.transform.SetParent(transform);
                simRoot.transform.localScale = Vector3.one * 0.2f;
                simRoot.transform.localPosition = Vector3.zero;
                simRoot.transform.localEulerAngles = new Vector3(90f, 0f, 0f);
            }

            EnsureSimSplitModel(simRoot.transform);
            InitSmallShoot();
            UpdateAppearance(true);
        }

        // Matches WW2-Naval's AABlock.InitBaseGunObjectBuild for the outermost piece: Vis
        // itself IS the mount object now (PaoZuo, the pedestal PaoTa/PaoGuan sit on), so
        // UpdateAppearance overwrites its own placeholder MeshFilter/MeshRenderer (from the
        // XML <Mesh>/<Texture> tags) directly instead of hiding it behind a separate object —
        // previously this role was played by the turret mesh (PaoTa-equivalent) before PaoZuo
        // existed as its own piece. Base (PaoTa) is a new explicit child since Vis's own mesh
        // slot is now taken by the mount; GunBase/Gun (PaoGuan) are unchanged. This has to stay
        // structurally different from the simulation hierarchy below — nesting objects two
        // levels under Vis (inside Vis's own baked Rotation/Scale from the XML) was compounding
        // that transform into the per-type offset and mispositioning the model in build mode
        // specifically; simulation mode never had this problem because it builds under a fresh
        // "myVis" parented straight to the block root, not under Vis.
        private void EnsureBuildSplitModel(Transform vis)
        {
            Transform baseTransform = vis.Find("Base");
            if (baseTransform == null)
            {
                GameObject obj = new GameObject("Base");
                obj.transform.SetParent(vis);
                obj.transform.localScale = Vector3.one;
                obj.transform.localPosition = Vector3.zero;
                obj.transform.localEulerAngles = Vector3.zero;
                obj.AddComponent<MeshFilter>();
                obj.AddComponent<MeshRenderer>().material = CloneVisMaterial();
                baseTransform = obj.transform;
            }

            Transform gunBase = vis.Find("GunBase");
            if (gunBase == null)
            {
                GameObject gunBaseObject = new GameObject("GunBase");
                gunBaseObject.transform.SetParent(vis);
                gunBaseObject.transform.localScale = Vector3.one;
                gunBaseObject.transform.localPosition = Vector3.zero;
                gunBaseObject.transform.localEulerAngles = Vector3.zero;
                gunBase = gunBaseObject.transform;
            }

            Transform gunTransform = gunBase.Find("Gun");
            if (gunTransform == null)
            {
                GameObject obj = new GameObject("Gun");
                obj.transform.SetParent(gunBase);
                obj.transform.localScale = Vector3.one;
                obj.transform.localPosition = Vector3.zero;
                obj.transform.localEulerAngles = Vector3.zero;
                obj.AddComponent<MeshFilter>();
                obj.AddComponent<MeshRenderer>().material = CloneVisMaterial();
                gunTransform = obj.transform;
            }

            mountObject = vis.gameObject;
            baseObject = baseTransform.gameObject;
            gunObject = gunTransform.gameObject;
            pitchTransform = gunBase;
        }

        // TurretBsae (PaoZuo, the pedestal the whole mount is bolted to the ship with) has to
        // sit above BaseBase rather than under it — BaseBase is the yaw pivot (see
        // ApplyVisualPose), so anything parented under it inherits that spin every tick. A
        // child of BaseBase can't satisfy "does not rotate with the turret"; only a parent of
        // BaseBase can. BaseBase/GunBase keep their existing roles (yaw/pitch pivots for
        // PaoTa/PaoGuan) unchanged, just re-parented one level down from root to TurretBsae.
        private void EnsureSimSplitModel(Transform root)
        {
            Transform turretBase = root.Find("TurretBsae");
            if (turretBase == null)
            {
                GameObject turretBaseObject = new GameObject("TurretBsae");
                turretBaseObject.transform.SetParent(root);
                turretBaseObject.transform.localScale = Vector3.one;
                turretBaseObject.transform.localPosition = Vector3.zero;
                turretBaseObject.transform.localEulerAngles = Vector3.zero;
                turretBaseObject.AddComponent<MeshFilter>();
                turretBaseObject.AddComponent<MeshRenderer>().material = CloneVisMaterial();
                turretBase = turretBaseObject.transform;
            }

            Transform baseBase = turretBase.Find("BaseBase");
            if (baseBase == null)
            {
                GameObject baseBaseObject = new GameObject("BaseBase");
                baseBaseObject.transform.SetParent(turretBase);
                baseBaseObject.transform.localScale = Vector3.one;
                baseBaseObject.transform.localPosition = Vector3.zero;
                baseBaseObject.transform.localEulerAngles = Vector3.zero;
                baseBase = baseBaseObject.transform;
            }

            Transform baseTransform = baseBase.Find("Base");
            if (baseTransform == null)
            {
                GameObject obj = new GameObject("Base");
                obj.transform.SetParent(baseBase);
                obj.transform.localScale = Vector3.one;
                obj.transform.localPosition = Vector3.zero;
                obj.transform.localEulerAngles = Vector3.zero;
                obj.AddComponent<MeshFilter>();
                obj.AddComponent<MeshRenderer>().material = CloneVisMaterial();
                baseTransform = obj.transform;
            }

            Transform gunBase = baseBase.Find("GunBase");
            if (gunBase == null)
            {
                GameObject gunBaseObject = new GameObject("GunBase");
                gunBaseObject.transform.SetParent(baseBase);
                gunBaseObject.transform.localScale = Vector3.one;
                gunBaseObject.transform.localPosition = Vector3.zero;
                gunBaseObject.transform.localEulerAngles = Vector3.zero;
                gunBase = gunBaseObject.transform;
            }

            Transform gunTransform = gunBase.Find("Gun");
            if (gunTransform == null)
            {
                GameObject obj = new GameObject("Gun");
                obj.transform.SetParent(gunBase);
                obj.transform.localScale = Vector3.one;
                obj.transform.localPosition = Vector3.zero;
                obj.transform.localEulerAngles = Vector3.zero;
                obj.AddComponent<MeshFilter>();
                obj.AddComponent<MeshRenderer>().material = CloneVisMaterial();
                gunTransform = obj.transform;
            }

            mountObject = turretBase.gameObject;
            baseObject = baseTransform.gameObject;
            gunObject = gunTransform.gameObject;
            yawTransform = baseBase;
            pitchTransform = gunBase;
        }

        // WW2-Naval clones Vis's own material onto every newly created Base/Gun renderer
        // instead of leaving them on Unity's default material.
        private Material CloneVisMaterial()
        {
            Transform vis = transform.Find("Vis");
            MeshRenderer visRenderer = vis == null ? null : vis.GetComponent<MeshRenderer>();
            return visRenderer == null ? null : visRenderer.material;
        }

        private void InitSmallShoot()
        {
            if (smallShoot != null || pitchTransform == null)
            {
                return;
            }

            GameObject effect = new GameObject("Small AA Particle Shot");
            effect.transform.SetParent(pitchTransform);
            effect.transform.localScale = Vector3.one;
            effect.transform.localRotation = Quaternion.identity;
            effect.transform.localPosition = new Vector3(0f, 0f, 7f);
            smallShoot = effect.AddComponent<ParticleSystem>();
            ParticleSystemRenderer renderer = effect.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.material = new Material(Shader.Find("Particles/Additive"));
                renderer.material.mainTexture = RoundDotTexture();
                renderer.renderMode = ParticleSystemRenderMode.Stretch;
                renderer.lengthScale = TracerLengthScale;
            }

            GameObject core = new GameObject("Core");
            core.transform.SetParent(effect.transform);
            core.transform.localPosition = Vector3.zero;
            core.transform.localRotation = Quaternion.identity;
            core.transform.localScale = new Vector3(gunWidth, 0f, 1f);
            ParticleSystem coreParticles = core.AddComponent<ParticleSystem>();
            ParticleSystemRenderer coreRenderer = core.GetComponent<ParticleSystemRenderer>();
            if (coreRenderer != null)
            {
                coreRenderer.material = new Material(Shader.Find("Particles/Additive"));
                coreRenderer.material.mainTexture = RoundDotTexture();
                coreRenderer.renderMode = ParticleSystemRenderMode.Stretch;
                coreRenderer.lengthScale = TracerLengthScale;
            }

            ConfigureParticleSystem(smallShoot, particleRate);
            ConfigureParticleSystem(coreParticles, particleRate);
            smallShoot.Stop();
            coreParticles.Stop();
        }

        private void ConfigureParticleSystem(ParticleSystem particles, float rate)
        {
            // Not recoverable from WW2-Naval's C# source — its tracer prefab (AssetBundle
            // asset "AA") is compiled, not text, so simulationSpace/shape aren't in any .cs
            // file. These are baseline requirements for a muzzle-mounted stream regardless:
            // World space stops already-fired particles from being dragged along as the
            // turret keeps re-aiming every tick (Local, the ParticleSystem default, is why
            // fired shots visibly swung with the gun); disabling shape removes Unity's
            // default 25-degree cone scatter so the stream reads as one coherent line.
            particles.simulationSpace = ParticleSystemSimulationSpace.World;
            // Fired tracer particles inherit the muzzle's world motion (≈ ship velocity) at birth, so
            // the stream carries the ship's velocity like the real rounds do — see ADR 0006. Unity
            // derives the emitter velocity from the muzzle transform's own movement, so this works
            // identically on host and clients with no ship-velocity value to plumb through. Needs the
            // World simulation space set above.
            ParticleSystem.InheritVelocityModule inheritVelocity = particles.inheritVelocity;
            inheritVelocity.enabled = true;
            inheritVelocity.mode = ParticleSystemInheritVelocityMode.Initial;
            inheritVelocity.curve = new ParticleSystem.MinMaxCurve(1f);
            ParticleSystem.ShapeModule shape = particles.shape;
            shape.enabled = false;
            // Default ParticleSystemScalingMode.Hierarchy applies the *whole* parent chain's
            // scale to rendered particle size — simRoot's uniform 0.2x, and for the "Core"
            // child specifically its own (gunWidth, 0, 1) local scale, which flattened every
            // particle to zero height (the reported "squashed" round dot). Shape mode ignores
            // transform scale for size/position entirely (fine since shape is disabled above),
            // leaving startSize below as the only thing that determines rendered size.
            particles.scalingMode = ParticleSystemScalingMode.Shape;

            particles.randomSeed = (uint)(UnityEngine.Random.value * 1000f);
            // Must equal the muzzleVelocity UpdateFireControlTarget() feeds into lead
            // prediction (and what MuzzleVelocityInfo displays) — otherwise the tracer's
            // travel speed wouldn't match the point the turret aims ahead of a moving target.
            particles.startSpeed = muzzleVelocity;
            particles.gravityModifier = 0f;
            particles.startLifetime = Mathf.Clamp(range / Mathf.Max(1f, muzzleVelocity), 0.35f, 2f);
            particles.startSize = Mathf.Pow(caliber, 1.5f) / 2000f;
            // No drag: a constant-velocity round in flight, not damped toward some lower speed.
            ParticleSystem.LimitVelocityOverLifetimeModule limit = particles.limitVelocityOverLifetime;
            limit.enabled = false;
            ParticleSystem.EmissionModule emission = particles.emission;
            emission.rate = rate;
        }

        // Round tracer dot, generated once and shared by every turret instance/caliber —
        // "Particles/Additive" with no texture assigned renders a solid square, not a circle.
        private static Texture2D roundDotTexture;

        private static Texture2D RoundDotTexture()
        {
            if (roundDotTexture == null)
            {
                const int size = 16;
                float radius = size * 0.5f;
                roundDotTexture = new Texture2D(size, size, TextureFormat.ARGB32, false);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(radius, radius));
                        roundDotTexture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(1f - dist / radius)));
                    }
                }
                roundDotTexture.Apply();
            }
            return roundDotTexture;
        }

        private void SetSmallShoot(bool value)
        {
            if (smallShoot == null)
            {
                return;
            }

            if (smallShoot.isPlaying != value)
            {
                if (value)
                {
                    smallShoot.Play();
                    if (smallShoot.transform.childCount > 0)
                    {
                        ParticleSystem child = smallShoot.transform.GetChild(0).GetComponent<ParticleSystem>();
                        if (child != null)
                        {
                            child.Play();
                        }
                    }
                }
                else
                {
                    smallShoot.Stop();
                    if (smallShoot.transform.childCount > 0)
                    {
                        ParticleSystem child = smallShoot.transform.GetChild(0).GetComponent<ParticleSystem>();
                        if (child != null)
                        {
                            child.Stop();
                        }
                    }
                }
            }
        }

        private void HoldAppearance(bool simulation)
        {
            if (simulation)
            {
                if (originVis != null && originVis.activeSelf)
                {
                    originVis.SetActive(false);
                }
                return;
            }

            // Matches WW2-Naval's AABlock.HoldAppearance: this refresh (cluster-LOD/skin/type
            // change) — and, downstream of ResolveType/RecomputeStats, caliber itself — is
            // build-mode only. The guard above used to be `simulation && originVis != null`,
            // which meant a null originVis (shouldn't happen — "Vis" is always created from the
            // block's own XML <Mesh> tag — but nothing enforced it) would fall through into the
            // block below during actual simulation instead of returning, recomputing caliber
            // outside the two places it's supposed to update (BuildingUpdate's live build-mode
            // recompute and OnSimulateStart's one-time lock-in). Unconditional on `simulation`
            // now so that can't happen regardless of originVis. ResolveType() below reconfigures
            // the live <76mm AA tracer ParticleSystem (see ConfigureParticleSystem), and
            // StatMaster.clusterCoded/OptionsMaster.skinsEnabled do change mid-simulation —
            // letting this run there reassigns randomSeed/startLifetime/etc. on a system that
            // may be actively playing and corrupts the small-caliber shot effect.
            bool appearanceChanged = false;
            if (previousShowCluster != StatMaster.clusterCoded)
            {
                previousShowCluster = StatMaster.clusterCoded;
                appearanceChanged = true;
            }
            if (previousSkinEnabled != OptionsMaster.skinsEnabled)
            {
                previousSkinEnabled = OptionsMaster.skinsEnabled;
                appearanceChanged = true;
            }
            if (appearanceChanged)
            {
                ResolveType();
                UpdateAppearance(simulation);
            }

            if (previousType != Type.Value)
            {
                ResolveType();
                UpdateAppearance(simulation);
                previousType = Type.Value;
            }
        }

        // All 13 Type entries now share one fixed model (PaoZuo/PaoTa/PaoGuan) instead of each
        // caliber loading its own WW2-sourced mesh pair — Type still drives caliber/gunCount/
        // damage via ResolveType's switch below, just not the visuals anymore.
        private void UpdateAppearance(bool simulation)
        {
            if (mountObject == null || baseObject == null || gunObject == null)
            {
                return;
            }

            int type = Mathf.Clamp(Type.Value, 0, SpaceFlakTurretAssets.TypeCount - 1);
            mountObject.GetComponent<MeshFilter>().sharedMesh = ModResource.GetMesh(SpaceFlakTurretAssets.MountMeshName[type]).Mesh;
            mountObject.GetComponent<MeshRenderer>().material.mainTexture = ModResource.GetTexture(SpaceFlakTurretAssets.MountTextureName[type]).Texture;
            baseObject.GetComponent<MeshFilter>().sharedMesh = ModResource.GetMesh(SpaceFlakTurretAssets.TurretMeshName[type]).Mesh;
            baseObject.GetComponent<MeshRenderer>().material.mainTexture = ModResource.GetTexture(SpaceFlakTurretAssets.TurretTextureName[type]).Texture;
            gunObject.GetComponent<MeshFilter>().sharedMesh = ModResource.GetMesh(SpaceFlakTurretAssets.GunMeshName[type]).Mesh;
            gunObject.GetComponent<MeshRenderer>().material.mainTexture = ModResource.GetTexture(SpaceFlakTurretAssets.GunTextureName[type]).Texture;

            // Offsets below are all zero pending in-game tuning — nobody has checked yet where
            // each type's PaoZuo/PaoTa/PaoGuan pivots actually land once loaded, unlike the old
            // per-type arrays which were hand-tuned against the WW2 meshes they shipped with.
            if (simulation)
            {
                mountObject.transform.localPosition = SpaceFlakTurretAssets.MountOffset[type];
                baseObject.transform.localPosition = SpaceFlakTurretAssets.TurretOffset[type];
                gunObject.transform.localPosition = SpaceFlakTurretAssets.GunOffset[type];
                pitchTransform.localPosition = SpaceFlakTurretAssets.GunBaseOffset[type];
            }
            else
            {
                Vector3 offset = SpaceFlakTurretAssets.TurretOffset[type] * 0.2f;
                baseObject.transform.localPosition = new Vector3(offset.x, -offset.z, offset.y);
                gunObject.transform.localPosition = SpaceFlakTurretAssets.GunOffset[type];
                pitchTransform.localPosition = SpaceFlakTurretAssets.GunBaseOffset[type] - offset * 5f;
            }
        }

        private void ApplyVisualPose()
        {
            if (yawTransform != null)
            {
                yawTransform.localEulerAngles = new Vector3(0f, currentYaw, 0f);
            }
            if (pitchTransform != null)
            {
                pitchTransform.localEulerAngles = new Vector3(-currentPitch, 0f, 0f);
            }
        }

        private void ResolveType()
        {
            int type = Mathf.Clamp(Type.Value, 0, SpaceFlakTurretAssets.TypeCount - 1);
            SpaceFlakTurretAssets.EnsureInitialized();
            gunWidth = SpaceFlakTurretAssets.GunWidth[type];
            particleRate = SpaceFlakTurretAssets.GunSpeed[type];
            gunCount = SpaceFlakTurretAssets.GunCount[type];
            RecomputeStats();
            if (smallShoot != null)
            {
                ConfigureParticleSystem(smallShoot, particleRate);
                if (smallShoot.transform.childCount > 0)
                {
                    ConfigureParticleSystem(smallShoot.transform.GetChild(0).GetComponent<ParticleSystem>(), particleRate);
                    smallShoot.transform.GetChild(0).localScale = new Vector3(gunWidth, 0f, 1f);
                }
            }
        }

        // Recomputes caliber and everything downstream of it (tracking speed/muzzle velocity/
        // range/reload time) from the current Type's BaseCaliber and the block's live scale, plus
        // the Energy Use slider. Called from ResolveType (type just changed) and unconditionally
        // every BuildingUpdate tick (scale may have been dragged, or Energy Use, without Type
        // changing) — mirrors RailgunBarrelBlock.RecomputeBallistics/SuperCapacitorBlock.
        // RecomputeCapacity, including their MinScaleComponent floor so a squashed-flat build
        // handle can't divide-by-zero this into NaN. Caliber uses the cube root of the scaled
        // volume (not the raw volume) so it stays a linear dimension — scaling a block by 2x in
        // every axis multiplies its volume by 8x but should only double its caliber, and cube
        // root recovers that "2x" from the 8x volume even under non-uniform x/y/z scale. Bore
        // length ratio (倍径, L/45 for all three types at rest scale) isn't a separate variable
        // here — it's a fixed proportion baked into the mesh geometry itself, so it scales
        // automatically along with everything else and never needs its own term in a formula.
        private void RecomputeStats()
        {
            int type = Mathf.Clamp(Type.Value, 0, SpaceFlakTurretAssets.TypeCount - 1);
            Vector3 scale = transform.localScale;
            float x = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.x));
            float y = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.y));
            float z = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.z));
            float scaleFactor = Mathf.Pow(x * y * z, 1f / 3f);
            caliber = SpaceFlakTurretAssets.BaseCaliber[type] * scaleFactor;

            trackingSpeed = Mathf.Clamp(TrackingSpeedCaliberCoefficient / caliber, TrackingSpeedMin, TrackingSpeedMax);
            muzzleVelocity = Mathf.Clamp(MuzzleVelocityBase + Mathf.Sqrt(caliber) * MuzzleVelocityCaliberCoefficient + EnergyPerSecond.Value * MuzzleVelocityEnergyCoefficient, MuzzleVelocityMin, MuzzleVelocityMax);
            range = Mathf.Clamp(muzzleVelocity * RangeFlightTimeBudget, RangeMin, RangeMax);
            reloadTime = caliber >= 100f ? caliber / 100f * 0.3f : caliber / 76f * 0.12f;

            CaliberInfo.Set(caliber.ToString("F0") + " mm");
            TrackingSpeedInfo.Set(trackingSpeed.ToString("F2") + " deg/tick");
            MuzzleVelocityInfo.Set(muzzleVelocity.ToString("F0") + " m/s");
            RangeInfo.Set(range.ToString("F0") + " m");
        }

        private Vector3 GetMuzzlePosition()
        {
            return pitchTransform == null ? transform.position + transform.forward : pitchTransform.position + pitchTransform.forward;
        }

        private Vector3 GetMuzzleForward()
        {
            return pitchTransform == null ? transform.forward : pitchTransform.forward;
        }

        private void UpdateRandomError()
        {
            errorAngle += (UnityEngine.Random.value - 0.5f) * 0.6f;
            errorDistance += (UnityEngine.Random.value - 0.5f) * 0.3f;
            errorDistance = Mathf.Clamp(errorDistance, -8f, 8f);
            float multiplier = caliber < 76f ? 0.5f : 0.25f;
            randomError.x = Mathf.Sin(errorAngle) * errorDistance * multiplier;
            randomError.y = Mathf.Cos(errorAngle) * errorDistance * multiplier;
        }

        private void SyncStateIfNeeded()
        {
            if (!StatMaster.isMP || Time.time < nextStateSync)
            {
                return;
            }
            if (shooting != previousShooting || shooting)
            {
                ModNetworking.SendToAll(SpaceFlakTurretNet.StateMsg.CreateMessage(PlayerID, GuidHash, currentYaw, currentPitch, shooting));
                previousShooting = shooting;
                nextStateSync = Time.time + 0.08f;
            }
            else
            {
                nextStateSync = Time.time + 0.25f;
                ModNetworking.SendToAll(SpaceFlakTurretNet.StateMsg.CreateMessage(PlayerID, GuidHash, currentYaw, currentPitch, false));
            }
        }

        private void SendActive()
        {
            if (StatMaster.isMP)
            {
                ModNetworking.SendToAll(SpaceFlakTurretNet.ActiveMsg.CreateMessage(PlayerID, GuidHash, active));
            }
        }

        private static float StepAngle(float current, float target, float speed)
        {
            float delta = Mathf.DeltaAngle(current, target);
            if (Mathf.Abs(delta) < speed * 8f)
            {
                return current + delta * 0.2f;
            }
            return current + Mathf.Sign(delta) * speed;
        }

        private static Vector3 RandomForce(float shotCaliber)
        {
            Vector3 randomForce = new Vector3(UnityEngine.Random.value - 0.5f, UnityEngine.Random.value - 0.5f, UnityEngine.Random.value - 0.5f) * 6f / Mathf.Sqrt(shotCaliber);
            randomForce += new Vector3(0f, UnityEngine.Random.value - 0.5f, 0f) * 6f / Mathf.Sqrt(shotCaliber);
            return randomForce * Mathf.Pow(UnityEngine.Random.value, 2f);
        }
    }

    // Index 0/1/2 = 单管火炮 (single-barrel) / 多管火炮 (multi-barrel) / 近防炮 (CIWS) throughout
    // every array in this class — must stay in sync with SpaceFlakTurretBlock.typeList.
    public static class SpaceFlakTurretAssets
    {
        public const int TypeCount = 3;

        public static readonly string[] MountMeshName = { "BS FlakTurret Single Mount Mesh", "BS FlakTurret Multi Mount Mesh", "BS FlakTurret Ciws Mount Mesh" };
        public static readonly string[] MountTextureName = { "BS FlakTurret Single Mount Texture", "BS FlakTurret Multi Mount Texture", "BS FlakTurret Ciws Mount Texture" };
        public static readonly string[] TurretMeshName = { "BS FlakTurret Single Turret Mesh", "BS FlakTurret Multi Turret Mesh", "BS FlakTurret Ciws Turret Mesh" };
        public static readonly string[] TurretTextureName = { "BS FlakTurret Single Turret Texture", "BS FlakTurret Multi Turret Texture", "BS FlakTurret Ciws Turret Texture" };
        public static readonly string[] GunMeshName = { "BS FlakTurret Single Gun Mesh", "BS FlakTurret Multi Gun Mesh", "BS FlakTurret Ciws Gun Mesh" };
        public static readonly string[] GunTextureName = { "BS FlakTurret Single Gun Texture", "BS FlakTurret Multi Gun Texture", "BS FlakTurret Ciws Gun Texture" };

        // Caliber (mm) at rest scale (1,1,1) — SpaceFlakTurretBlock.RecomputeStats multiplies
        // this by the cube root of the scaled volume every tick. Bore length ratio (倍径) is
        // L/45 for all three at rest scale; not tracked separately since it's a fixed proportion
        // baked into the mesh and scales along with everything else automatically.
        public static readonly float[] BaseCaliber = { 100f, 100f, 30f };

        // Placeholder — nobody has confirmed actual barrel counts for 多管/近防炮 yet. Only
        // matters when caliber < 76 (ApplyParticleDamage's small-caliber stream path); at rest
        // scale that's 近防炮 (30) only, since 单管/多管 start at 100 and scaling only raises
        // caliber further for a ship built bigger, not smaller.
        public static readonly int[] GunCount = { 1, 2, 1 };

        // All zero pending in-game tuning — nobody has checked yet where each type's PaoZuo/
        // PaoTa/PaoGuan pivots actually land once loaded, unlike the old per-type arrays which
        // were hand-tuned against the WW2 meshes they shipped with.
        public static readonly Vector3[] MountOffset = new Vector3[TypeCount];
        public static readonly Vector3[] TurretOffset = new Vector3[TypeCount];
        public static readonly Vector3[] GunOffset = new Vector3[TypeCount];
        public static readonly Vector3[] GunBaseOffset = new Vector3[TypeCount];

        public static readonly float[] GunWidth = new float[TypeCount];
        public static readonly float[] GunSpeed = new float[TypeCount];

        private static bool initialized;

        public static void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }
            initialized = true;

            // 单管/多管 sit at caliber >= 76 at rest scale, so these two only feed the tracer
            // stream if the ship is ever scaled down — placeholders carried over from the old
            // table's nearest-caliber entries, effectively dead weight until that happens.
            GunWidth[0] = 0.07f;
            GunSpeed[0] = 7f;

            GunWidth[1] = 0.15f;
            GunSpeed[1] = 14f;

            // 近防炮 stays under 76 at rest scale, so this one actually drives the live tracer
            // stream — picked to read as a dense CIWS burst, not verified in-game.
            GunWidth[2] = 1.5f;
            GunSpeed[2] = 50f;
        }
    }
}
