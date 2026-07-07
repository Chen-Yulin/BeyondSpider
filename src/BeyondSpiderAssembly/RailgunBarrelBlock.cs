using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class RailgunBarrelBlock : SpaceBlock
    {
        public MKey FireKey;
        public MToggle FireControl;
        public MText GunGroup;
        public MSlider Caliber;
        public MSlider MuzzleVelocity;

        private const float ReloadIconSize = 50f;
        private const float ReloadIconWorldOffset = 0f;
        private const float ProjectileVisualScale = 3f;

        private float reloadTime;
        private float reload;
        private bool manualFireQueued;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Railgun Barrel";
            FireKey = AddKey("Fire", "BSRailgunFire", KeyCode.C);
            FireControl = AddToggle("Fire Control", "BSRailgunFireControl", false);
            GunGroup = AddText("Gun Group", "BSRailgunGroup", "g0");
            Caliber = AddSlider("Caliber", "BSRailgunCaliber", 90f, 20f, 300f);
            MuzzleVelocity = AddSlider("Muzzle Velocity", "BSRailgunVelocity", 2600f, 1200f, 6000f);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            reloadTime = Mathf.Clamp(0.35f + Caliber.Value / 180f, 0.18f, 6f);
            reload = reloadTime;
            manualFireQueued = false;
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Guns);
            }
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Guns);
            }
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

            Vector3 aim = solution.AimPoint - transform.position;
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

            float massFactor = Caliber.Value / 300f;
            float energyPerShot = 40f + massFactor * MuzzleVelocity.Value * MuzzleVelocity.Value * 0.00035f;

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
            float baseDamage = Caliber.Value * 1.1f;
            const float velocityDamageCoefficient = 0.0006f;

            GameObject round = new GameObject("BeyondSpider Railgun Slug");
            round.transform.position = transform.position + transform.forward * 1.2f;
            round.transform.rotation = Quaternion.LookRotation(direction.normalized, transform.up);
            Rigidbody rb = round.AddComponent<Rigidbody>();
            rb.interpolation = RigidbodyInterpolation.Extrapolate;
            rb.mass = Mathf.Max(0.05f, massFactor);
            rb.drag = 0.005f;
            rb.useGravity = false;
            rb.velocity = direction.normalized * MuzzleVelocity.Value + (Body == null ? Vector3.zero : Body.velocity);

            GameObject vis = new GameObject("CannonVis");
            vis.transform.SetParent(round.transform);
            vis.transform.localPosition = Vector3.zero;
            vis.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            vis.transform.localScale = Vector3.one * Caliber.Value / 500f * ProjectileVisualScale;
            MeshFilter meshFilter = vis.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = ModResource.GetMesh("Cannon Mesh").Mesh;
            MeshRenderer meshRenderer = vis.AddComponent<MeshRenderer>();
            meshRenderer.material.mainTexture = ModResource.GetTexture("Cannon Texture").Texture;

            SpaceKineticRound projectile = round.AddComponent<SpaceKineticRound>();
            projectile.OwnerPlayerID = PlayerID;
            projectile.OwnerTeam = Team;
            projectile.Damage = baseDamage;
            projectile.VelocityDamageCoefficient = velocityDamageCoefficient;
            projectile.Lifetime = Mathf.Clamp(life + 2f, 2f, 12f);
            projectile.MassEstimate = rb.mass;
            projectile.Caliber = Caliber.Value;
            projectile.RadiusValue = Mathf.Clamp(Caliber.Value / 100f * ProjectileVisualScale, 1.2f, 7.5f);
            projectile.SpawnImpactSpark = true;
            projectile.UseRaycastDetection = true;
            SpaceEffectAssets.AttachRailgunTailGlow(round.transform, rb, Caliber.Value, MuzzleVelocity.Value);

            SpaceEffectAssets.PlayMuzzleSound(transform, Caliber.Value);

            if (Body != null)
            {
                Body.AddForce(-direction.normalized * (Caliber.Value / 100f) * (MuzzleVelocity.Value / 500f) * 6f, ForceMode.Impulse);
            }
            return true;
        }

        public Vector3 ApproximateMuzzlePosition()
        {
            return transform.position + transform.forward * 1.2f;
        }

        public float CurrentMuzzleVelocity()
        {
            return MuzzleVelocity.Value;
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
            if (screen.z <= 0f)
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

        public int GuidHash { get; private set; }

        private const float BindRefreshInterval = 0.5f;
        private const float ActiveIconSize = 44f;
        private const float ActiveIconWorldOffset = 0.45f;

        private readonly List<SpaceGunnerHinge> orientationHinges = new List<SpaceGunnerHinge>();
        private readonly List<SpaceGunnerHinge> pitchHinges = new List<SpaceGunnerHinge>();
        private readonly List<RailgunBarrelBlock> boundGuns = new List<RailgunBarrelBlock>();
        private bool active;
        private bool fireEmulated;
        private float nextBindRefresh;
        private RailgunBarrelBlock referenceGun;

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
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            GuidHash = BlockBehaviour.BuildingBlock.Guid.GetHashCode();
            active = DefaultActive.IsActive;
            fireEmulated = false;
            nextBindRefresh = 0f;
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Gunners);
            }
            SpaceGunnerNet.Instance.Register(this);
            RefreshBindings(true);
        }

        public override void OnSimulateStop()
        {
            StopFireEmulation();
            ReleaseHinges();
            SpaceGunnerNet.Instance.Unregister(this);
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Gunners);
            }
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
            if (!active)
            {
                StopFireEmulation();
                ReleaseHinges();
                return;
            }

            ShipState ship = OwnShip();
            if (ship == null)
            {
                StopFireEmulation();
                return;
            }

            RefreshBindings(false);
            if (referenceGun == null)
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
            DriveHinges(orientationHinges, aimDirection);
            DriveHinges(pitchHinges, aimDirection);

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
            solution = default(FireSolution);
            if (ship.Priority == CommandPriority.AntiShip && ship.Captain != null)
            {
                if (ship.Captain.TryGetLockedFireSolution(referenceGun.ApproximateMuzzlePosition(), referenceGun.CurrentMuzzleVelocity(), out solution))
                {
                    return SpaceBallistics.IsHostile(this, solution.Target);
                }
            }

            if (ship.DefensiveSolution.Target == null)
            {
                return false;
            }

            solution = ship.DefensiveSolution;
            SensorTrack synthetic = new SensorTrack();
            synthetic.Position = solution.Target.Position;
            synthetic.Velocity = solution.Target.Velocity;
            solution.AimPoint = SpaceBallistics.AimPoint(referenceGun.ApproximateMuzzlePosition(), synthetic, referenceGun.CurrentMuzzleVelocity());
            return SpaceBallistics.IsHostile(this, solution.Target);
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
                    if (active)
                    {
                        hinge.AddOwner(this);
                    }
                }
                if (MatchesAny(hinge, PitchUpKey, PitchDownKey))
                {
                    pitchHinges.Add(hinge);
                    if (active)
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
                    RailgunBarrelBlock gun = ship.Guns[i];
                    if (gun != null && gun.GunGroup.Value == GunGroup.Value && gun.FireKey.Equals(FireKey))
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
                RailgunBarrelBlock gun = boundGuns[i];
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

        private void DriveHinges(List<SpaceGunnerHinge> hinges, Vector3 aimDirection)
        {
            float maxStep = TrackingSpeed.Value * Time.fixedDeltaTime;
            for (int i = 0; i < hinges.Count; i++)
            {
                SpaceGunnerHinge hinge = hinges[i];
                if (hinge == null || !hinge.IsValid)
                {
                    continue;
                }

                float signedAngle = hinge.SignedAngleTo(referenceGun.transform.forward, aimDirection);
                float step = Mathf.Clamp(signedAngle, -maxStep, maxStep);
                if (hinge.Wheel.Flipped)
                {
                    step = -step;
                }
                hinge.AddAngle(step);
            }
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
