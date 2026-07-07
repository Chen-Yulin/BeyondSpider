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
        public MSlider Range;
        public MSlider EnergyPerSecond;
        public MSlider TrackingSpeed;
        public MLimits YawLimit;

        public int GuidHash { get; private set; }

        // See agent-besiege-mod-guide.md's "注册时序规范" — retried each tick until it
        // succeeds, since ShipState may not exist yet when this OnSimulateStart runs.
        private bool registered;

        private readonly List<string> typeList = new List<string>
        {
            "1x20mm",
            "3x25mm",
            "2x40mm",
            "4x40mm",
            "2x76mm",
            "2x100mm",
            "2x105mm",
            "2x127mm",
            "2x113mm",
            "2x134mm",
            "UK 4x40mm",
            "UK 8x40mm",
            "IJN 2x127mm"
        };

        private GameObject originVis;
        private GameObject buildBase;
        private GameObject simRoot;
        private GameObject baseObject;
        private GameObject gunObject;
        private Transform yawTransform;
        private Transform pitchTransform;
        private ParticleSystem smallShoot;

        private bool active;
        private bool hasTarget;
        private bool shooting;
        private float caliber = 20f;
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
        private float nextStateSync;
        private bool previousShooting;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Space Flak Turret";
            SwitchActive = AddKey("Switch Active", "BSSpaceFlakSwitch", KeyCode.None);
            Type = AddMenu("AA Type", 0, typeList, false);
            DefaultActive = AddToggle("Default Active", "BSSpaceFlakActive", true);
            Range = AddSlider("Range", "BSSpaceFlakRange", 460f, 80f, 1800f);
            EnergyPerSecond = AddSlider("Energy Use", "BSSpaceFlakEnergy", 125f, 10f, 900f);
            TrackingSpeed = AddSlider("Tracking Speed", "BSSpaceFlakTracking", 1.5f, 0.2f, 6f);
            YawLimit = AddLimits("Turret Orien Limit", "BSSpaceFlakYawLimit", 90f, 90f, 180f, new FauxTransform(new Vector3(0f, -0.5f, -0.5f), Quaternion.Euler(-90f, 0f, 0f), Vector3.one * 0.0001f));
            InitBuildModel();
        }

        public void Start()
        {
            gameObject.name = "BeyondSpider Space Flak Turret";
        }

        public override void BuildingUpdate()
        {
            HoldAppearance(false);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            GuidHash = BlockBehaviour.BuildingBlock.Guid.GetHashCode();
            active = DefaultActive.IsActive;
            ResolveType();
            originVis = transform.Find("Vis") == null ? null : transform.Find("Vis").gameObject;
            InitSimulationModel();
            HoldAppearance(true);
            SpaceFlakTurretNet.Instance.Register(this);

            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.FlakTurrets);
                registered = true;
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
            SpawnLargeRound(position, forward, velocity, shotCaliber, damage, lifetime, false);
        }

        private void UpdateFireControlTarget()
        {
            hasTarget = false;
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

            float muzzleVelocity = InitialVelocity(caliber);
            FireSolution solution;
            bool hasSolution = false;
            if (ship.Priority == CommandPriority.AntiShip && ship.Captain != null)
            {
                hasSolution = ship.Captain.TryGetLockedFireSolution(GetMuzzlePosition(), muzzleVelocity, out solution);
            }
            else
            {
                solution = default(FireSolution);
            }

            if (!hasSolution)
            {
                if (ship.DefensiveSolution.Target == null)
                {
                    SetSmallShoot(false);
                    return;
                }

                solution = ship.DefensiveSolution;
                SensorTrack synthetic = new SensorTrack();
                synthetic.Position = solution.Target.Position;
                synthetic.Velocity = solution.Target.Velocity;
                solution.AimPoint = SpaceBallistics.AimPoint(GetMuzzlePosition(), synthetic, muzzleVelocity);
                hasSolution = true;
            }

            ITrackable target = solution.Target;
            if (!SpaceBallistics.IsHostile(this, target))
            {
                SetSmallShoot(false);
                return;
            }

            Vector3 aimPoint = solution.AimPoint;
            Vector3 delta = aimPoint - GetMuzzlePosition();
            if (delta.magnitude > Range.Value)
            {
                SetSmallShoot(false);
                return;
            }

            hasTarget = true;
            Quaternion localAim = Quaternion.Inverse(transform.rotation) * Quaternion.LookRotation(delta.normalized, transform.up);
            Vector3 euler = localAim.eulerAngles;
            targetYaw = NormalizeAngle(euler.y);
            targetPitch = -NormalizeAngle(euler.x);
        }

        private void DriveTurret()
        {
            bool ready = false;
            if (hasTarget)
            {
                UpdateRandomError();
                float speed = caliber < 76f ? TrackingSpeed.Value : TrackingSpeed.Value * 0.5f;
                realYaw = StepAngle(realYaw, targetYaw, speed);
                realPitch = StepAngle(realPitch, targetPitch, speed);
                if (YawLimit.UseLimitsToggle.isDefaultValue)
                {
                    realYaw = Mathf.Clamp(realYaw, -YawLimit.Min, YawLimit.Max);
                }
                realPitch = Mathf.Clamp(realPitch, -5f, 90f);
                ready = Mathf.Abs(Mathf.DeltaAngle(realYaw, targetYaw)) + Mathf.Abs(Mathf.DeltaAngle(realPitch, targetPitch)) < 10f;
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
            float energy = ship == null ? 1f : ship.Energy.Request(EnergyBus.Weapon, EnergyPerSecond.Value * Time.fixedDeltaTime);
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
            if (ship == null || ship.DefensiveSolution.Target == null)
            {
                return;
            }

            HeavyNuclearMissileBlock missile = ship.DefensiveSolution.Target as HeavyNuclearMissileBlock;
            if (missile == null)
            {
                return;
            }

            float distance = Vector3.Distance(GetMuzzlePosition(), missile.Position);
            if (distance > Range.Value || Vector3.Angle(GetMuzzleForward(), missile.Position - GetMuzzlePosition()) > 10f)
            {
                return;
            }

            float speedPenalty = Mathf.Clamp01(1f - missile.Velocity.magnitude / 900f);
            float rangeFactor = Mathf.Clamp01(1f - distance / Range.Value);
            missile.ApplyDamage((caliber * gunCount) * (0.25f + rangeFactor + speedPenalty) * energy * Time.fixedDeltaTime * 12f);
        }

        private void FireLargeRound(float energy)
        {
            reload = 0f;
            alternateMuzzle = !alternateMuzzle;
            Vector3 forward = GetMuzzleForward();
            Vector3 position = GetMuzzlePosition() + forward + (alternateMuzzle ? -1f : 1f) * gunWidth * pitchTransform.right;
            Vector3 randomForce = RandomForce(caliber);
            Vector3 velocity = forward * InitialVelocity(caliber) + randomForce + (Body == null ? Vector3.zero : Body.velocity);
            float lifetime = Mathf.Clamp(SpaceBallistics.EstimateInterceptTime(position, position + forward * Range.Value, Vector3.zero, InitialVelocity(caliber)) + 2f, 2f, 10f);
            float damage = caliber * (1.6f + energy);
            SpawnLargeRound(position, forward, velocity, caliber, damage, lifetime, true);

            if (StatMaster.isMP)
            {
                ModNetworking.SendToAll(SpaceFlakTurretNet.ShotMsg.CreateMessage(PlayerID, GuidHash, position, forward, velocity, caliber, damage, lifetime));
            }
        }

        private void SpawnLargeRound(Vector3 position, Vector3 forward, Vector3 velocity, float shotCaliber, float damage, float lifetime, bool registerHost)
        {
            GameObject round = new GameObject("BeyondSpider Space Flak Round");
            round.transform.position = position;
            round.transform.rotation = Quaternion.LookRotation(forward, transform.up);
            Rigidbody rb = round.AddComponent<Rigidbody>();
            rb.interpolation = RigidbodyInterpolation.Extrapolate;
            rb.mass = 0.2f;
            rb.drag = shotCaliber > 100f ? 5000f / (shotCaliber * shotCaliber) : Mathf.Max(0.02f, 1f - shotCaliber / 200f);
            rb.useGravity = false;
            rb.velocity = velocity;

            GameObject vis = new GameObject("CannonVis");
            vis.transform.SetParent(round.transform);
            vis.transform.localPosition = Vector3.zero;
            vis.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            vis.transform.localScale = Vector3.one * shotCaliber / 500f;
            MeshFilter meshFilter = vis.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = ModResource.GetMesh("Cannon Mesh").Mesh;
            MeshRenderer meshRenderer = vis.AddComponent<MeshRenderer>();
            meshRenderer.material.mainTexture = ModResource.GetTexture("Cannon Texture").Texture;

            SpaceKineticRound projectile = round.AddComponent<SpaceKineticRound>();
            projectile.OwnerPlayerID = PlayerID;
            projectile.OwnerTeam = Team;
            projectile.Damage = damage;
            projectile.Lifetime = lifetime;
            projectile.RadiusValue = Mathf.Clamp(shotCaliber / 100f, 0.8f, 2.5f);
            projectile.MassEstimate = rb.mass;
            projectile.Caliber = shotCaliber;

            Destroy(round, lifetime + 0.2f);
        }

        private void InitBuildModel()
        {
            Transform vis = transform.Find("Vis");
            if (vis == null)
            {
                return;
            }
            buildBase = vis.gameObject;
            EnsureSplitModel(buildBase.transform, false);
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

            EnsureSplitModel(simRoot.transform, true);
            InitSmallShoot();
            UpdateAppearance(true);
        }

        private void EnsureSplitModel(Transform root, bool simulation)
        {
            Transform baseBase = root.Find("BaseBase");
            if (baseBase == null)
            {
                GameObject baseBaseObject = new GameObject("BaseBase");
                baseBaseObject.transform.SetParent(root);
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
                obj.AddComponent<MeshRenderer>();
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
                obj.AddComponent<MeshRenderer>();
                gunTransform = obj.transform;
            }

            baseObject = baseTransform.gameObject;
            gunObject = gunTransform.gameObject;
            yawTransform = baseBase;
            pitchTransform = gunBase;
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
            }

            ConfigureParticleSystem(smallShoot, particleRate);
            ConfigureParticleSystem(coreParticles, particleRate);
            smallShoot.Stop();
            coreParticles.Stop();
        }

        private void ConfigureParticleSystem(ParticleSystem particles, float rate)
        {
            particles.randomSeed = (uint)(UnityEngine.Random.value * 1000f);
            particles.startSpeed = InitialVelocity(caliber);
            particles.gravityModifier = 0f;
            particles.startLifetime = Mathf.Clamp(Range.Value / Mathf.Max(1f, InitialVelocity(caliber)), 0.35f, 2f);
            particles.startSize = Mathf.Pow(caliber, 1.5f) / 2000f;
            ParticleSystem.EmissionModule emission = particles.emission;
            emission.rate = rate;
            ParticleSystem.LimitVelocityOverLifetimeModule limit = particles.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.dampen = Mathf.Max(0.001f, 0.08f - caliber / 1250f);
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
            if (simulation && originVis != null && originVis.activeSelf)
            {
                originVis.SetActive(false);
            }

            if (previousType != Type.Value)
            {
                ResolveType();
                UpdateAppearance(simulation);
                previousType = Type.Value;
            }
        }

        private void UpdateAppearance(bool simulation)
        {
            if (baseObject == null || gunObject == null)
            {
                return;
            }

            int type = Mathf.Clamp(Type.Value, 0, SpaceFlakTurretAssets.TypeCount - 1);
            baseObject.GetComponent<MeshFilter>().sharedMesh = ModResource.GetMesh(SpaceFlakTurretAssets.AssetName[type] + "-1 Mesh").Mesh;
            baseObject.GetComponent<MeshRenderer>().material.mainTexture = ModResource.GetTexture(SpaceFlakTurretAssets.AssetName[type] + "-1 Texture").Texture;
            gunObject.GetComponent<MeshFilter>().sharedMesh = ModResource.GetMesh(SpaceFlakTurretAssets.AssetName[type] + "-2 Mesh").Mesh;
            gunObject.GetComponent<MeshRenderer>().material.mainTexture = ModResource.GetTexture(SpaceFlakTurretAssets.AssetName[type] + "-2 Texture").Texture;

            Vector3 baseOffset = SpaceFlakTurretAssets.BaseOffset[type];
            Vector3 gunOffset = SpaceFlakTurretAssets.GunOffset[type];
            Vector3 gunBaseOffset = SpaceFlakTurretAssets.GunBaseOffset[type];
            if (simulation)
            {
                baseObject.transform.localPosition = baseOffset;
                gunObject.transform.localPosition = gunOffset;
                pitchTransform.localPosition = gunBaseOffset;
            }
            else
            {
                Vector3 offset = baseOffset * 0.2f;
                baseObject.transform.localPosition = new Vector3(offset.x, -offset.z, offset.y);
                gunObject.transform.localPosition = gunOffset;
                pitchTransform.localPosition = gunBaseOffset - offset * 5f;
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
            switch (type)
            {
                case 1: gunCount = 3; caliber = 25f; break;
                case 2: gunCount = 2; caliber = 40f; break;
                case 3: gunCount = 4; caliber = 40f; break;
                case 4: gunCount = 2; caliber = 76f; break;
                case 5: gunCount = 2; caliber = 100f; break;
                case 6: gunCount = 2; caliber = 105f; break;
                case 7: gunCount = 2; caliber = 127f; break;
                case 8: gunCount = 2; caliber = 113f; break;
                case 9: gunCount = 2; caliber = 134f; break;
                case 10: gunCount = 4; caliber = 40f; break;
                case 11: gunCount = 8; caliber = 40f; break;
                case 12: gunCount = 2; caliber = 127f; break;
                default: gunCount = 1; caliber = 20f; break;
            }
            reloadTime = caliber >= 100f ? caliber / 100f * 0.3f : caliber / 76f * 0.12f;
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

        private static float NormalizeAngle(float angle)
        {
            while (angle > 180f)
            {
                angle -= 360f;
            }
            while (angle < -180f)
            {
                angle += 360f;
            }
            return angle;
        }

        private static float InitialVelocity(float shotCaliber)
        {
            return Mathf.Clamp(190f + Mathf.Sqrt(shotCaliber) * 42f, 280f, 720f);
        }

        private static Vector3 RandomForce(float shotCaliber)
        {
            Vector3 randomForce = new Vector3(UnityEngine.Random.value - 0.5f, UnityEngine.Random.value - 0.5f, UnityEngine.Random.value - 0.5f) * 6f / Mathf.Sqrt(shotCaliber);
            randomForce += new Vector3(0f, UnityEngine.Random.value - 0.5f, 0f) * 6f / Mathf.Sqrt(shotCaliber);
            return randomForce * Mathf.Pow(UnityEngine.Random.value, 2f);
        }
    }

    public static class SpaceFlakTurretAssets
    {
        public const int TypeCount = 13;

        public static readonly string[] AssetName =
        {
            "AA-20",
            "AA-25",
            "AA-40-x2",
            "AA-40-x4",
            "AA-76",
            "AA-100",
            "AA-105",
            "AA-127",
            "AA-113",
            "AA-134",
            "AA-UK-40-x4",
            "AA-UK-40-x8",
            "AA-IJN-127"
        };

        public static readonly Vector3[] BaseOffset = new Vector3[TypeCount];
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

            BaseOffset[0] = new Vector3(0.0f, -0.59f, -0.3f);
            GunBaseOffset[0] = new Vector3(0f, 0.95f, -0.3f);
            GunOffset[0] = new Vector3(0f, -1.5f, 0f);
            GunWidth[0] = 0.1f;
            GunSpeed[0] = 15f;

            BaseOffset[1] = new Vector3(0.0f, 0f, 0f);
            GunBaseOffset[1] = new Vector3(0f, 1.05f, -0.3f);
            GunOffset[1] = new Vector3(0f, -1.05f, 0.3f);
            GunWidth[1] = 2f;
            GunSpeed[1] = 45f;

            BaseOffset[2] = new Vector3(0.0f, -1.3f, 2.55f);
            GunBaseOffset[2] = new Vector3(0f, 1.55f, 0f);
            GunOffset[2] = new Vector3(0f, -2.8f, 2.6f);
            GunWidth[2] = 1.2f;
            GunSpeed[2] = 26f;

            BaseOffset[3] = new Vector3(0.8f, -1.3f, 2.55f);
            GunBaseOffset[3] = new Vector3(0f, 1.55f, 0f);
            GunOffset[3] = new Vector3(0f, -2.8f, 2.6f);
            GunWidth[3] = 3f;
            GunSpeed[3] = 52f;

            BaseOffset[4] = new Vector3(0f, -1f, 0.75f);
            GunBaseOffset[4] = new Vector3(0f, 1.5f, 0f);
            GunOffset[4] = new Vector3(0f, -2.5f, 0.75f);
            GunWidth[4] = 0.18f;
            GunSpeed[4] = 9f;

            BaseOffset[5] = new Vector3(0f, -0.4f, 0.7f);
            GunBaseOffset[5] = new Vector3(0f, 1.6f, 0f);
            GunOffset[5] = new Vector3(0f, -2f, 0.75f);
            GunWidth[5] = 0.07f;
            GunSpeed[5] = 7f;

            BaseOffset[6] = new Vector3(0f, 0f, 0.3f);
            GunBaseOffset[6] = new Vector3(0f, 1.6f, -0.7f);
            GunOffset[6] = new Vector3(0f, -1.6f, 1f);
            GunWidth[6] = 0.08f;
            GunSpeed[6] = 7f;

            BaseOffset[7] = new Vector3(0f, -1.1f, -0.2f);
            GunBaseOffset[7] = new Vector3(0f, 1.7f, 0.5f);
            GunOffset[7] = new Vector3(0f, -2.8f, -0.5f);
            GunWidth[7] = 0.2f;
            GunSpeed[7] = 6f;

            BaseOffset[8] = new Vector3(-0.25f, -2.2f, 0.5f);
            GunBaseOffset[8] = new Vector3(-0.25f, 0.9f, 0.3f);
            GunOffset[8] = new Vector3(0f, -3.1f, 0.2f);
            GunWidth[8] = 0.1f;
            GunSpeed[8] = 6f;

            BaseOffset[9] = new Vector3(0f, -3.4f, 0.9f);
            GunBaseOffset[9] = new Vector3(0f, 1.6f, 0.4f);
            GunOffset[9] = new Vector3(0f, -5f, 0.5f);
            GunWidth[9] = 0.28f;
            GunSpeed[9] = 5f;

            BaseOffset[10] = new Vector3(0f, 0f, 0f);
            GunBaseOffset[10] = new Vector3(0f, 1.6f, 0f);
            GunOffset[10] = new Vector3(0f, -1.6f, 0f);
            GunSpeed[10] = 40f;
            GunWidth[10] = 1.5f;

            BaseOffset[11] = new Vector3(0f, 0f, 0f);
            GunBaseOffset[11] = new Vector3(0f, 1.5f, 0f);
            GunOffset[11] = new Vector3(0f, -1.5f, 0f);
            GunSpeed[11] = 80f;
            GunWidth[11] = 2.5f;

            BaseOffset[12] = new Vector3(0f, 0f, 0f);
            GunBaseOffset[12] = new Vector3(0f, 1.8f, 0f);
            GunOffset[12] = new Vector3(0f, -1.8f, 0f);
            GunWidth[12] = 0.1f;
            GunSpeed[12] = 6f;
        }
    }
}
