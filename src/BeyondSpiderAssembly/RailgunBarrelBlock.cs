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
            if (FireKey.IsPressed)
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
        public MToggle DefaultActive;
        public MText GunGroup;
        public MSlider AimTolerance;

        private bool active;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Space Gunner";
            ActiveSwitch = AddKey("Switch Active", "BSSpaceGunnerActive", KeyCode.None);
            DefaultActive = AddToggle("Default Active", "BSSpaceGunnerDefault", true);
            GunGroup = AddText("Gun Group", "BSSpaceGunnerGroup", "g0");
            AimTolerance = AddSlider("Aim Tolerance", "BSSpaceGunnerTolerance", 12f, 1f, 45f);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            active = DefaultActive.IsActive;
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Gunners);
            }
        }

        public override void OnSimulateStop()
        {
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
                active = !active;
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            if (!active)
            {
                return;
            }

            ShipState ship = OwnShip();
            if (ship == null)
            {
                return;
            }

            FireSolution solution = (ship.Priority == CommandPriority.AntiShip && ship.LockedSolution.Target != null)
                ? ship.LockedSolution
                : ship.DefensiveSolution;
            if (solution.Target == null)
            {
                return;
            }

            for (int i = 0; i < ship.Guns.Count; i++)
            {
                RailgunBarrelBlock gun = ship.Guns[i];
                if (gun == null || !gun.FireControl.IsActive || gun.GunGroup.Value != GunGroup.Value)
                {
                    continue;
                }

                Vector3 delta = solution.AimPoint - gun.transform.position;
                if (Vector3.Angle(gun.transform.forward, delta) <= AimTolerance.Value)
                {
                    gun.TryFireControl(solution);
                }
            }
        }
    }
}
