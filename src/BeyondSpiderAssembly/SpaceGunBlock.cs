using System.Collections.Generic;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class SpaceGunBlock : SpaceBlock
    {
        public MKey FireKey;
        public MToggle FireControl;
        public MText GunGroup;
        public MSlider Caliber;
        public MSlider MuzzleVelocity;
        public MSlider EnergyPerShot;

        private float reloadTime;
        private float reload;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Space Gun";
            FireKey = AddKey("Fire", "BSSpaceGunFire", KeyCode.C);
            FireControl = AddToggle("Fire Control", "BSSpaceGunFireControl", false);
            GunGroup = AddText("Gun Group", "BSSpaceGunGroup", "g0");
            Caliber = AddSlider("Caliber", "BSSpaceGunCaliber", 155f, 20f, 510f);
            MuzzleVelocity = AddSlider("Muzzle Velocity", "BSSpaceGunVelocity", 520f, 80f, 1800f);
            EnergyPerShot = AddSlider("Energy Per Shot", "BSSpaceGunEnergy", 60f, 1f, 600f);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            reloadTime = Mathf.Clamp(0.35f + Caliber.Value / 180f, 0.18f, 6f);
            reload = reloadTime;
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

        public override void SimulateFixedUpdateHost()
        {
            reload = Mathf.Min(reloadTime, reload + Time.fixedDeltaTime);
            if (FireKey.IsPressed)
            {
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

            ShipState ship = OwnShip();
            if (ship != null && ship.Energy.Request(EnergyBus.Weapon, EnergyPerShot.Value) < 0.25f)
            {
                return false;
            }

            reload = 0f;
            GameObject round = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            round.name = "BeyondSpider Kinetic Round";
            round.transform.position = transform.position + transform.forward * 1.2f;
            round.transform.localScale = Vector3.one * Mathf.Clamp(Caliber.Value / 220f, 0.12f, 1.7f);
            Rigidbody rb = round.AddComponent<Rigidbody>();
            rb.mass = Mathf.Max(0.05f, Caliber.Value / 300f);
            rb.drag = 0.005f;
            rb.useGravity = false;
            rb.velocity = direction.normalized * MuzzleVelocity.Value + (Body == null ? Vector3.zero : Body.velocity);
            SpaceKineticRound projectile = round.AddComponent<SpaceKineticRound>();
            projectile.OwnerPlayerID = PlayerID;
            projectile.OwnerTeam = Team;
            projectile.Damage = Caliber.Value * 2f;
            projectile.Lifetime = Mathf.Clamp(life + 2f, 2f, 12f);

            if (Body != null)
            {
                Body.AddForce(-direction.normalized * Caliber.Value * 3f, ForceMode.Impulse);
            }
            return true;
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
            if (ship == null || ship.DefensiveSolution.Target == null)
            {
                return;
            }

            for (int i = 0; i < ship.Guns.Count; i++)
            {
                SpaceGunBlock gun = ship.Guns[i];
                if (gun == null || !gun.FireControl.IsActive || gun.GunGroup.Value != GunGroup.Value)
                {
                    continue;
                }

                Vector3 delta = ship.DefensiveSolution.AimPoint - gun.transform.position;
                if (Vector3.Angle(gun.transform.forward, delta) <= AimTolerance.Value)
                {
                    gun.TryFireControl(ship.DefensiveSolution);
                }
            }
        }
    }
}
