using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // MP replication for the railgun's fired slug (the flak turret's ShotMsg pattern): the host
    // fires the authoritative round in FireAt and broadcasts the launch parameters; each client's
    // matching barrel spawns its own visual mirror round (NetAuthority gates it to display-only).
    public class RailgunNet : SingleInstance<RailgunNet>
    {
        public override string Name { get { return "BeyondSpider Railgun Net"; } }

        // playerId, guidHash, muzzlePos, forward, worldVelocity, caliberMm, lifetime
        public static MessageType ShotMsg = ModNetworking.CreateMessageType(
            DataType.Integer, DataType.Integer, DataType.Vector3, DataType.Vector3, DataType.Vector3, DataType.Single, DataType.Single);

        private readonly Dictionary<string, RailgunBarrelBlock> barrels = new Dictionary<string, RailgunBarrelBlock>();

        public void Register(RailgunBarrelBlock barrel)
        {
            barrels[Key(barrel.PlayerID, barrel.GuidHash)] = barrel;
        }

        public void Unregister(RailgunBarrelBlock barrel)
        {
            barrels.Remove(Key(barrel.PlayerID, barrel.GuidHash));
        }

        public void ShotReceiver(Message msg)
        {
            RailgunBarrelBlock barrel;
            if (barrels.TryGetValue(Key((int)msg.GetData(0), (int)msg.GetData(1)), out barrel) && barrel != null)
            {
                barrel.ReceiveShot((Vector3)msg.GetData(2), (Vector3)msg.GetData(3), (Vector3)msg.GetData(4), (float)msg.GetData(5), (float)msg.GetData(6));
            }
        }

        private static string Key(int playerId, int guid)
        {
            return playerId + ":" + guid;
        }
    }

    public class RailgunBarrelBlock : SpaceBlock, IShipGun
    {
        // Auto-properties (not plain fields) so they satisfy IShipGun; assignment stays in
        // SafeAwake via the private setters, external access is unchanged.
        public MKey FireKey { get; private set; }
        public MToggle FireControl { get; private set; }
        public MText GunGroup { get; private set; }
        public MInfo CaliberInfo;
        public MInfo BoreLengthRatioInfo;
        public MInfo MuzzleVelocityInfo;

        private const float ReloadIconSize = 50f;
        private const float ReloadIconWorldOffset = 0f;
        private const float BaseMuzzleForwardOffset = 2f;
        private const float MinScaleComponent = 0.05f;

        // Caliber/bore length/muzzle velocity used to be player-set sliders; they're now derived
        // from the block's own build-mode scale instead, so the player sizes the barrel visually
        // and these follow automatically. See agent-besiege-mod-guide.md for the derivation.
        private const float CaliberFromScaleCoefficient = 300f;
        private const float BoreLengthFromScaleCoefficient = 16000f;
        private const float MuzzleVelocityPerBoreLength = 65f;
        // Fixed firing overhead added on top of the per-damage cost from SpaceBalance; the
        // damage-scaled part is what dominates. See FireAt.
        private const float EnergyPerShotBase = 40f;

        public int GuidHash { get; private set; }

        private float reloadTime;
        private float reload;
        private bool manualFireQueued;
        private float caliberMm;
        private float boreLengthRatio;
        private float muzzleVelocity;
        // See agent-besiege-mod-guide.md's "注册时序规范" — retried each tick until it
        // succeeds, since ShipState may not exist yet when this OnSimulateStart runs.
        private bool registered;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Railgun Barrel";
            FireKey = AddKey("Fire", "BSRailgunFire", KeyCode.C);
            FireControl = AddToggle("Fire Control", "BSRailgunFireControl", false);
            GunGroup = AddText("Gun Group", "BSRailgunGroup", "g0");
            CaliberInfo = AddInfo("Caliber", "BSRailgunCaliberInfo");
            BoreLengthRatioInfo = AddInfo("Bore Length", "BSRailgunBoreLengthInfo");
            MuzzleVelocityInfo = AddInfo("Muzzle Velocity", "BSRailgunVelocityInfo");
            RecomputeBallistics();
        }

        // Caliber depends only on the barrel's cross-section (local x/y scale); bore length ratio
        // ("倍径", barrel length in calibers, standard naval-gun notation like "16 in/L45") folds
        // in the along-barrel scale (local z, same axis ApproximateMuzzlePosition scales against);
        // muzzle velocity follows from bore length ratio alone. Recomputed continuously (not just
        // once) because BuildingUpdate needs it live while the player drags the scale handles.
        private void RecomputeBallistics()
        {
            Vector3 scale = transform.localScale;
            float x = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.x));
            float y = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.y));
            float z = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.z));

            caliberMm = CaliberFromScaleCoefficient * Mathf.Sqrt(x * y);
            boreLengthRatio = BoreLengthFromScaleCoefficient * z / caliberMm;
            muzzleVelocity = MuzzleVelocityPerBoreLength * boreLengthRatio;

            CaliberInfo.Set(caliberMm.ToString("F0") + " mm");
            BoreLengthRatioInfo.Set("L/" + boreLengthRatio.ToString("F1"));
            MuzzleVelocityInfo.Set(muzzleVelocity.ToString("F0") + " m/s");
        }

        public override void BuildingUpdate()
        {
            base.BuildingUpdate();
            RecomputeBallistics();
        }

        public override void SimulateFixedUpdateAlways()
        {
            base.SimulateFixedUpdateAlways();
            //RecomputeBallistics(); // only recompute info in build
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            RecomputeBallistics();
            reloadTime = Mathf.Clamp(0.35f + caliberMm / 180f, 0.18f, 6f);
            reload = reloadTime;
            manualFireQueued = false;
            GuidHash = BlockBehaviour.BuildingBlock.Guid.GetHashCode();
            RailgunNet.Instance.Register(this);
            ShipState ship = OwnShip();
            if (ship != null)
            {
                OnAssignedToShip(ship);
            }
        }

        public override void OnAssignedToShip(ShipState ship)
        {
            SpaceCombatRegistry.RegisterSubsystem<IShipGun>(PlayerID, this, ship.Guns);
            registered = true;
        }

        public override void OnSimulateStop()
        {
            RailgunNet.Instance.Unregister(this);
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem<IShipGun>(this, ship.Guns);
            }
            registered = false;
        }

        public override void SimulateUpdateHost()
        {
            if (FireKey.IsPressed || FireKey.EmulationPressed() || FireKey.EmulationHeld())
            {
                manualFireQueued = true;
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            if (!registered)
            {
                ShipState ship = OwnShip();
                if (ship != null)
                {
                    SpaceCombatRegistry.RegisterSubsystem<IShipGun>(PlayerID, this, ship.Guns);
                    registered = true;
                }
            }

            reload = Mathf.Min(reloadTime, reload + Time.fixedDeltaTime);
            if (manualFireQueued)
            {
                manualFireQueued = false;
                FireAt(transform.forward, 20f);
            }
        }

        public bool TryFireControl(FireSolution solution)
        {
            if (solution.Target == null || !FireControl.IsActive)
            {
                return false;
            }

            Vector3 aim = solution.AimPoint - ApproximateMuzzlePosition();
            if (aim.sqrMagnitude < 0.001f)
            {
                return false;
            }
            return FireAt(aim.normalized, solution.TimeToImpact);
        }

        public bool FireAt(Vector3 direction, float life)
        {
            if (reload < reloadTime)
            {
                return false;
            }

            float massFactor = caliberMm / 300f;
            float mass = Mathf.Max(0.05f, massFactor);

            // Price the shot on the Weapon bus by the potential muzzle damage it will deliver
            // (energy-usage algorithm), so every barrel — however scaled — costs the same energy
            // per point of damage. See SpaceBalance.
            float potentialDamage = SpaceBalance.KineticDamage(caliberMm, mass, muzzleVelocity);
            float energyPerShot = SpaceBalance.WeaponShotEnergy(EnergyPerShotBase, potentialDamage);

            ShipState ship = OwnShip();
            if (ship != null)
            {
                if (!ship.Energy.CanSupply(EnergyBus.Weapon, energyPerShot))
                {
                    return false;
                }
                ship.Energy.Request(EnergyBus.Weapon, energyPerShot);
            }

            reload = 0f;
            // Pure KE velocity term now — no caliber floor (ADR-0007). At the muzzle the round
            // deals exactly the potentialDamage priced above, less once a shield or prior
            // penetration has bled its relative speed.
            float velocityDamageCoefficient = SpaceBalance.KineticDamagePerKE;

            Vector3 muzzlePosition = ApproximateMuzzlePosition();
            Vector3 velocity = direction.normalized * muzzleVelocity + (Body == null ? Vector3.zero : Body.velocity);
            float lifetime = Mathf.Clamp(life + 2f, 2f, 12f);
            SpaceBallistics.SpawnKineticRound(
                muzzlePosition, direction.normalized, velocity, caliberMm,
                velocityDamageCoefficient, mass, lifetime, PlayerID, Team, muzzleVelocity);

            // FireAt only runs host-side (both fire paths hang off SimulateFixedUpdateHost) —
            // broadcast the launch so every client's matching barrel spawns a visual mirror round.
            if (StatMaster.isMP)
            {
                ModNetworking.SendToAll(RailgunNet.ShotMsg.CreateMessage(
                    PlayerID, GuidHash, muzzlePosition, direction.normalized, velocity, caliberMm, lifetime));
            }

            if (Body != null)
            {
                Body.AddForce(-direction.normalized * (caliberMm / 100f) * (muzzleVelocity / 500f) * 6f, ForceMode.Impulse);
            }
            return true;
        }

        // Client mirror of a host shot (routed here by RailgunNet.ShotReceiver via this barrel's
        // PlayerID+GuidHash). Spawns the same round — NetAuthority gates it to a visual-only march —
        // and resets the reload ring so the client's OnGUI progress circle animates in step. Muzzle
        // sound and tail glow come free from inside SpawnKineticRound. Mass is recomputed from the
        // received caliber exactly like the flak's ReceiveLargeShot.
        public void ReceiveShot(Vector3 position, Vector3 forward, Vector3 velocity, float shotCaliber, float lifetime)
        {
            reload = 0f;
            SpaceBallistics.SpawnKineticRound(
                position, forward, velocity, shotCaliber, SpaceBalance.KineticDamagePerKE,
                Mathf.Max(0.05f, shotCaliber / 300f), lifetime, PlayerID, Team, muzzleVelocity);
        }

        // Client-side reload ticking for the OnGUI ring (the host ticks inside
        // SimulateFixedUpdateHost, which never runs here) — the missile launcher's
        // TickReloadAndDoor idiom.
        private void FixedUpdate()
        {
            if (BlockBehaviour != null && BlockBehaviour.isSimulating && StatMaster.isClient)
            {
                reload = Mathf.Min(reloadTime, reload + Time.fixedDeltaTime);
            }
        }

        public Vector3 ApproximateMuzzlePosition()
        {
            float zScale = Mathf.Max(0.05f, Mathf.Abs(transform.localScale.z));
            return transform.position + transform.forward * BaseMuzzleForwardOffset * zScale;
        }

        public float CurrentMuzzleVelocity()
        {
            return muzzleVelocity;
        }

        private void OnGUI()
        {
            if (StatMaster.hudHidden || !BlockBehaviour.isSimulating)
            {
                return;
            }

            Camera camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            Vector3 screen = camera.WorldToScreenPoint(transform.position + transform.up * ReloadIconWorldOffset);
            if (screen.z <= 0f || screen.z >= 20f)
            {
                return;
            }

            Texture outCircle = ModResource.GetTexture("BS Migrated Gun Load Circle Out Texture").Texture;
            Texture inCircle = ModResource.GetTexture("BS Migrated Gun Load Circle In Texture").Texture;
            float progress = Mathf.Clamp01(reload / Mathf.Max(0.001f, reloadTime));
            float inSize = ReloadIconSize * progress;
            float x = screen.x - ReloadIconSize * 0.5f;
            float y = camera.pixelHeight - screen.y - ReloadIconSize * 0.5f;

            GUI.DrawTexture(new Rect(x, y, ReloadIconSize, ReloadIconSize), outCircle);
            GUI.DrawTexture(new Rect(screen.x - inSize * 0.5f, camera.pixelHeight - screen.y - inSize * 0.5f, inSize, inSize), inCircle);
        }
    }

    public class SpaceGunnerBlock : SpaceBlock
    {
        public MKey ActiveSwitch;
        public MKey FireKey;
        public MKey OrientationLeftKey;
        public MKey OrientationRightKey;
        public MKey PitchUpKey;
        public MKey PitchDownKey;
        public MToggle DefaultActive;
        public MText GunGroup;
        public MSlider AimTolerance;
        public MSlider TrackingSpeed;
        public MFireChannel FireChannel;

        public int GuidHash { get; private set; }

        // See agent-besiege-mod-guide.md's "注册时序规范" — retried each tick until it
        // succeeds, since ShipState may not exist yet when this OnSimulateStart runs.
        private bool registered;

        private const float BindRefreshInterval = 0.5f;
        private const float ActiveIconSize = 44f;
        private const float ActiveIconWorldOffset = 0.45f;
        private const int LinkLineCount = 12;

        private readonly List<SpaceGunnerHinge> orientationHinges = new List<SpaceGunnerHinge>();
        private readonly List<SpaceGunnerHinge> pitchHinges = new List<SpaceGunnerHinge>();
        private readonly List<IShipGun> boundGuns = new List<IShipGun>();
        private readonly GameObject[] linkLines = new GameObject[LinkLineCount];
        private bool active;
        private bool fireEmulated;
        private float nextBindRefresh;
        private IShipGun referenceGun;
        // This gunner's personal channel-0 lottery ticket (ADR-0014): which of the ship's up-to-4
        // channel-0 threats it steers its bound guns at, sticky until that target drops off the list.
        private readonly FireChannelAssignment channel0Assignment = new FireChannelAssignment();

        // Rate feed-forward, ADR-0016. EaseStep alone is proportional control: while the aim
        // direction rotates at ω (a moving target's lead point always does), the loop settles
        // with a steady error of ω·dt/0.2 on the trailing side — the guns systematically aim
        // short of the computed lead. Feeding the setpoint's own per-tick rotation into the
        // hinge step cancels that: AddAngle is an integrator, so with the ramp rate supplied
        // the P term only carries residual disturbance and the mean lag goes to zero, motor
        // lag included. The rate is measured in the ship frame (own-ship rotation must not
        // feed back — the error term already handles it), lightly smoothed because track
        // velocities step at radar scan updates, and reset on any discontinuity: target
        // switch, a skipped tick, or a same-target aim jump past AimRateJumpResetDegrees.
        private const float AimRateSmoothing = 0.25f;
        private const float AimRateJumpResetDegrees = 5f;

        private bool hasAimHistory;
        private Vector3 prevAimShipLocal;
        private Vector3 aimRateShipLocal; // right-handed rotation vector, deg per tick, ship frame
        private ITrackable aimRateTarget;
        private int aimRateTick;
        private int hostTick;

        // Empirical hinge direction calibration. Whether AngleToBe += x turns the gun in the
        // +x direction around a hinge's axis depends on how the hinge was placed/flipped, and
        // deriving it from Wheel.Flipped alone proved unreliable. So right after simulation
        // start the gunner nudges its bound hinges a few degrees (orientation group first,
        // then pitch group), watches which way the reference gun actually rotates around each
        // hinge's axis, and records a per-hinge sign that DriveHinges uses from then on. A
        // hinge that shows no measurable response (e.g. jammed against its limit) falls back
        // to the Flipped heuristic.
        private enum HingeCalibrationPhase { NotStarted, SettlingOrientation, WaitingForOrientationRevert, SettlingPitch, Done }

        private const int CalibrationSettleTicks = 30;
        private const int CalibrationRevertSettleTicks = 15;
        private const float CalibrationNudgeDegrees = 4f;
        private const float CalibrationMinResponseDegrees = 0.25f;

        private HingeCalibrationPhase calibrationPhase;
        private int calibrationTicks;
        private IShipGun calibrationGun;
        private readonly Dictionary<SpaceGunnerHinge, float> hingeSigns = new Dictionary<SpaceGunnerHinge, float>();
        private readonly Dictionary<SpaceGunnerHinge, Vector3> calibrationBaselines = new Dictionary<SpaceGunnerHinge, Vector3>();
        private readonly Dictionary<SpaceGunnerHinge, float> calibrationApplied = new Dictionary<SpaceGunnerHinge, float>();

        public override bool EmulatesAnyKeys { get { return true; } }

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Space Gunner";
            ActiveSwitch = AddKey("Switch Active", "BSSpaceGunnerActive", KeyCode.None);
            FireKey = AddEmulatorKey("Fire", "BSSpaceGunnerFire", KeyCode.C);
            OrientationLeftKey = AddEmulatorKey("Orientation Left", "BSSpaceGunnerLeft", KeyCode.LeftArrow);
            OrientationRightKey = AddEmulatorKey("Orientation Right", "BSSpaceGunnerRight", KeyCode.RightArrow);
            PitchUpKey = AddEmulatorKey("Pitch Up", "BSSpaceGunnerUp", KeyCode.UpArrow);
            PitchDownKey = AddEmulatorKey("Pitch Down", "BSSpaceGunnerDown", KeyCode.DownArrow);
            DefaultActive = AddToggle("Default Active", "BSSpaceGunnerDefault", true);
            GunGroup = AddText("Gun Group", "BSSpaceGunnerGroup", "g0");
            AimTolerance = AddSlider("Aim Tolerance", "BSSpaceGunnerTolerance", 12f, 1f, 45f);
            TrackingSpeed = AddSlider("Tracking Speed", "BSSpaceGunnerTracking", 90f, 5f, 360f);
            FireChannel = AddFireChannel("Fire Channels", "BSSpaceGunnerFireChannel");
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            GuidHash = BlockBehaviour.BuildingBlock.Guid.GetHashCode();
            active = DefaultActive.IsActive;
            channel0Assignment.Clear();
            fireEmulated = false;
            nextBindRefresh = 0f;
            calibrationPhase = HingeCalibrationPhase.NotStarted;
            calibrationTicks = 0;
            calibrationGun = null;
            hingeSigns.Clear();
            calibrationBaselines.Clear();
            calibrationApplied.Clear();
            hasAimHistory = false;
            aimRateShipLocal = Vector3.zero;
            aimRateTarget = null;
            ShipState ship = OwnShip();
            if (ship != null)
            {
                OnAssignedToShip(ship);
            }
            SpaceGunnerNet.Instance.Register(this);
            RefreshBindings(true);
        }

        public override void OnAssignedToShip(ShipState ship)
        {
            SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Gunners);
            registered = true;
        }

        public override void OnSimulateStop()
        {
            CancelCalibrationInProgress();
            hingeSigns.Clear();
            StopFireEmulation();
            ReleaseHinges();
            HideLinkLines();
            SpaceGunnerNet.Instance.Unregister(this);
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Gunners);
            }
            registered = false;
        }

        private void Update()
        {
            if (!BlockBehaviour.isSimulating || !SpaceCombatRuntime.ShowArmorHP)
            {
                HideLinkLines();
                return;
            }

            ShowGroupLine();
        }

        public override void SimulateUpdateHost()
        {
            if (ActiveSwitch.IsPressed)
            {
                SetActive(!active);
                SendActive();
            }
        }

        public override void SimulateUpdateClient()
        {
            if (ActiveSwitch.IsPressed)
            {
                SetActive(!active);
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            // Counted before any early-out so the feed-forward can tell "aimed last tick too"
            // from "came back after a gap" — a stale prevAim sample must not be differentiated.
            hostTick++;
            ShipState ship = OwnShip();
            if (ship != null && !registered)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Gunners);
                registered = true;
            }

            if (!active)
            {
                CancelCalibrationInProgress();
                StopFireEmulation();
                ReleaseHinges();
                return;
            }

            if (ship == null)
            {
                StopFireEmulation();
                return;
            }

            RefreshBindings(false);
            if (referenceGun == null)
            {
                CancelCalibrationInProgress();
                StopFireEmulation();
                return;
            }

            if (!EnsureHingeCalibration())
            {
                StopFireEmulation();
                return;
            }

            FireSolution solution;
            if (!TryGetSolution(ship, out solution))
            {
                StopFireEmulation();
                return;
            }

            Vector3 delta = solution.AimPoint - referenceGun.ApproximateMuzzlePosition();
            if (delta.sqrMagnitude < 0.001f)
            {
                StopFireEmulation();
                return;
            }

            Vector3 aimDirection = delta.normalized;
            Vector3 aimRateWorld = UpdateAimFeedForward(ship, solution.Target, aimDirection);
            DriveHinges(orientationHinges, aimDirection, aimRateWorld);
            DriveHinges(pitchHinges, aimDirection, aimRateWorld);

            bool ready = Vector3.Angle(referenceGun.transform.forward, aimDirection) <= AimTolerance.Value;
            SetFireEmulation(ready);
        }

        public void ReceiveActive(bool value)
        {
            SetActive(value);
        }

        private void SetActive(bool value)
        {
            if (active == value)
            {
                return;
            }

            active = value;
            if (!active)
            {
                CancelCalibrationInProgress();
                StopFireEmulation();
                ReleaseHinges();
            }
            else
            {
                RefreshBindings(true);
            }
        }

        private bool TryGetSolution(ShipState ship, out FireSolution solution)
        {
            // Multi-channel fire control (ADR-0010): channel 0 first, then the best
            // angle/distance score among this gunner's other enabled channels. No arc check —
            // the turret's real reach lives in its player-built hinge limits, which are opaque
            // here; an unreachable target simply never aligns, so fire emulation stays off. The
            // captain's IFF override is applied per candidate inside TrySelectSolution. Channel 0
            // itself is the sole point-defense target source now (ADR-0012 retired the separate
            // DefenseDirectorBlock/DefensiveSolution fallback that used to live here) — the
            // captain's auto air-defence lock already covers bare cannon shells/large projectiles
            // via ship.Tracks, not just ILockable ships/missiles, so this single call is enough.
            return FireChannels.TrySelectSolution(ship, FireChannel.Value, channel0Assignment, referenceGun.ApproximateMuzzlePosition(),
                referenceGun.CurrentMuzzleVelocity(), referenceGun.transform.forward, float.MaxValue, null, out solution);
        }

        private void InitLinkLines()
        {
            for (int i = 0; i < LinkLineCount; i++)
            {
                if (linkLines[i] != null)
                {
                    continue;
                }

                Transform existing = transform.Find("line" + i.ToString());
                if (existing != null)
                {
                    linkLines[i] = existing.gameObject;
                    linkLines[i].SetActive(false);
                    continue;
                }

                linkLines[i] = new GameObject("line" + i.ToString());
                linkLines[i].transform.SetParent(gameObject.transform);
                LineRenderer renderer = linkLines[i].AddComponent<LineRenderer>();
                renderer.material = new Material(Shader.Find("Particles/Additive"));
                renderer.SetColors(Color.red, Color.yellow);
                renderer.SetWidth(0.1f, 0.05f);
                linkLines[i].SetActive(false);
            }
        }

        private void ShowGroupLine()
        {
            InitLinkLines();
            HideLinkLines();
            RefreshBindings(false);

            int lineIndex = 0;
            for (int i = 0; i < boundGuns.Count && lineIndex < LinkLineCount; i++)
            {
                IShipGun gun = boundGuns[i];
                if (gun == null)
                {
                    continue;
                }

                SetLinkLine(lineIndex, gun.transform.position);
                lineIndex++;
            }
            for (int i = 0; i < orientationHinges.Count && lineIndex < LinkLineCount; i++)
            {
                SpaceGunnerHinge hinge = orientationHinges[i];
                if (hinge == null || !hinge.IsValid)
                {
                    continue;
                }

                SetLinkLine(lineIndex, hinge.transform.position);
                lineIndex++;
            }
            for (int i = 0; i < pitchHinges.Count && lineIndex < LinkLineCount; i++)
            {
                SpaceGunnerHinge hinge = pitchHinges[i];
                if (hinge == null || !hinge.IsValid)
                {
                    continue;
                }

                SetLinkLine(lineIndex, hinge.transform.position);
                lineIndex++;
            }
        }

        private void SetLinkLine(int index, Vector3 target)
        {
            if (index < 0 || index >= linkLines.Length || linkLines[index] == null)
            {
                return;
            }

            LineRenderer renderer = linkLines[index].GetComponent<LineRenderer>();
            if (renderer == null)
            {
                return;
            }

            renderer.SetPosition(0, transform.position);
            renderer.SetPosition(1, target);
            linkLines[index].SetActive(true);
        }

        private void HideLinkLines()
        {
            for (int i = 0; i < linkLines.Length; i++)
            {
                if (linkLines[i] != null)
                {
                    linkLines[i].SetActive(false);
                }
            }
        }

        private void RefreshBindings(bool force)
        {
            if (!force && Time.time < nextBindRefresh && referenceGun != null)
            {
                return;
            }

            nextBindRefresh = Time.time + BindRefreshInterval;
            ReleaseHinges();
            orientationHinges.Clear();
            pitchHinges.Clear();
            boundGuns.Clear();

            // Hinge ownership is a physics claim (the owned hinge forces wheel state every tick),
            // so clients never take it — their replica turrets are posed by vanilla sync. The
            // binding lists themselves are still built for the link-line overlay.
            bool takeOwnership = active && !NetAuthority.IsClient;
            IList<SpaceGunnerHinge> hinges = SpaceGunnerHingeRegistry.HingesFor(PlayerID);
            for (int i = 0; i < hinges.Count; i++)
            {
                SpaceGunnerHinge hinge = hinges[i];
                if (hinge == null || !hinge.IsValid)
                {
                    continue;
                }

                if (MatchesAny(hinge, OrientationLeftKey, OrientationRightKey))
                {
                    orientationHinges.Add(hinge);
                    if (takeOwnership)
                    {
                        hinge.AddOwner(this);
                    }
                }
                if (MatchesAny(hinge, PitchUpKey, PitchDownKey))
                {
                    pitchHinges.Add(hinge);
                    if (takeOwnership)
                    {
                        hinge.AddOwner(this);
                    }
                }
            }

            ShipState ship = OwnShip();
            if (ship != null)
            {
                for (int i = 0; i < ship.Guns.Count; i++)
                {
                    IShipGun gun = ship.Guns[i];
                    if (gun != null && gun.GunGroup.Value == GunGroup.Value && MKeyMatch.SharesBinding(gun.FireKey, FireKey))
                    {
                        boundGuns.Add(gun);
                    }
                }
            }

            SelectReferenceGun();
        }

        private void SelectReferenceGun()
        {
            if (referenceGun != null && boundGuns.Contains(referenceGun) && referenceGun.FireControl.IsActive)
            {
                return;
            }

            referenceGun = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < boundGuns.Count; i++)
            {
                IShipGun gun = boundGuns[i];
                if (gun == null || !gun.FireControl.IsActive)
                {
                    continue;
                }

                float distance = (gun.transform.position - transform.position).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    referenceGun = gun;
                }
            }
        }

        // Measures how fast the aim direction itself is rotating and returns that rate as a
        // world-frame rotation vector in degrees per tick, for DriveHinges to feed forward.
        // Measured and smoothed in the ship frame so the ship's own rotation stays out of it;
        // see the field comment for why any discontinuity resets the rate to zero.
        private Vector3 UpdateAimFeedForward(ShipState ship, ITrackable target, Vector3 aimDirection)
        {
            Quaternion shipRotation = ship.Core != null ? ship.Core.transform.rotation : transform.rotation;
            Vector3 aimLocal = Quaternion.Inverse(shipRotation) * aimDirection;

            bool continuous = hasAimHistory && aimRateTarget == target && hostTick == aimRateTick + 1;
            if (continuous)
            {
                float angleDeg = Vector3.Angle(prevAimShipLocal, aimLocal);
                if (angleDeg > AimRateJumpResetDegrees)
                {
                    aimRateShipLocal = Vector3.zero;
                }
                else
                {
                    Vector3 rotationAxis = Vector3.Cross(prevAimShipLocal, aimLocal);
                    Vector3 instantRate = rotationAxis.sqrMagnitude < 0.000001f
                        ? Vector3.zero
                        : rotationAxis.normalized * angleDeg;
                    aimRateShipLocal = Vector3.Lerp(aimRateShipLocal, instantRate, AimRateSmoothing);
                }
            }
            else
            {
                aimRateShipLocal = Vector3.zero;
            }

            hasAimHistory = true;
            prevAimShipLocal = aimLocal;
            aimRateTarget = target;
            aimRateTick = hostTick;
            return shipRotation * aimRateShipLocal;
        }

        private void DriveHinges(List<SpaceGunnerHinge> hinges, Vector3 aimDirection, Vector3 aimRateWorld)
        {
            float maxStep = TrackingSpeed.Value * Time.fixedDeltaTime;
            for (int i = 0; i < hinges.Count; i++)
            {
                SpaceGunnerHinge hinge = hinges[i];
                if (hinge == null || !hinge.IsValid)
                {
                    continue;
                }

                float sign;
                if (!hingeSigns.TryGetValue(hinge, out sign))
                {
                    // Not calibrated yet (e.g. hinge bound mid-simulation); EnsureHingeCalibration
                    // will pick it up on the next tick — don't steer it blindly in the meantime.
                    continue;
                }

                float signedAngle = hinge.SignedAngleTo(referenceGun.transform.forward, aimDirection);
                // Both terms share SignedAngleTo's right-handed convention, so the rate vector's
                // projection on this hinge's axis is exactly the degrees the setpoint moves
                // around it this tick. The clamp keeps the player's TrackingSpeed as the total
                // slew budget — during a max-rate slew the feed-forward changes nothing.
                float feedForward = Vector3.Dot(aimRateWorld, hinge.WorldAxis);
                float step = Mathf.Clamp(EaseStep(signedAngle, maxStep) + feedForward, -maxStep, maxStep);
                hinge.AddAngle(step * sign);
            }
        }

        // Constant-speed while the error is outside the ease zone, then ease the last
        // EaseZoneDegrees down proportionally (20% of the remainder per tick) instead of
        // commanding the full remaining angle in one step. Snapping AngleToBe straight to the
        // target as soon as it's within one step's reach was giving the physical hinge a new
        // close-range target every tick with no deceleration, which let its own motor overshoot
        // and rebound — visible as back-and-forth twitching once the turret got near its aim
        // point. This only runs during tracking (after calibration is Done), so it never
        // interacts with the calibration nudge/measurement.
        private const float EaseZoneDegrees = 2f;

        private static float EaseStep(float delta, float maxStep)
        {
            if (Mathf.Abs(delta) < EaseZoneDegrees)
            {
                return delta * 0.2f;
            }
            return Mathf.Clamp(delta, -maxStep, maxStep);
        }

        // Returns true once every bound hinge has a measured direction sign. While measuring it
        // holds fire and skips aiming: nudge the orientation hinges, wait for the physics to
        // respond, read which way the reference gun turned, restore, then repeat for pitch.
        private bool EnsureHingeCalibration()
        {
            if (calibrationPhase == HingeCalibrationPhase.Done)
            {
                if (AllBoundHingesCalibrated())
                {
                    return true;
                }
                calibrationPhase = HingeCalibrationPhase.NotStarted;
            }

            if (calibrationPhase == HingeCalibrationPhase.NotStarted)
            {
                calibrationGun = referenceGun;
                calibrationTicks = 0;
                if (StartCalibrationNudge(orientationHinges))
                {
                    calibrationPhase = HingeCalibrationPhase.SettlingOrientation;
                }
                else if (StartCalibrationNudge(pitchHinges))
                {
                    calibrationPhase = HingeCalibrationPhase.SettlingPitch;
                }
                else
                {
                    calibrationPhase = HingeCalibrationPhase.Done;
                    return true;
                }
                return false;
            }

            if (calibrationGun != referenceGun)
            {
                // The gun we were watching disappeared or was swapped mid-measurement; the
                // baselines are meaningless now, so revert the nudge and start over.
                CancelCalibrationInProgress();
                return false;
            }

            if (calibrationPhase == HingeCalibrationPhase.WaitingForOrientationRevert)
            {
                // Orientation's own AddAngle(-appliedValue) revert command was issued in
                // FinishCalibrationNudge below, but the physical hinge takes time to catch up
                // to it. Give that a few ticks to settle before nudging pitch, so the two
                // moves don't visibly overlap (orientation still easing back to zero while
                // pitch nudges out — looked like the turret was moving diagonally). The pitch
                // measurement is taken in the pitch hinge's own rotating local frame, so a
                // still-moving orientation hinge shouldn't actually corrupt the sign on a
                // normal nested gimbal — this is just removing the overlap outright instead of
                // relying on that.
                calibrationTicks++;
                if (calibrationTicks < CalibrationRevertSettleTicks)
                {
                    return false;
                }

                if (StartCalibrationNudge(pitchHinges))
                {
                    calibrationPhase = HingeCalibrationPhase.SettlingPitch;
                    calibrationTicks = 0;
                    return false;
                }

                calibrationPhase = HingeCalibrationPhase.Done;
                return AllBoundHingesCalibrated();
            }

            calibrationTicks++;
            if (calibrationTicks < CalibrationSettleTicks)
            {
                return false;
            }

            FinishCalibrationNudge();
            if (calibrationPhase == HingeCalibrationPhase.SettlingOrientation)
            {
                calibrationPhase = HingeCalibrationPhase.WaitingForOrientationRevert;
                calibrationTicks = 0;
                return false;
            }

            calibrationPhase = HingeCalibrationPhase.Done;
            return AllBoundHingesCalibrated();
        }

        private bool StartCalibrationNudge(List<SpaceGunnerHinge> hinges)
        {
            calibrationBaselines.Clear();
            calibrationApplied.Clear();
            if (referenceGun == null)
            {
                return false;
            }

            for (int i = 0; i < hinges.Count; i++)
            {
                SpaceGunnerHinge hinge = hinges[i];
                if (hinge == null || !hinge.IsValid || hinge.Wheel == null || calibrationApplied.ContainsKey(hinge))
                {
                    continue;
                }

                hinge.AddOwner(this);
                float before = hinge.Wheel.AngleToBe;
                hinge.AddAngle(CalibrationNudgeDegrees * ChooseNudgeSign(hinge));
                float applied = hinge.Wheel.AngleToBe - before;
                if (Mathf.Abs(applied) < 0.01f)
                {
                    // Limits left no room to move at all — nothing to measure.
                    hingeSigns[hinge] = FallbackSign(hinge);
                    continue;
                }

                calibrationApplied[hinge] = applied;
                calibrationBaselines[hinge] = hinge.ToLocalDirection(referenceGun.transform.forward);
            }
            return calibrationApplied.Count > 0;
        }

        private void FinishCalibrationNudge()
        {
            foreach (KeyValuePair<SpaceGunnerHinge, float> pair in calibrationApplied)
            {
                SpaceGunnerHinge hinge = pair.Key;
                if (hinge == null || hinge.Wheel == null)
                {
                    continue;
                }

                float response = 0f;
                Vector3 baseline;
                if (referenceGun != null && calibrationBaselines.TryGetValue(hinge, out baseline))
                {
                    response = hinge.SignedAngleAroundAxis(baseline, hinge.ToLocalDirection(referenceGun.transform.forward));
                }

                // sign answers: "AddAngle(+x) rotates the gun which way around this axis?"
                // response/applied both carry direction, so the mapping is their sign product.
                hingeSigns[hinge] = Mathf.Abs(response) >= CalibrationMinResponseDegrees
                    ? Mathf.Sign(response) * Mathf.Sign(pair.Value)
                    : FallbackSign(hinge);
                hinge.AddAngle(-pair.Value);
            }
            calibrationApplied.Clear();
            calibrationBaselines.Clear();
        }

        private void CancelCalibrationInProgress()
        {
            foreach (KeyValuePair<SpaceGunnerHinge, float> pair in calibrationApplied)
            {
                if (pair.Key != null && pair.Key.Wheel != null)
                {
                    pair.Key.AddAngle(-pair.Value);
                }
            }
            calibrationApplied.Clear();
            calibrationBaselines.Clear();
            if (calibrationPhase != HingeCalibrationPhase.Done)
            {
                calibrationPhase = HingeCalibrationPhase.NotStarted;
                calibrationTicks = 0;
            }
        }

        private bool AllBoundHingesCalibrated()
        {
            for (int i = 0; i < orientationHinges.Count; i++)
            {
                SpaceGunnerHinge hinge = orientationHinges[i];
                if (hinge != null && hinge.IsValid && !hingeSigns.ContainsKey(hinge))
                {
                    return false;
                }
            }
            for (int i = 0; i < pitchHinges.Count; i++)
            {
                SpaceGunnerHinge hinge = pitchHinges[i];
                if (hinge != null && hinge.IsValid && !hingeSigns.ContainsKey(hinge))
                {
                    return false;
                }
            }
            return true;
        }

        private static float ChooseNudgeSign(SpaceGunnerHinge hinge)
        {
            // Nudge toward whichever side has more travel left, so a turret parked against
            // one of its limits still produces a measurable response.
            SteeringWheel wheel = hinge.Wheel;
            if (wheel == null || wheel.LimitsSlider == null || !wheel.LimitsSlider.IsActive)
            {
                return 1f;
            }

            float positiveRoom = wheel.LimitsSlider.Max - wheel.AngleToBe;
            float negativeRoom = wheel.AngleToBe + wheel.LimitsSlider.Min;
            return positiveRoom >= negativeRoom ? 1f : -1f;
        }

        private static float FallbackSign(SpaceGunnerHinge hinge)
        {
            return hinge.Wheel != null && hinge.Wheel.Flipped ? -1f : 1f;
        }

        private void ReleaseHinges()
        {
            for (int i = 0; i < orientationHinges.Count; i++)
            {
                if (orientationHinges[i] != null)
                {
                    orientationHinges[i].RemoveOwner(this);
                }
            }
            for (int i = 0; i < pitchHinges.Count; i++)
            {
                if (pitchHinges[i] != null)
                {
                    pitchHinges[i].RemoveOwner(this);
                }
            }
        }

        private static bool MatchesAny(SpaceGunnerHinge hinge, MKey a, MKey b)
        {
            return hinge.MatchesLeft(a) || hinge.MatchesRight(a) || hinge.MatchesLeft(b) || hinge.MatchesRight(b);
        }

        private void SetFireEmulation(bool value)
        {
            if (fireEmulated == value)
            {
                return;
            }

            EmulateKeys(new MKey[0], FireKey, value);
            fireEmulated = value;
        }

        private void StopFireEmulation()
        {
            SetFireEmulation(false);
        }

        private void SendActive()
        {
            if (StatMaster.isMP)
            {
                ModNetworking.SendToAll(SpaceGunnerNet.ActiveMsg.CreateMessage(PlayerID, GuidHash, active));
            }
        }

        private void OnGUI()
        {
            if (StatMaster.hudHidden || !BlockBehaviour.isSimulating || !active)
            {
                return;
            }

            Camera camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            Vector3 screen = camera.WorldToScreenPoint(transform.position + transform.up * ActiveIconWorldOffset);
            if (screen.z <= 0f)
            {
                return;
            }

            Texture icon = ModResource.GetTexture("BS Migrated Gunner Alert Texture").Texture;
            GUI.DrawTexture(new Rect(
                screen.x - ActiveIconSize * 0.5f,
                camera.pixelHeight - screen.y - ActiveIconSize * 0.5f,
                ActiveIconSize,
                ActiveIconSize), icon);
        }
    }
}
