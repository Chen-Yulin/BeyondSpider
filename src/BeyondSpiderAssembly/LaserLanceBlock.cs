using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // MP replication for the laser pulse (the fire-event pattern): the host resolves the raycast +
    // damage in FirePulse and broadcasts the beam endpoint plus a hit classification; each client's
    // matching barrel replays the beam and the matching impact flavor. Damage never rides along —
    // the hitType exists precisely so the client can pick the visual without re-running the hit.
    public class LaserNet : SingleInstance<LaserNet>
    {
        public override string Name { get { return "BeyondSpider Laser Net"; } }

        // Hit classification carried in FireMsg (drives the client's impact visual only).
        public const int HitMiss = 0;       // no collider within range — beam to full range, no impact FX
        public const int HitReflect = 1;    // intact nano-armor — mirrored reflection beam (needs normal + reflectivity)
        public const int HitSparks = 2;     // stripped nano-armor — continuous spark shower
        public const int HitPierce = 3;     // anything else (hull, missile, terrain) — pierce flash

        // playerId, guidHash, endPoint, hitNormal (zero on miss), hitType, reflectivity
        public static MessageType FireMsg = ModNetworking.CreateMessageType(
            DataType.Integer, DataType.Integer, DataType.Vector3, DataType.Vector3, DataType.Integer, DataType.Single);

        private readonly Dictionary<string, LaserLanceBlock> barrels = new Dictionary<string, LaserLanceBlock>();

        public void Register(LaserLanceBlock barrel)
        {
            barrels[Key(barrel.PlayerID, barrel.GuidHash)] = barrel;
        }

        public void Unregister(LaserLanceBlock barrel)
        {
            barrels.Remove(Key(barrel.PlayerID, barrel.GuidHash));
        }

        public void FireReceiver(Message msg)
        {
            LaserLanceBlock barrel;
            if (barrels.TryGetValue(Key((int)msg.GetData(0), (int)msg.GetData(1)), out barrel) && barrel != null)
            {
                barrel.ReceiveFire((Vector3)msg.GetData(2), (Vector3)msg.GetData(3), (int)msg.GetData(4), (float)msg.GetData(5));
            }
        }

        private static string Key(int playerId, int guid)
        {
            return playerId + ":" + guid;
        }
    }

    // 光矛 / Laser Lance. A barrel-only weapon in the same family as RailgunBarrelBlock — same
    // Fire key / Fire Control toggle / Gun Group, same SpaceGunner aiming, same Weapon-bus energy,
    // same build-mode-scale-derived read-only stats, same reload-ring HUD — but it fires an
    // instant raycast beam instead of a kinetic slug. Consequences of "instant beam", per the
    // design brief:
    //   * Shield-immune: ShieldProjectorBlock only ever decelerates SpaceKineticRound / missiles,
    //     and a laser spawns neither, so it passes through untouched with no special-casing.
    //   * Infinite muzzle velocity to fire-control: CurrentMuzzleVelocity() returns a huge value,
    //     so the gunner/captain lead solution collapses to "aim straight at the current target".
    //   * Nano-armor interaction: routed through DamageRouter.RouteLaserHit ->
    //     NanoArmorBehaviour.ApplyLaserDamage, which reflects ~95% off intact armor and lets the
    //     beam bite only once Integrity has been stripped by kinetic/blast hits.
    //   * Range falloff: damage tapers toward MinDamageFalloff at maximum range (光越远越散射).
    //
    // Visuals live in LaserFx.cs: the beam itself is a LaserBeamFx (flickering core/halo lines,
    // coiling lightning bolts, particle sheath), and the impact picks between a mirrored
    // reflection beam (intact nano-armor) and a continuous spark shower (stripped armor) — see
    // LaserImpactFx. All purely cosmetic; damage is only ever the raycast above.
    public class LaserLanceBlock : SpaceBlock, IShipGun
    {
        public MKey FireKey { get; private set; }
        public MToggle FireControl { get; private set; }
        public MText GunGroup { get; private set; }
        public MInfo ApertureInfo;
        public MInfo FocalRatioInfo;
        public MInfo RangeInfo;

        private const float ReloadIconSize = 50f;
        private const float ReloadIconWorldOffset = 0f;
        private const float BaseMuzzleForwardOffset = 2f;
        private const float MinScaleComponent = 0.05f;

        // Derived from the block's own build-mode scale, same idea as RailgunBarrelBlock's
        // caliber/bore/velocity (see agent-besiege-mod-guide.md). Aperture (beam diameter, mm)
        // follows the barrel cross-section; focal ratio (f-number, the optical analogue of the
        // railgun's 倍径) folds in the along-barrel length; range follows focal ratio; beam
        // damage and per-shot energy follow aperture/focal. Default scale (1,1,1) gives aperture
        // 300 mm / focal f/40 / range 1200 m.
        private const float ApertureFromScaleCoefficient = 300f;
        private const float FocalFromScaleCoefficient = 12000f;
        private const float RangePerFocalRatio = 30f;
        private const float BeamDamageCoefficient = 0.9f;
        private const float MinDamageFalloff = 0.4f;
        // Fixed firing overhead; the damage-scaled part of the per-shot cost comes from
        // SpaceBalance.WeaponShotEnergy so the beam pays the same MJ-per-damage as the railgun.
        private const float EnergyBase = 25f;

        // Fire-control sees the beam as instantaneous. 1e6 m/s dwarfs any target speed in this
        // mod (hundreds of m/s), so the intercept-time solution is ~0 => aim point == target
        // position. Kept well under int.MaxValue so the captain's velocity-bucket quantization
        // (RoundToInt(muzzleVelocity / 10)) can't overflow.
        private const float InfiniteMuzzleVelocity = 1000000f;

        // Long enough for the high-frequency width/brightness flicker and the coiling bolts to
        // register as texture rather than a one-frame flash; still comfortably shorter than the
        // fastest possible reload (0.15s floor + aperture term), so pulses never overlap.
        private const float BeamVisibleTime = 0.18f;
        // Reflected beams keep no minimum length stub: whatever range the beam had left after the
        // hit is what bounces, clamped so a point-blank bounce still reads.
        private const float MinReflectedBeamLength = 20f;

        private static readonly Color BeamCoreColor = new Color(0.55f, 0.9f, 1f, 1f);
        private static readonly Color BeamEdgeColor = new Color(0.85f, 1f, 1f, 1f);
        private static readonly Color BeamBoltColor = new Color(0.75f, 0.85f, 1f, 1f);

        public int GuidHash { get; private set; }

        private float reloadTime;
        private float reload;
        private bool manualFireQueued;
        private float apertureMm;
        private float focalRatio;
        private float range;
        private float beamDamage;
        private float energyPerShot;
        private float beamWidth;
        // See agent-besiege-mod-guide.md's "注册时序规范" — retried each tick until it
        // succeeds, since ShipState may not exist yet when this OnSimulateStart runs.
        private bool registered;

        private LaserBeamFx beamFx;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Laser Lance Barrel";
            FireKey = AddKey("Fire", "BSLaserFire", KeyCode.C);
            FireControl = AddToggle("Fire Control", "BSLaserFireControl", false);
            GunGroup = AddText("Gun Group", "BSLaserGroup", "g0");
            ApertureInfo = AddInfo("Aperture", "BSLaserApertureInfo");
            FocalRatioInfo = AddInfo("Focal Ratio", "BSLaserFocalInfo");
            RangeInfo = AddInfo("Range", "BSLaserRangeInfo");
            RecomputeBallistics();
        }

        // Aperture depends only on the barrel cross-section (local x/y scale); focal ratio folds
        // in the along-barrel scale (local z); everything downstream follows. Recomputed live in
        // build mode so the read-out tracks the scale handles, and locked once at simulation
        // start — a barrel's scale doesn't change mid-simulation, same as RailgunBarrelBlock.
        private void RecomputeBallistics()
        {
            Vector3 scale = transform.localScale;
            float x = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.x));
            float y = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.y));
            float z = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.z));

            apertureMm = ApertureFromScaleCoefficient * Mathf.Sqrt(x * y);
            focalRatio = FocalFromScaleCoefficient * z / apertureMm;
            range = RangePerFocalRatio * focalRatio;
            beamDamage = apertureMm * BeamDamageCoefficient;
            // Charged for full potential beam damage (what it deals to unarmoured structure,
            // missiles, or integrity-stripped armour) even though intact nano-armour reflects
            // ~95% of it — the capability is what costs energy. See SpaceBalance.
            energyPerShot = SpaceBalance.WeaponShotEnergy(EnergyBase, beamDamage);
            beamWidth = Mathf.Clamp(apertureMm / 1500f, 0.05f, 0.6f);

            ApertureInfo.Set(apertureMm.ToString("F0") + " mm");
            FocalRatioInfo.Set("f/" + focalRatio.ToString("F1"));
            RangeInfo.Set(range.ToString("F0") + " m");
        }

        public override void BuildingUpdate()
        {
            base.BuildingUpdate();
            RecomputeBallistics();
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            RecomputeBallistics();
            reloadTime = Mathf.Clamp(0.15f + apertureMm / 900f, 0.1f, 2f);
            reload = reloadTime;
            manualFireQueued = false;
            GuidHash = BlockBehaviour.BuildingBlock.Guid.GetHashCode();
            LaserNet.Instance.Register(this);
            InitBeamFx();
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
            LaserNet.Instance.Unregister(this);
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem<IShipGun>(this, ship.Guns);
            }
            registered = false;
            if (beamFx != null)
            {
                Destroy(beamFx.gameObject);
                beamFx = null;
            }
        }

        public override void SimulateUpdateHost()
        {
            // Latch instantaneous input here (see agent-besiege-mod-guide.md's "输入采样规则"):
            // the SpaceGunner drives fire-control by emulating this same FireKey.
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
                FirePulse(transform.forward);
            }
        }

        // Fires one beam pulse along direction: reload/energy gate, raycast to the nearest
        // collider in the line of fire (own hull included — laser ignores IFF, ADR-0007), damage
        // that hit, then flash the beam out to the hit point (or full range if it hit nothing).
        public bool FirePulse(Vector3 direction)
        {
            if (reload < reloadTime || direction.sqrMagnitude < 0.0001f)
            {
                return false;
            }
            direction = direction.normalized;

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

            Vector3 muzzle = ApproximateMuzzlePosition();
            Vector3 endPoint = muzzle + direction * range;
            int hitType = LaserNet.HitMiss;
            Vector3 hitNormal = Vector3.zero;
            float reflectivity = 0f;

            RaycastHit hit;
            if (RaycastBeam(muzzle, direction, out hit))
            {
                endPoint = hit.point;
                hitNormal = hit.normal;
                float falloff = Mathf.Lerp(1f, MinDamageFalloff, Mathf.Clamp01(hit.distance / Mathf.Max(1f, range)));
                float rawDamage = beamDamage * falloff;

                MissileProjectile missile = hit.collider.GetComponentInParent<MissileProjectile>();
                if (missile != null && missile.PlayerID != PlayerID)
                {
                    missile.ApplyDamage(rawDamage);
                }
                NanoArmorBehaviour armor = DamageRouter.RouteLaserHit(hit.collider, rawDamage);
                if (armor != null && armor.ReflectsLaser)
                {
                    // Intact coating is a mirror: the beam bounces off along the reflection of
                    // its own direction, carrying whatever range it had left. Visual only — the
                    // 5% that bites was already applied by RouteLaserHit above.
                    hitType = LaserNet.HitReflect;
                    reflectivity = armor.LaserReflectivity;
                    float remaining = Mathf.Clamp(range - hit.distance, MinReflectedBeamLength, range);
                    LaserImpactFx.PlayReflection(hit.point, direction, hit.normal, beamWidth,
                        BeamCoreColor, BeamEdgeColor, BeamBoltColor, remaining, BeamVisibleTime, reflectivity);
                }
                else if (armor != null)
                {
                    // Coating stripped below the mirror threshold: the beam is burning metal now,
                    // so the impact point sprays a continuous shower of sparks instead.
                    hitType = LaserNet.HitSparks;
                    LaserImpactFx.PlayArmorSparks(hit.point, hit.normal, hit.collider.transform, apertureMm);
                }
                else
                {
                    hitType = LaserNet.HitPierce;
                    SpaceEffectAssets.PlayPierceEffect(endPoint, apertureMm);
                }
            }

            if (beamFx != null)
            {
                beamFx.Show(muzzle, endPoint, BeamVisibleTime, 1f);
            }

            // FirePulse only runs host-side (both fire paths hang off SimulateFixedUpdateHost) —
            // broadcast the resolved beam so every client's matching barrel replays it.
            if (StatMaster.isMP)
            {
                ModNetworking.SendToAll(LaserNet.FireMsg.CreateMessage(
                    PlayerID, GuidHash, endPoint, hitNormal, hitType, reflectivity));
            }
            return true;
        }

        // Client replay of a host pulse (routed here by LaserNet.FireReceiver via this barrel's
        // PlayerID+GuidHash): beam to the host's endpoint plus the impact flavor the hitType names.
        // Display only — no raycast for damage, no energy, no missile/armor calls. The beam origin
        // is this replica barrel's own muzzle, so the beam stays glued to the (vanilla-synced)
        // turret pose even if it lags the host's a frame.
        public void ReceiveFire(Vector3 endPoint, Vector3 hitNormal, int hitType, float reflectivity)
        {
            reload = 0f;
            Vector3 muzzle = ApproximateMuzzlePosition();

            if (hitType == LaserNet.HitReflect)
            {
                Vector3 incoming = endPoint - muzzle;
                float dist = incoming.magnitude;
                if (dist > 0.001f)
                {
                    float remaining = Mathf.Clamp(range - dist, MinReflectedBeamLength, range);
                    LaserImpactFx.PlayReflection(endPoint, incoming / dist, hitNormal, beamWidth,
                        BeamCoreColor, BeamEdgeColor, BeamBoltColor, remaining, BeamVisibleTime, reflectivity);
                }
            }
            else if (hitType == LaserNet.HitSparks)
            {
                // No follow transform on clients (the hit collider isn't identified in the message);
                // the spark shower just doesn't track a moving plate — LaserSparkFx handles null.
                LaserImpactFx.PlayArmorSparks(endPoint, hitNormal, null, apertureMm);
            }
            else if (hitType == LaserNet.HitPierce)
            {
                SpaceEffectAssets.PlayPierceEffect(endPoint, apertureMm);
            }

            if (beamFx != null)
            {
                beamFx.Show(muzzle, endPoint, BeamVisibleTime, 1f);
            }
        }

        // Client-side reload ticking for the OnGUI ring (the host ticks inside
        // SimulateFixedUpdateHost, which never runs here).
        private void FixedUpdate()
        {
            if (BlockBehaviour != null && BlockBehaviour.isSimulating && StatMaster.isClient)
            {
                reload = Mathf.Min(reloadTime, reload + Time.fixedDeltaTime);
            }
        }

        private bool RaycastBeam(Vector3 origin, Vector3 direction, out RaycastHit best)
        {
            best = default(RaycastHit);
            RaycastHit[] hits = Physics.RaycastAll(origin, direction, range);
            if (hits == null || hits.Length == 0)
            {
                return false;
            }

            float bestDistance = float.MaxValue;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                // No own-ship skip: laser damage ignores IFF exactly like kinetic rounds, so a beam
                // strikes own / friendly armour if it sits in the line of fire (ADR-0007). The one
                // exception is an unrecognized trigger collider (e.g. the spectator camera's own
                // collider) — real hit volumes (missile capsules, block hitboxes) are excluded from
                // that check so the beam still catches them.
                RaycastHit candidate = hits[i];
                if (SpaceCombatUtil.IsUnrecognizedObstacle(candidate.collider)
                    && candidate.collider.GetComponentInParent<MissileProjectile>() == null
                    && candidate.collider.GetComponentInParent<BlockBehaviour>() == null)
                {
                    continue;
                }
                if (candidate.distance < bestDistance)
                {
                    bestDistance = candidate.distance;
                    best = candidate;
                    found = true;
                }
            }
            return found;
        }

        public Vector3 ApproximateMuzzlePosition()
        {
            float zScale = Mathf.Max(0.05f, Mathf.Abs(transform.localScale.z));
            return transform.position + transform.forward * BaseMuzzleForwardOffset * zScale;
        }

        public float CurrentMuzzleVelocity()
        {
            return InfiniteMuzzleVelocity;
        }

        private void InitBeamFx()
        {
            if (beamFx != null)
            {
                return;
            }

            beamFx = LaserBeamFx.Create(transform, "BS Laser Beam", beamWidth,
                BeamCoreColor, BeamEdgeColor, BeamBoltColor, false);
            // Same muzzle math as ApproximateMuzzlePosition, so the fading beam stays glued to
            // the muzzle while the barrel re-aims between pulses.
            float zScale = Mathf.Max(0.05f, Mathf.Abs(transform.localScale.z));
            beamFx.SetAnchor(transform, BaseMuzzleForwardOffset * zScale);
        }

        // Reload ring, reusing the migrated railgun load-circle textures.
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

            GUI.DrawTexture(new Rect(screen.x - ReloadIconSize * 0.5f, camera.pixelHeight - screen.y - ReloadIconSize * 0.5f, ReloadIconSize, ReloadIconSize), outCircle);
            GUI.DrawTexture(new Rect(screen.x - inSize * 0.5f, camera.pixelHeight - screen.y - inSize * 0.5f, inSize, inSize), inCircle);
        }
    }
}
