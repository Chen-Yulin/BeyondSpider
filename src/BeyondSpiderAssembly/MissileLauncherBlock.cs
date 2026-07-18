using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // Networks missile spawns from a launcher's host to every client, mirroring the flak turret's
    // ShotMsg (see SpaceFlakTurretNet). The host mints a shared missile id and broadcasts the launch
    // kinematics; each client routes the message back to its own copy of the firing launcher (matched by
    // PlayerID + GuidHash), which spawns a mirror missile carrying that same id. That shared id is the
    // missile's GuidHash, so a radar lock resolves to the same logical missile on every machine.
    public class MissileLauncherNet : SingleInstance<MissileLauncherNet>
    {
        public override string Name { get { return "BeyondSpider Missile Launcher Net"; } }

        // (ownerPlayerId, launcherGuidHash, missileNetId, missileType, spawnPos, launchDir, velocity,
        //  fireChannelMask) — the mask rides along so a client mirror re-acquires targets from the
        // same fire channels the host launcher was subscribed to (ADR-0010).
        public static MessageType SpawnMsg = ModNetworking.CreateMessageType(
            DataType.Integer, DataType.Integer, DataType.Integer, DataType.Integer,
            DataType.Vector3, DataType.Vector3, DataType.Vector3, DataType.Integer);

        private readonly Dictionary<string, MissileLauncherBlock> launchers = new Dictionary<string, MissileLauncherBlock>();

        public void Register(MissileLauncherBlock launcher)
        {
            launchers[Key(launcher.PlayerID, launcher.GuidHash)] = launcher;
        }

        public void Unregister(MissileLauncherBlock launcher)
        {
            launchers.Remove(Key(launcher.PlayerID, launcher.GuidHash));
        }

        public void SpawnReceiver(Message msg)
        {
            MissileLauncherBlock launcher = Find((int)msg.GetData(0), (int)msg.GetData(1));
            if (launcher != null)
            {
                launcher.ReceiveMissileSpawn(
                    (int)msg.GetData(2), (int)msg.GetData(3),
                    (Vector3)msg.GetData(4), (Vector3)msg.GetData(5), (Vector3)msg.GetData(6),
                    (int)msg.GetData(7));
            }
        }

        private MissileLauncherBlock Find(int playerId, int guid)
        {
            MissileLauncherBlock launcher;
            launchers.TryGetValue(Key(playerId, guid), out launcher);
            return launcher;
        }

        private static string Key(int playerId, int guid)
        {
            return playerId.ToString() + ":" + guid.ToString();
        }
    }

    // Unified missile launcher (ADR-0009). One block replaces the old Heavy Nuclear Missile part and
    // the Space Interceptor Launcher: the player picks a missile size (small / medium / heavy) from a
    // build-mode menu, and the launcher fires that size of guided projectile. The three sizes differ
    // ONLY in numbers (maneuverability / range / weight / warhead), never in role — every missile can
    // both intercept incoming threats and strike ships; which target a given missile guides toward is
    // chosen by fire control (defensive threats first, else the ship's offensive lock). Missiles are
    // projectiles now (like cannon rounds but with a collision box), not blocks. Launch is along the
    // block's +forward axis. All per-type tuning lives in MissileLauncherAssets.
    public class MissileLauncherBlock : SpaceBlock
    {
        public MMenu Type;
        public MKey Launch;
        public MToggle AutoFire;
        public MFireChannel FireChannel;
        public MSlider ArmDelay;
        public MInfo PitInfo;
        public MInfo ReloadInfo;
        public MInfo RangeInfo;

        // Block identity (from BuildingBlock.Guid, like the flak turret / ship core) — the key clients
        // use to route this launcher's networked missile-spawn messages back to their own copy of it.
        public int GuidHash { get; private set; }

        private readonly List<string> typeList = new List<string> { "小型弹", "中型弹", "重型弹" };

        // Reload-icon sizing/anchoring, mirrored from RailgunBarrelBlock so the launcher's empty-pit
        // indicator reads the same as the railgun's.
        private const float ReloadIconSize = 50f;
        // Launch-door animation: from a closed bay, opening takes this long before the missile emerges
        // (so it's "opened for launch"), and the bay stays open at least this long after the last shot
        // before closing again.
        private const float DoorOpenLead = 0.15f;
        private const float DoorOpenHold = 0.7f;

        private int pitCount;
        private int loadedCount;
        private float reloadTimer;
        private float reloadTime;
        private float salvoCooldown;
        private bool manualLaunchQueued;
        private int previousType = -1;
        private bool builtForSimulation;

        // Launch-door state (simulation only): closed when idle, open around a launch.
        private bool doorOpen;
        private float doorHoldTimer;
        private bool launchPending;
        private float launchLeadTimer;
        private bool pendingManual;

        // See agent-besiege-mod-guide.md's "注册时序规范" — retried each tick until it succeeds,
        // since ShipState may not exist yet when OnSimulateStart runs.
        private bool registered;

        // This launcher's personal channel-0 lottery ticket (ADR-0014): which of the ship's
        // up-to-4 channel-0 threats its auto-fire engages, sticky until that target drops off
        // the list. Each fired missile draws its own ticket for in-flight re-acquisition.
        private readonly FireChannelAssignment channel0Assignment = new FireChannelAssignment();

        private GameObject pitContainer;
        private GameObject[] pitObjects;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Missile Launcher";
            Type = AddMenu("Missile Type", 0, typeList, false);
            Launch = AddKey("Launch", "BSMissileLaunch", KeyCode.X);
            AutoFire = AddToggle("Auto Fire", "BSMissileAutoFire", true);
            FireChannel = AddFireChannel("Fire Channels", "BSMissileFireChannel");
            // Contact fuze arming delay: how long after launch the missile ignores anything it touches
            // (so it can clear its own launcher/hull before it's live to detonate — see
            // MissileProjectile.OnTriggerEnter). Was a hardcoded 0.18s; exposed as a slider so ships
            // with a short muzzle clearance or unusual mounting can retune it without a recompile.
            ArmDelay = AddSlider("Arm Delay", "BSMissileArmDelay", 0.18f, 0f, 2f);
            PitInfo = AddInfo("Cells", "BSMissilePitInfo");
            ReloadInfo = AddInfo("Reload", "BSMissileReloadInfo");
            RangeInfo = AddInfo("Range", "BSMissileRangeInfo");
            MissileLauncherAssets.EnsureInitialized();
            ApplyBody(false);
            RefreshInfo();
        }

        private void RefreshInfo()
        {
            int t = ClampType();
            if (PitInfo != null) PitInfo.Set(MissileLauncherAssets.PitCount[t].ToString());
            if (ReloadInfo != null) ReloadInfo.Set(MissileLauncherAssets.ReloadTime[t].ToString("F1") + " s/cell");
            if (RangeInfo != null) RangeInfo.Set(MissileLauncherAssets.MissileRange[t].ToString("F0") + " m");
        }

        private int ClampType()
        {
            return Mathf.Clamp(Type.Value, 0, MissileLauncherAssets.TypeCount - 1);
        }

        public override void BuildingUpdate()
        {
            base.BuildingUpdate();
            if (previousType != Type.Value)
            {
                previousType = Type.Value;
                ApplyBody(false);
                RefreshInfo();
            }
        }

        // Swaps the block's own "Vis" mesh between the closed-bay and open-bay models (the launch
        // animation switches these in simulation). Same overwrite-the-XML-placeholder idiom
        // SpaceFlakTurretBlock uses for its mount/turret/gun pieces.
        private void ApplyBody(bool open)
        {
            Transform vis = transform.Find("Vis");
            if (vis == null) return;
            MeshFilter mf = vis.GetComponent<MeshFilter>();
            MeshRenderer mr = vis.GetComponent<MeshRenderer>();
            if (mf == null || mr == null) return;

            int t = ClampType();
            string meshName = open ? MissileLauncherAssets.OpenMeshName[t] : MissileLauncherAssets.CloseMeshName[t];
            string texName = open ? MissileLauncherAssets.OpenTextureName[t] : MissileLauncherAssets.CloseTextureName[t];
            mf.sharedMesh = ModResource.GetMesh(meshName).Mesh;
            mr.material.mainTexture = ModResource.GetTexture(texName).Texture;
        }

        // Opens/closes the launch bay: swaps the body mesh and (re)shows the loaded cells, which are
        // only visible while the bay is open.
        private void SetDoor(bool open)
        {
            if (doorOpen == open)
            {
                return;
            }
            doorOpen = open;
            ApplyBody(open);
            RefreshPitVisibility();
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            MissileLauncherAssets.EnsureInitialized();
            // Runs on host and clients alike; the net registry keys on GuidHash, so set it first, then
            // register so a client's copy of this launcher can receive the host's missile-spawn messages.
            GuidHash = BlockBehaviour.BuildingBlock.Guid.GetHashCode();
            MissileLauncherNet.Instance.Register(this);
            int t = ClampType();
            pitCount = MissileLauncherAssets.PitCount[t];
            reloadTime = MissileLauncherAssets.ReloadTime[t];
            // Simulate start: magazine full (every cell loaded), per the spec.
            loadedCount = pitCount;
            reloadTimer = 0f;
            salvoCooldown = 0f;
            manualLaunchQueued = false;
            channel0Assignment.Clear();
            doorOpen = false;
            doorHoldTimer = 0f;
            launchPending = false;

            // Sim starts with the bay closed; it opens only to launch.
            ApplyBody(false);
            BuildPits(t);
            builtForSimulation = true;
            RefreshPitVisibility();

            ShipState ship = OwnShip();
            if (ship != null)
            {
                OnAssignedToShip(ship);
            }
        }

        public override void OnAssignedToShip(ShipState ship)
        {
            SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Launchers);
            registered = true;
        }

        public override void OnSimulateStop()
        {
            MissileLauncherNet.Instance.Unregister(this);
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Launchers);
            }
            registered = false;
            builtForSimulation = false;
            if (pitContainer != null)
            {
                Destroy(pitContainer);
                pitContainer = null;
                pitObjects = null;
            }
            ApplyBody(false);
        }

        // Fire key is read ONLY here, and SimulateUpdateHost is host-only — so a network client's
        // launcher never fires off the local key. A client player's press is synced to the host, the
        // host runs fire control and relays the launch (MissileLauncherNet.SpawnMsg), and the client's
        // launcher performs the launch action on receipt (ReceiveMissileSpawn).
        public override void SimulateUpdateHost()
        {
            if (Launch.IsPressed)
            {
                manualLaunchQueued = true;
            }
        }

        // Client-side upkeep. Fire control is host-only (SimulateFixedUpdateHost doesn't run on a
        // client), so the client launcher would otherwise freeze — bay stuck shut/open, cells never
        // refilling — while networked missiles spawn in front of it. Run the same reload/door upkeep the
        // host runs, so a client launcher animates in step. Same guard the flak turret uses for its
        // client visual pose; host and single-player drive this from SimulateFixedUpdateHost instead.
        private void FixedUpdate()
        {
            if (BlockBehaviour != null && BlockBehaviour.isSimulating && StatMaster.isClient)
            {
                TickReloadAndDoor(Time.fixedDeltaTime);
            }
        }

        // Sequential reload (refill one empty cell at a time — the spec's "一个个往弹坑里填") plus
        // closing the bay once it has been open past its hold. Pure magazine/presentation upkeep with no
        // fire decision, so it runs identically on host and client. launchPending is always false on a
        // client (host-only fire control), so there this just closes the bay after the hold elapses.
        private void TickReloadAndDoor(float dt)
        {
            if (loadedCount < pitCount)
            {
                reloadTimer += dt;
                if (reloadTimer >= reloadTime)
                {
                    reloadTimer -= reloadTime;
                    loadedCount++;
                    RefreshPitVisibility();
                }
            }
            else
            {
                reloadTimer = 0f;
            }

            if (doorHoldTimer > 0f)
            {
                doorHoldTimer -= dt;
            }
            if (!launchPending && doorOpen && doorHoldTimer <= 0f)
            {
                SetDoor(false);
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            ShipState ship = OwnShip();
            if (ship != null && !registered)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Launchers);
                registered = true;
            }

            float dt = Time.fixedDeltaTime;
            // Reload refill + close the bay after its hold: shared upkeep the client also runs (from
            // FixedUpdate) so both sides animate identically.
            TickReloadAndDoor(dt);

            if (salvoCooldown > 0f)
            {
                salvoCooldown -= dt;
            }

            // Launch animation: a pending launch (bay just opened) fires once the open lead elapses.
            if (launchPending)
            {
                launchLeadTimer -= dt;
                if (launchLeadTimer <= 0f)
                {
                    ExecuteFire(ship, ClampType(), pendingManual);
                    launchPending = false;
                }
            }

            bool manual = manualLaunchQueued;
            manualLaunchQueued = false;

            if (launchPending || loadedCount <= 0 || salvoCooldown > 0f)
            {
                return;
            }

            int t = ClampType();
            // Launcher-side selection is channel-driven (ADR-0010): defensive threats, then the
            // launcher's enabled fire channels — never the old nearest-hostile fallback, which
            // would let auto-fire ignore channel assignment entirely. A missile already in
            // flight still falls back to nearest-hostile when its channels go empty.
            ITrackable target = MissileFireControl.SelectTarget(ship, PlayerID, Team, transform.position,
                transform.forward, MissileLauncherAssets.MissileRange[t], FireChannel.Value, channel0Assignment, null, false);
            // Auto-fire needs a fire-control target; a manual press launches even without one (it flies
            // straight out and tries to acquire in flight).
            if (!(manual || (AutoFire.IsActive && target != null)))
            {
                return;
            }

            if (doorOpen)
            {
                // Bay already open (mid-ripple): fire straight away and keep it open.
                ExecuteFire(ship, t, manual);
            }
            else
            {
                // Closed: pop the bay open, then fire after the open lead so the missile emerges from an
                // already-open launcher. Reserve the shot now so we don't re-trigger during the lead.
                SetDoor(true);
                doorHoldTimer = DoorOpenHold;
                launchPending = true;
                launchLeadTimer = DoorOpenLead;
                pendingManual = manual;
                salvoCooldown = MissileLauncherAssets.SalvoInterval[t];
            }
        }

        private void ExecuteFire(ShipState ship, int type, bool manual)
        {
            if (loadedCount <= 0)
            {
                return;
            }
            ITrackable target = MissileFireControl.SelectTarget(ship, PlayerID, Team, transform.position,
                transform.forward, MissileLauncherAssets.MissileRange[type], FireChannel.Value, channel0Assignment, null, false);
            if (!manual && target == null)
            {
                return; // auto-fire lost its target during the open lead — don't spend a cell
            }

            // Active-defence energy draw scaled by the 主动-被动防御 macro dial (1 at neutral).
            if (ship != null)
            {
                float cost = MissileLauncherAssets.EnergyPerLaunch[type] * SpaceBalance.ActiveDefenseEnergyScale;
                if (!ship.Energy.CanSupply(EnergyBus.Weapon, cost))
                {
                    return;
                }
                ship.Energy.Request(EnergyBus.Weapon, cost);
            }

            loadedCount--;
            salvoCooldown = MissileLauncherAssets.SalvoInterval[type];
            doorHoldTimer = DoorOpenHold;
            RefreshPitVisibility();

            Vector3 launchDir = transform.forward;
            Vector3 spawnPos = transform.position + launchDir * MissileLauncherAssets.MuzzleOffset[type];
            Vector3 velocity = (Body == null ? Vector3.zero : Body.velocity) + launchDir * MissileLauncherAssets.LaunchSpeed[type];

            // ExecuteFire only ever runs host-side (the whole fire path hangs off SimulateFixedUpdateHost),
            // so this is the one place a missile is born: mint the shared id here, spawn the real guided
            // missile locally, then have every client spawn a mirror carrying the SAME id. That id is the
            // missile's GuidHash, so a radar lock resolves to the same logical missile on every machine
            // (CaptainLockNet matches PlayerID + GuidHash) — the identity the old heavy-missile BLOCK got
            // for free from its BuildingBlock.Guid. This mirrors the flak turret's ShotMsg replication.
            int netId = NextMissileNetId();
            SpawnMissile(type, netId, spawnPos, launchDir, velocity, target, FireChannel.Value);
            if (StatMaster.isMP)
            {
                ModNetworking.SendToAll(MissileLauncherNet.SpawnMsg.CreateMessage(
                    PlayerID, GuidHash, netId, type, spawnPos, launchDir, velocity, FireChannel.Value));
            }
        }

        // Client mirror of a host launch (routed here by MissileLauncherNet.SpawnReceiver via this
        // launcher's PlayerID+GuidHash). The client launcher performs the SAME visible launch action the
        // host did — spend a cell, pop the bay open — then spawns the mirror missile, so a client sees its
        // launcher fire in step instead of a bare missile appearing in front of a frozen launcher. The bay
        // then closes and cells reload via TickReloadAndDoor (FixedUpdate). No fire-control target is
        // handed in: the mirror re-acquires in flight like any missile fired at nothing, which is all a
        // visual, lockable, shoot-downable copy needs. The missile spawn itself is NOT gated on the
        // client's cell count — the host already authoritatively decided to fire.
        public void ReceiveMissileSpawn(int netId, int type, Vector3 spawnPos, Vector3 launchDir, Vector3 velocity, int channelMask)
        {
            if (loadedCount > 0)
            {
                loadedCount--;
            }
            SetDoor(true);
            doorHoldTimer = DoorOpenHold;
            RefreshPitVisibility();
            SpawnMissile(type, netId, spawnPos, launchDir, velocity, null, channelMask);
        }

        // Builds the missile GameObject. Shared by the host fire path and the client mirror path so both
        // construct an identical projectile; only the initial fire-control target differs (null on mirrors).
        private void SpawnMissile(int type, int netId, Vector3 spawnPos, Vector3 launchDir, Vector3 velocity, ITrackable target, int channelMask)
        {
            GameObject go = new GameObject("BeyondSpider Missile");
            go.transform.position = spawnPos;
            go.transform.rotation = Quaternion.LookRotation(launchDir, transform.up);

            GameObject vis = new GameObject("MissileVis");
            vis.transform.SetParent(go.transform);
            vis.transform.localPosition = Vector3.zero;
            vis.transform.localRotation = Quaternion.Euler(MissileLauncherAssets.RoundMeshEuler[type]);
            vis.transform.localScale = Vector3.one * MissileLauncherAssets.MissileMeshScale[type];
            vis.AddComponent<MeshFilter>().sharedMesh = ModResource.GetMesh(MissileLauncherAssets.RoundMeshName[type]).Mesh;
            vis.AddComponent<MeshRenderer>().material.mainTexture = ModResource.GetTexture(MissileLauncherAssets.RoundTextureName[type]).Texture;

            Rigidbody rb = go.AddComponent<Rigidbody>();
            // Smooth the visual between physics ticks (physics runs at the fixed step, rendering is
            // per-frame) — same Extrapolate mode the cannon rounds use, so a fast missile doesn't
            // visibly step forward each FixedUpdate. Without this it defaults to None (no smoothing).
            rb.interpolation = RigidbodyInterpolation.Extrapolate;
            rb.mass = MissileLauncherAssets.MissileMass[type];
            rb.drag = 0.02f;
            rb.angularDrag = 0.05f;
            rb.useGravity = false;
            rb.velocity = velocity;

            // Collision capsule (trigger so it never physics-pushes ships, but raycasting rounds still
            // hit it — Physics.queriesHitTriggers is true by default — so it stays shoot-downable, and
            // it detonates on contact via OnTriggerEnter). Sized and offset from the round mesh's own
            // measured bounds (see MissileLauncherAssets.ColliderRadius/Height/Center) instead of a
            // hand-tuned box, so the hit volume hugs the actual slender body and tracks any mesh change.
            CapsuleCollider capsule = go.AddComponent<CapsuleCollider>();
            capsule.isTrigger = true;
            capsule.direction = 2; // local Z — the missile's travel axis
            capsule.radius = MissileLauncherAssets.ColliderRadius(type);
            capsule.height = MissileLauncherAssets.ColliderHeight(type);
            capsule.center = MissileLauncherAssets.ColliderCenter(type);

            MissileProjectile missile = go.AddComponent<MissileProjectile>();
            missile.Configure(PlayerID, Team, type, target, netId, channelMask, OwnShip(), ArmDelay.Value);
        }

        // Monotonic per-session source of shared missile ids. Minted only on the host (ExecuteFire is
        // host-only), so every missile in the session gets a distinct id regardless of which player's
        // launcher fired it; PlayerID disambiguates further when a lock is resolved.
        private static int missileNetIdCounter;

        private static int NextMissileNetId()
        {
            missileNetIdCounter++;
            return missileNetIdCounter;
        }

        private void BuildPits(int type)
        {
            if (pitContainer != null)
            {
                Destroy(pitContainer);
            }
            pitContainer = new GameObject("MissilePits");
            pitContainer.transform.SetParent(transform, false);
            pitContainer.transform.localPosition = Vector3.zero;
            pitContainer.transform.localRotation = Quaternion.identity;

            Vector3[] positions = MissileLauncherAssets.PitPositions[type];
            int n = MissileLauncherAssets.PitCount[type];
            pitObjects = new GameObject[n];
            for (int i = 0; i < n; i++)
            {
                GameObject pit = new GameObject("Cell" + i.ToString());
                pit.transform.SetParent(pitContainer.transform, false);
                pit.transform.localPosition = i < positions.Length ? positions[i] : Vector3.zero;
                pit.transform.localRotation = Quaternion.Euler(MissileLauncherAssets.RoundMeshEuler[type]);
                pit.transform.localScale = Vector3.one * MissileLauncherAssets.PitMeshScale[type];
                pit.AddComponent<MeshFilter>().sharedMesh = ModResource.GetMesh(MissileLauncherAssets.RoundMeshName[type]).Mesh;
                pit.AddComponent<MeshRenderer>().material.mainTexture = ModResource.GetTexture(MissileLauncherAssets.RoundTextureName[type]).Texture;
                pitObjects[i] = pit;
            }
        }

        // A loaded cell shows its missile whether the bay is open or closed; only a fired/empty cell
        // hides it. Cells fill/empty as a stack (indices 0..loadedCount-1 loaded), so reload visibly
        // tops the magazine back up.
        private void RefreshPitVisibility()
        {
            if (pitObjects == null)
            {
                return;
            }
            for (int i = 0; i < pitObjects.Length; i++)
            {
                if (pitObjects[i] != null)
                {
                    pitObjects[i].SetActive(i < loadedCount);
                }
            }
        }

        // Empty-cell reload indicator, same twin-circle art as the railgun's reload icon. Shown while
        // any cell is empty (i.e. the magazine is refilling).
        private void OnGUI()
        {
            if (StatMaster.hudHidden || !builtForSimulation || !BlockBehaviour.isSimulating)
            {
                return;
            }
            if (loadedCount >= pitCount)
            {
                return;
            }

            Camera camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            Vector3 screen = camera.WorldToScreenPoint(transform.position);
            if (screen.z <= 0f || screen.z >= 30f)
            {
                return;
            }

            Texture outCircle = ModResource.GetTexture("BS Migrated Gun Load Circle Out Texture").Texture;
            Texture inCircle = ModResource.GetTexture("BS Migrated Gun Load Circle In Texture").Texture;
            float progress = Mathf.Clamp01(reloadTimer / Mathf.Max(0.001f, reloadTime));
            float inSize = ReloadIconSize * progress;
            float x = screen.x - ReloadIconSize * 0.5f;
            float y = camera.pixelHeight - screen.y - ReloadIconSize * 0.5f;
            GUI.DrawTexture(new Rect(x, y, ReloadIconSize, ReloadIconSize), outCircle);
            GUI.DrawTexture(new Rect(screen.x - inSize * 0.5f, camera.pixelHeight - screen.y - inSize * 0.5f, inSize, inSize), inCircle);
        }
    }

    // Chooses which target a launcher (or an in-flight missile) should guide toward: the requested
    // fire channels (channel 0 — the captain's auto air-defence lock, ADR-0012 retired the separate
    // DefenseDirectorBlock/DefensiveSolution shortcut that used to be checked first here — outranks
    // the rest, ADR-0010), then — for in-flight missiles only — the nearest hostile in range as a
    // fallback. Always range- and IFF-gated.
    public static class MissileFireControl
    {
        // `self` is the trackable doing the asking — the in-flight missile — and is excluded from every
        // candidate so a missile can NEVER select itself. Without this the channel-override path below
        // bites the owner: locking your own in-flight missile (with IFF off) makes that missile resolve
        // its ship's lock — itself — as its target, then fuze at zero range and self-detonate. Excluding
        // self means the self-lock is ignored and the missile keeps guiding toward its real target
        // (thrust unchanged); OTHER interceptors can still be commanded onto the locked friendly
        // missile. The launcher passes null (a launcher block is not a selectable trackable).
        //
        // `allowNearestFallback` separates the two callers: an in-flight missile may re-acquire the
        // nearest hostile when its channels go empty (it's already spent), but a launcher deciding
        // whether to auto-fire must stay channel-driven, otherwise the channel mapper would be
        // meaningless for launchers.
        // `channel0Assignment` is the caller's personal channel-0 lottery ticket (ADR-0014) — the
        // launcher owns one for its auto-fire decisions, and each in-flight missile owns its own
        // for re-acquisition, so a salvo spreads over the threat list instead of every missile
        // converging on rank 0.
        public static ITrackable SelectTarget(ShipState ship, int playerId, MPTeam team, Vector3 origin,
            Vector3 forward, float range, int channelMask, FireChannelAssignment channel0Assignment,
            ITrackable self, bool allowNearestFallback)
        {
            if (ship != null)
            {
                // Channel targets are engageable through the captain's IFF override, exactly like the
                // flak turret's channel path (CanEngage handles it inside FireChannels.SelectTarget) —
                // so an interceptor can be commanded onto a locked friendly/own-team target (e.g.
                // test-firing at your own missile, or a threat from one of this player's OTHER ships,
                // with IFF off) that the blanket hostility gate refuses.
                ITrackable channelTarget = FireChannels.SelectTarget(ship, channelMask, channel0Assignment, origin, forward, range, playerId, team, self);
                if (channelTarget != null)
                {
                    return channelTarget;
                }
            }
            return allowNearestFallback ? NearestHostile(playerId, team, origin, range) : null;
        }

        private static ITrackable NearestHostile(int playerId, MPTeam team, Vector3 origin, float range)
        {
            ITrackable best = null;
            float bestDistance = range;
            IList<ITrackable> all = SpaceCombatRegistry.Trackables;
            for (int i = 0; i < all.Count; i++)
            {
                ITrackable candidate = all[i];
                if (!IsHostile(candidate, playerId, team) || candidate == null || !candidate.IsAlive)
                {
                    continue;
                }
                float distance = Vector3.Distance(origin, candidate.Position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            return best;
        }

        public static bool IsHostile(ITrackable target, int playerId, MPTeam team)
        {
            return target != null && target.PlayerID != playerId && target.Team != team;
        }

        // True when a missile owned by (playerId, team) may guide onto `target` right now: the target
        // is alive and either plainly hostile, or it is locked on one of this ship's fire channels
        // with the captain's IFF override authorizing fire on it (the same override the flak turret
        // honors). The channel-identity check matters because with IFF off CanCommandLockedFireAt()
        // authorizes ANY target — only explicitly locked ones may be engaged, never arbitrary
        // friendlies.
        public static bool CanEngage(ShipState ship, ITrackable target, int playerId, MPTeam team)
        {
            if (target == null || !target.IsAlive)
            {
                return false;
            }
            if (IsHostile(target, playerId, team))
            {
                return true;
            }
            return FireChannels.IsChannelTarget(ship, target)
                && ship.Captain != null && ship.Captain.CanCommandLockedFireAt(target);
        }
    }

    // The flying missile: a projectile (not a block) with a collision box. Guides by the shared PN +
    // closing-speed governor + single-nozzle model (ADR-0008); acquires and re-acquires its target
    // through fire control (ADR-0009); detonates on the proximity/closest-approach fuze or on contact;
    // is itself a trackable, shoot-downable target that cooks off its warhead when killed.
    public class MissileProjectile : MonoBehaviour, ITrackable, IKineticTarget, ILockable
    {
        private int ownerPlayerID;
        private MPTeam ownerTeam;
        private int type;
        // The LAUNCHING ship (by connectivity, ADR-0011) — fire control must consult this ship's
        // channel locks, not "the owner player's ship": one player may field several.
        private ShipState ownerShip;

        private ITrackable target;
        private Rigidbody body;
        private MissileEngineGlow engineGlow;
        private float spawnTime;
        private float nextRetarget;
        private float structuralHP;
        private float radiusValue;
        private float lifetime;
        private bool detonated;
        private int guidHash;
        // Fire channels this missile listens to when (re-)acquiring, copied from its launcher's
        // MFireChannel mask at spawn (ADR-0010).
        private int channelMask = FireChannels.AllMask;
        // This missile's own channel-0 lottery ticket (ADR-0014) for in-flight re-acquisition —
        // per-missile, so a salvo spreads over the threat list instead of converging on rank 0.
        private readonly FireChannelAssignment channel0Assignment = new FireChannelAssignment();

        // Player-tunable via the launcher's Arm Delay slider (MissileLauncherBlock.ArmDelay); 0.18f is
        // just the fallback if Configure is somehow never called. Set once per missile at spawn, not
        // read live off the block, since the block that fired it may not even exist a moment later.
        private float armSeconds = 0.18f;
        private const float RetargetInterval = 0.4f;

        public void Configure(int playerId, MPTeam team, int missileType, ITrackable initialTarget, int netId, int fireChannelMask, ShipState launchingShip, float armDelaySeconds)
        {
            ownerPlayerID = playerId;
            ownerTeam = team;
            ownerShip = launchingShip;
            type = Mathf.Clamp(missileType, 0, MissileLauncherAssets.TypeCount - 1);
            target = initialTarget;
            guidHash = netId;
            channelMask = fireChannelMask & FireChannels.AllMask;
            armSeconds = Mathf.Max(0f, armDelaySeconds);
            structuralHP = MissileLauncherAssets.MissileStructuralHP[type];
            radiusValue = MissileLauncherAssets.MissileRadius[type];
            lifetime = MissileLauncherAssets.MissileLifetime[type];
            // Attach the engine glow here (not Awake) so it's sized for the configured type.
            if (engineGlow == null)
            {
                engineGlow = SpaceEffectAssets.AttachMissileEngineGlow(
                    transform, MissileLauncherAssets.GlowSize[type], MissileLauncherAssets.TailOffset(type));
            }
        }

        public int PlayerID { get { return ownerPlayerID; } }
        public MPTeam Team { get { return ownerTeam; } }
        public TrackKind Kind { get { return TrackKind.HeavyMissile; } }
        public Vector3 Position { get { return transform.position; } }
        public Vector3 Velocity { get { return body == null ? Vector3.zero : body.velocity; } }
        public float Radius { get { return radiusValue; } }
        // `this != null` is the destroyed-object test (see ITrackable.IsAlive), not dead weight: a
        // missile is destroyed on detonation, while cached track/lock references still point at it.
        public bool IsAlive { get { return this != null && !detonated && gameObject.activeSelf && Time.time - spawnTime < lifetime; } }
        // Mass the shield reads for its deceleration pricing (mirrors the old heavy missile's ThreatMass).
        public float ThreatMass { get { return body != null ? body.mass : MissileLauncherAssets.MissileMass[type]; } }
        // What channel-0 threat evaluation (ADR-0014) expects this missile to deliver on impact:
        // warhead area blast plus the direct-hit bonus, both straight from the per-type tables.
        public float EstimatedThreatDamage
        {
            get
            {
                return SpaceBalance.MissileBlastDamage(MissileLauncherAssets.MissileWarheadCharge[type])
                    + MissileLauncherAssets.MissileDirectDamage[type];
            }
        }
        // Warhead area-blast radius (ADR-0007 derivation from the per-type charge). Channel-0
        // threat evaluation pads the hull box with a multiple of this: a missile detonating NEAR
        // the hull still damages it, so "would hit" must include the blast envelope, not just
        // the airframe.
        public float BlastRadius
        {
            get { return SpaceBalance.MissileBlastRadius(MissileLauncherAssets.MissileWarheadCharge[type]); }
        }

        // ---- ILockable: radar-lockable exactly like the old heavy-missile BLOCK ----
        // The old block took its GuidHash from BuildingBlock.Guid; a fired missile has no block, so the
        // launcher mints a shared id at spawn (host-side) and hands the SAME id to every client's mirror
        // copy (see MissileLauncherNet). That makes a lock resolve to the same logical missile on every
        // machine — CaptainLockNet matches PlayerID + GuidHash — the cross-client identity the old block
        // had for free.
        public int GuidHash { get { return guidHash; } }

        // ---- IKineticTarget (ADR-0007): structural HP only, never ricochets ----
        public float RemainingKineticHP { get { return structuralHP; } }
        public Vector3 KineticVelocity { get { return body != null ? body.velocity : Vector3.zero; } }
        public bool CanRicochet { get { return false; } }
        public bool IsBreached { get { return detonated || structuralHP <= 0f || !gameObject.activeSelf; } }
        public void ApplyKineticDamage(float damage) { ApplyDamage(damage); }

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            spawnTime = Time.time;
            SpaceCombatRegistry.RegisterTrackable(this);
        }

        // A fired missile is a free-standing GameObject, not a block, so nothing resets it when the
        // player ends the simulation — it would otherwise hang in the build scene for the rest of its
        // (up to 20s) lifetime. Poll the global sim flag every frame and remove the missile the moment
        // simulation stops. This lives in Update, not FixedUpdate, because FixedUpdate stops ticking
        // when the physics clock is paused (timeScale 0) on returning to build, whereas Update always
        // runs — so the cleanup fires even if no further physics step ever happens. No detonation: a
        // sim-end teardown must not spray blast damage. OnDestroy unregisters it from the radar.
        // (StatMaster.levelSimulating is the global static sim flag; BlockBehaviour.isSimulating is an
        // instance field and this projectile is not a block.)
        private void Update()
        {
            if (!StatMaster.levelSimulating)
            {
                Destroy(gameObject);
            }
        }

        private void FixedUpdate()
        {
            if (detonated)
            {
                return;
            }
            if (Time.time - spawnTime > lifetime)
            {
                Destroy(gameObject);
                return;
            }

            if (Time.time - spawnTime >= armSeconds && CheckSweepHit())
            {
                return;
            }

            // Fire control (re-)selects the target: keep the current one while it's still engageable
            // (alive and hostile, or a captain-commanded lock — see MissileFireControl.CanEngage),
            // otherwise ask the LAUNCHING ship's fire control for a fresh one. This is what lets a
            // missile launched at nothing acquire in flight, a missile whose target dies re-task, and
            // an interceptor hold onto a locked friendly target it was commanded onto with IFF off.
            ShipState ship = ownerShip;
            bool currentEngageable = MissileFireControl.CanEngage(ship, target, ownerPlayerID, ownerTeam) && !IsSiblingMissile(target);
            if (!currentEngageable && Time.time >= nextRetarget)
            {
                nextRetarget = Time.time + RetargetInterval;
                // Pass ourselves so fire control never hands us back to ourselves — locking your own
                // in-flight missile must not make it home on itself and self-detonate at zero range.
                // In flight the nearest-hostile fallback stays on (unlike the launcher): a spent
                // missile whose channels went empty should still chase something rather than coast.
                ITrackable candidate = MissileFireControl.SelectTarget(ship, ownerPlayerID, ownerTeam, transform.position,
                    transform.forward, MissileLauncherAssets.MissileRange[type], channelMask, channel0Assignment, this, true);
                // A sibling missile from this same ship can still surface via the channel-lock path
                // (e.g. IFF off, captain lock) — never home or fuze on one of our own, so drop it
                // rather than adopt it as a target.
                target = IsSiblingMissile(candidate) ? null : candidate;
            }

            bool burning = false;
            if (target != null && target.IsAlive)
            {
                burning = MissileGuidance.Steer(transform, body, target.Position, target.Velocity, GuidanceParams(target.Kind));
                if (MissileGuidance.FuzeTriggered(transform.position, body.velocity, target.Position, target.Velocity,
                        target.Radius, SpaceBalance.MissileFuzeRadius, SpaceBalance.MissileArmingRadius))
                {
                    Detonate(target);
                    return;
                }
            }
            else
            {
                // No target yet: coast straight, nozzle off, and keep the seeker looking.
                if (Time.time - spawnTime < armSeconds)
                {
                    body.AddForce(transform.forward * MissileLauncherAssets.MissileThrust[type], ForceMode.Force);
                    burning = true;
                }
            }

            if (engineGlow != null)
            {
                engineGlow.SetBurning(burning);
            }
        }

        // True for another in-flight missile launched by this same ship (ADR-0011 connectivity, not
        // just same player/team — one player can field several ships). Sibling missiles must never
        // guide onto, proximity-fuze on, or contact-detonate against each other: a salvo would wipe
        // itself out the moment two of its own rounds got close. Missiles from a DIFFERENT friendly
        // ship (or the same ship's hull/armour) are unaffected — those still resolve normally.
        private bool IsSiblingMissile(ITrackable candidate)
        {
            MissileProjectile other = candidate as MissileProjectile;
            return other != null && other != this && ownerShip != null && other.ownerShip == ownerShip;
        }

        // Anti-tunnel sweep fuze: at up to 600 m/s a missile can cross an entire armour plate within a
        // single FixedUpdate, and OnTriggerEnter only catches overlaps that already exist at a tick
        // boundary — a fast flyby can punch straight through unscathed between two ticks. Raycast the
        // distance this tick's physics step is about to move it (mirrors the shell march in
        // SpaceBallistics.RoundProjectile.FixedUpdate) and resolve the first hostile hit directly
        // instead of waiting for the trigger to catch up.
        private bool CheckSweepHit()
        {
            float speed = body.velocity.magnitude;
            if (speed < 0.01f)
            {
                return false;
            }
            Vector3 dir = body.velocity / speed;
            RaycastHit[] hits = Physics.RaycastAll(transform.position, dir, speed * Time.fixedDeltaTime);
            if (hits == null || hits.Length == 0)
            {
                return false;
            }
            System.Array.Sort(hits, CompareHitDistance);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i].collider;
                if (col == null)
                {
                    continue;
                }
                MissileProjectile otherMissile = col.GetComponentInParent<MissileProjectile>();
                if (otherMissile != null && (otherMissile == this || otherMissile.Team == ownerTeam))
                {
                    continue; // never sweep-detonate on a friendly missile — this ship's or another's
                }
                BlockBehaviour block = col.GetComponentInParent<BlockBehaviour>();
                if (block != null && block.Team == ownerTeam)
                {
                    continue; // friendly (or own-ship) block — pass through, same as OnTriggerEnter
                }

                RaycastHit hit = hits[i];
                // Blast cloud can't carry through the surface it just struck — keep only the velocity
                // component tangential to the hit plane (the user's spec: project onto the struck plane).
                Vector3 blastVelocity = body.velocity - Vector3.Dot(body.velocity, hit.normal) * hit.normal;
                Detonate(col.GetComponentInParent<IKineticTarget>() as ITrackable, hit.point, blastVelocity);
                return true;
            }
            return false;
        }

        private static int CompareHitDistance(RaycastHit a, RaycastHit b)
        {
            return a.distance.CompareTo(b.distance);
        }

        private MissileGuidance.GuidanceParams GuidanceParams(TrackKind targetKind)
        {
            MissileGuidance.GuidanceParams p = new MissileGuidance.GuidanceParams();
            p.NavConstant = SpaceBalance.MissileNavConstant;
            p.GovernorGain = SpaceBalance.MissileGovernorGain;
            p.ClosingSpeedCap = MissileLauncherAssets.MissileClosingSpeedCap[type];
            p.MaxTurnRateDeg = MissileLauncherAssets.MissileTurnRate[type];
            p.Thrust = MissileLauncherAssets.MissileThrust[type];
            // Only cap/govern closing speed against ships; shells and other missiles are uncapped
            // (see MissileGuidance.GuidanceParams.LimitClosingSpeed).
            p.LimitClosingSpeed = targetKind == TrackKind.Ship;
            return p;
        }

        // Contact fuze via the collision capsule: once armed (clear of the launcher), touching any hostile
        // block or terrain detonates. Own ship and friendlies are skipped so a missile never blows up
        // on its own hull leaving the cell.
        private void OnTriggerEnter(Collider other)
        {
            if (detonated || Time.time - spawnTime < armSeconds || other == null)
            {
                return;
            }
            MissileProjectile otherMissile = other.GetComponentInParent<MissileProjectile>();
            if (otherMissile != null && (otherMissile == this || otherMissile.Team == ownerTeam))
            {
                return; // never contact-detonate on a friendly missile — this ship's or another's
            }
            BlockBehaviour block = other.GetComponentInParent<BlockBehaviour>();
            if (block != null && block.Team == ownerTeam && block.ParentMachine != null && block.ParentMachine.PlayerID == ownerPlayerID)
            {
                return;
            }
            if (block != null && block.Team == ownerTeam)
            {
                return; // friendly ship — pass through rather than self-fratricide
            }
            Detonate(other.GetComponentInParent<IKineticTarget>() as ITrackable);
        }

        public void ApplyDamage(float damage)
        {
            if (detonated || damage <= 0f || structuralHP <= 0f)
            {
                return;
            }
            structuralHP -= damage;
            if (structuralHP <= 0f)
            {
                // Shot down: the warhead cooks off (user's choice — an intercepted missile still blasts).
                Detonate(null);
            }
        }

        // Shields only slow a missile down (ADR 0006's hyper-velocity bleed) — they never damage or
        // detonate it, unlike a direct kinetic/flak hit. A missile that lingers past a shield gets
        // caught again next tick rather than dying to the field itself.
        public void ApplyShieldDeceleration(Vector3 newVelocity, float appliedDeltaV)
        {
            if (body == null || appliedDeltaV <= 0f)
            {
                return;
            }
            body.velocity = newVelocity;
        }

        // Default path (proximity fuze, plain trigger contact, shoot-down cook-off): blast sits at the
        // missile's own position and carries its full velocity, since nothing more precise is known.
        private void Detonate(ITrackable directTarget)
        {
            Detonate(directTarget, transform.position, body != null ? body.velocity : Vector3.zero);
        }

        // Sweep-fuze path: detonates exactly at the hit point with an explicit blast velocity (see
        // CheckSweepHit, which projects the impact velocity onto the struck plane).
        private void Detonate(ITrackable directTarget, Vector3 blastPosition, Vector3 blastVelocity)
        {
            if (detonated)
            {
                return;
            }
            detonated = true;

            // Vacuum fireball at the point of death — purely cosmetic, the blast damage below is
            // unchanged. Covers every detonation path (fuze, contact, shoot-down cook-off); the cloud
            // inherits the given blast velocity (see MissileBlastDrift).
            SpaceEffectAssets.PlayMissileBlast(blastPosition, blastVelocity, MissileLauncherAssets.MissileWarheadCharge[type]);

            // Direct hit on another penetrable target (e.g. intercepting an enemy missile) delivers the
            // per-type direct damage on top of the area blast.
            IKineticTarget kinetic = directTarget as IKineticTarget;
            if (kinetic != null && !ReferenceEquals(kinetic, this) && !kinetic.IsBreached)
            {
                kinetic.ApplyKineticDamage(MissileLauncherAssets.MissileDirectDamage[type]);
                ApplyImpactKnockback(kinetic, blastPosition);
            }

            ApplyBlastDamageToArmor(blastPosition);
            ApplyBlastForce(blastPosition);
            SpaceCombatRegistry.UnregisterTrackable(this);
            Destroy(gameObject);
        }

        // Physical push in the missile's own travel direction, applied at the exact impact point —
        // scoped to armour specifically (not e.g. an intercepted missile), mirroring
        // SpaceKineticRound.ApplyImpactKnockback for shells.
        private void ApplyImpactKnockback(IKineticTarget target, Vector3 hitPoint)
        {
            NanoArmorBehaviour armor = target as NanoArmorBehaviour;
            if (armor == null || body == null)
            {
                return;
            }
            Rigidbody targetBody = armor.GetComponent<Rigidbody>();
            float speed = body.velocity.magnitude;
            if (targetBody == null || speed < 0.01f)
            {
                return;
            }
            Vector3 impulse = (body.velocity / speed) * (body.mass * speed * SpaceBalance.KineticImpactImpulseFraction);
            targetBody.AddForceAtPosition(impulse, hitPoint, ForceMode.Impulse);
        }

        // Explosive shove on everything within blast range (armour, other missiles, cannon shells in
        // flight) — separate from ApplyBlastDamageToArmor's damage-only falloff, and never decaying to
        // zero (30% floor, see SpaceBalance.BlastForceFalloffFloor) so an intercept can still deflect
        // an inbound salvo clear out at the edge of the blast.
        private void ApplyBlastForce(Vector3 origin)
        {
            float charge = MissileLauncherAssets.MissileWarheadCharge[type];
            float blastRadius = SpaceBalance.MissileBlastRadius(charge);
            float baseForce = SpaceBalance.MissileBlastForce(charge);

            HashSet<Rigidbody> pushed = new HashSet<Rigidbody>();

            // Armour, missiles, and any other collider-bearing block in range.
            Collider[] hits = Physics.OverlapSphere(origin, blastRadius);
            for (int i = 0; i < hits.Length; i++)
            {
                Rigidbody rb = hits[i].attachedRigidbody;
                if (rb == null || rb == body || !pushed.Add(rb))
                {
                    continue;
                }
                PushWithBlast(rb, origin, blastRadius, baseForce);
            }

            // Cannon shells carry no collider at all (raycast-only hit detection, ADR-0007), so the
            // overlap sweep above can never see them — walk the trackable registry directly instead.
            IList<ITrackable> trackables = SpaceCombatRegistry.Trackables;
            for (int i = 0; i < trackables.Count; i++)
            {
                SpaceKineticRound round = trackables[i] as SpaceKineticRound;
                if (round == null || !round.IsAlive)
                {
                    continue;
                }
                Rigidbody rb = round.GetComponent<Rigidbody>();
                if (rb == null || !pushed.Add(rb))
                {
                    continue;
                }
                PushWithBlast(rb, origin, blastRadius, baseForce);
            }
        }

        private static void PushWithBlast(Rigidbody rb, Vector3 origin, float blastRadius, float baseForce)
        {
            Vector3 offset = rb.position - origin;
            float distance = offset.magnitude;
            if (distance > blastRadius)
            {
                return;
            }
            Vector3 dir = distance > 0.01f ? offset / distance : Random.onUnitSphere;
            float falloff = Mathf.Lerp(1f, SpaceBalance.BlastForceFalloffFloor, Mathf.Clamp01(distance / blastRadius));
            rb.AddForce(dir * baseForce * falloff, ForceMode.Impulse);
        }

        // Warhead area blast (ADR-0007 derivation): the charge alone drives blast damage (linear) and
        // radius (cube-root of yield), applied to every armour block within range with linear falloff.
        private void ApplyBlastDamageToArmor(Vector3 origin)
        {
            float charge = MissileLauncherAssets.MissileWarheadCharge[type];
            float baseBlastDamage = SpaceBalance.MissileBlastDamage(charge);
            float blastRadius = SpaceBalance.MissileBlastRadius(charge);
            foreach (ShipState ship in SpaceCombatRegistry.Ships)
            {
                for (int i = 0; i < ship.Armor.Count; i++)
                {
                    NanoArmorBehaviour armor = ship.Armor[i];
                    if (armor == null)
                    {
                        continue;
                    }
                    float distance = Vector3.Distance(origin, armor.transform.position);
                    if (distance > blastRadius)
                    {
                        continue;
                    }
                    float falloff = Mathf.Clamp01(1f - distance / blastRadius);
                    armor.ApplyPhysicalDamage(baseBlastDamage * falloff);
                }
            }
        }

        private void OnDestroy()
        {
            SpaceCombatRegistry.UnregisterTrackable(this);
        }
    }

    // Per-type tuning for the three missile sizes (index 0 = small, 1 = medium, 2 = heavy). The user
    // owns these numbers and the cell layouts; the launcher/projectile logic only reads them. Sizes
    // differ purely in maneuverability / range / weight / warhead — never in role.
    public static class MissileLauncherAssets
    {
        public const int TypeCount = 3;

        // ---- Visuals ----
        public static readonly string[] OpenMeshName = { "BS MissileLauncher Open S Mesh", "BS MissileLauncher Open M Mesh", "BS MissileLauncher Open L Mesh" };
        public static readonly string[] OpenTextureName = { "BS MissileLauncher Open S Texture", "BS MissileLauncher Open M Texture", "BS MissileLauncher Open L Texture" };
        public static readonly string[] CloseMeshName = { "BS MissileLauncher Close S Mesh", "BS MissileLauncher Close M Mesh", "BS MissileLauncher Close L Mesh" };
        public static readonly string[] CloseTextureName = { "BS MissileLauncher Close S Texture", "BS MissileLauncher Close M Texture", "BS MissileLauncher Close L Texture" };
        public static readonly string[] RoundMeshName = { "BS Missile Round S Mesh", "BS Missile Round M Mesh", "BS Missile Round L Mesh" };
        public static readonly string[] RoundTextureName = { "BS Missile Round S Texture", "BS Missile Round M Texture", "BS Missile Round L Texture" };

        // Local Euler applied to every round mesh (in the cell and in flight) so the model's nose lines up
        // with the +forward launch/travel axis. +90° about X is correct — verified in game — do not flip
        // it: these models have their ORIGIN AT THE NOSE, not at their centre, so a 180° flip doesn't just
        // turn the round around, it swings the whole body across the pivot and the round visibly jumps off
        // the launcher. The body therefore runs BACKWARD from local zero (zero = nose, tail at the mesh's
        // rearmost point) — which is why the exhaust glow is positioned from measured mesh bounds
        // (TailOffset) rather than a short fixed offset behind the origin, which would sit on the nose.
        public static readonly Vector3[] RoundMeshEuler = { new Vector3(90f, 0f, 0f), new Vector3(90f, 0f, 0f), new Vector3(90f, 0f, 0f) };
        public static readonly float[] MissileMeshScale = { 0.25f, 0.45f, 0.8f };  // in-flight round mesh scale (halved)
        public static readonly float[] PitMeshScale = { 0.25f, 0.45f, 0.8f };      // cell-loaded round mesh scale (halved)
        public static readonly float[] GlowSize = { 0.25f, 0.45f, 0.8f };          // exhaust glow scale (halved to match)

        // Distance from a round's origin back to its tail, in the missile root's local units, measured
        // once per type from the mesh's own bounds (rotated by RoundMeshEuler, scaled by MissileMeshScale).
        // These models are pivoted at the NOSE, so the nozzle sits a whole body-length behind local zero,
        // not a token offset — measuring beats a hand-tuned number because it tracks any change to the
        // mesh, its scale or its euler for free.
        private static readonly float[] tailOffsetCache = new float[TypeCount];
        private static readonly bool[] tailOffsetMeasured = new bool[TypeCount];

        public static float TailOffset(int type)
        {
            if (!tailOffsetMeasured[type])
            {
                Bounds bounds = ModResource.GetMesh(RoundMeshName[type]).Mesh.bounds;
                Quaternion rotation = Quaternion.Euler(RoundMeshEuler[type]);
                float scale = MissileMeshScale[type];
                float minZ = float.MaxValue;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 point = new Vector3(
                        (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                        (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                        (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                    float z = (rotation * (point * scale)).z;
                    if (z < minZ)
                    {
                        minZ = z;
                    }
                }
                // Rearmost point lies -minZ behind the origin; 0 if the mesh never reaches behind it.
                tailOffsetCache[type] = Mathf.Max(0f, -minZ);
                tailOffsetMeasured[type] = true;
            }
            return tailOffsetCache[type];
        }

        // Capsule collider dimensions, measured once per type the same way as TailOffset above (round
        // mesh bounds rotated by RoundMeshEuler, scaled by MissileMeshScale) rather than a hand-tuned
        // box, so the hit volume tracks whatever the model actually looks like. The capsule runs along
        // local +Z — the missile's travel axis, same frame TailOffset measures in.
        private static readonly float[] colliderRadiusCache = new float[TypeCount];
        private static readonly float[] colliderHeightCache = new float[TypeCount];
        private static readonly Vector3[] colliderCenterCache = new Vector3[TypeCount];
        private static readonly bool[] colliderMeasured = new bool[TypeCount];

        private static void MeasureCollider(int type)
        {
            if (colliderMeasured[type])
            {
                return;
            }
            colliderMeasured[type] = true;

            Bounds bounds = ModResource.GetMesh(RoundMeshName[type]).Mesh.bounds;
            Quaternion rotation = Quaternion.Euler(RoundMeshEuler[type]);
            float scale = MissileMeshScale[type];
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = new Vector3(
                    (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                    (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                Vector3 transformed = rotation * (point * scale);
                min = Vector3.Min(min, transformed);
                max = Vector3.Max(max, transformed);
            }

            // Radius covers the wider of the two cross-axes so the capsule never clips inside the
            // visible body; height spans the full measured length so the end caps land at the nose and
            // tail instead of sitting inset from them.
            float radius = Mathf.Max(0.05f, Mathf.Max(max.x - min.x, max.y - min.y) * 0.5f);
            colliderRadiusCache[type] = radius;
            colliderHeightCache[type] = Mathf.Max(radius * 2f, max.z - min.z);
            colliderCenterCache[type] = new Vector3((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, (min.z + max.z) * 0.5f);
        }

        public static float ColliderRadius(int type)
        {
            MeasureCollider(type);
            return colliderRadiusCache[type];
        }

        public static float ColliderHeight(int type)
        {
            MeasureCollider(type);
            return colliderHeightCache[type];
        }

        public static Vector3 ColliderCenter(int type)
        {
            MeasureCollider(type);
            return colliderCenterCache[type];
        }

        // ---- Magazine ----
        public static readonly int[] PitCount = { 16, 4, 1 };
        public static readonly float[] ReloadTime = { 1.0f, 3.0f, 8.0f };    // seconds to refill ONE cell
        public static readonly float[] SalvoInterval = { 0.25f, 0.6f, 1.5f };// min seconds between launches
        public static readonly float[] EnergyPerLaunch = { 60f, 130f, 300f };

        // ---- Flight / launch ----
        public static readonly float[] LaunchSpeed = { 35f, 30f, 25f };       // initial ejection speed (m/s)
        public static readonly float[] MuzzleOffset = { 1.0f, 1.5f, 2.2f };   // spawn distance ahead of the block

        // ---- Missile stats: sizes differ ONLY here ----
        public static readonly float[] MissileThrust = { 850f, 1400f, 2400f };
        public static readonly float[] MissileClosingSpeedCap = { 600f, 600f, 600f }; // m/s
        public static readonly float[] MissileTurnRate = { 600f, 300f, 40f };          // deg/s (maneuverability)
        public static readonly float[] MissileMass = { 0.5f, 3f, 20f };               // weight
        public static readonly float[] MissileWarheadCharge = { 8f, 30f, 200f };      // 装药 → blast dmg/radius
        public static readonly float[] MissileRange = { 1000f, 3000f, 10000f };         // launch/target gate (m)
        public static readonly float[] MissileStructuralHP = { 5f, 10f, 80f };        // shoot-down toughness
        public static readonly float[] MissileRadius = { 3f, 5f, 10f };          // track + fuze size
        public static readonly float[] MissileLifetime = { 10f, 20f, 250f };           // seconds
        public static readonly float[] MissileDirectDamage = { 200f, 600f, 1500f };   // direct hit bonus

        // ---- Cell layouts (local positions on the block, per type) ----
        // Placeholder grids; hand-tune these against the actual Open-S/M/L models. Cells lie in the
        // block's local XY plane (offset out along +Z toward the launch mouth); a size-1 magazine is a
        // single centred cell.
        public static readonly Vector3[][] PitPositions = new Vector3[TypeCount][];

        private static bool initialized;

        public static void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }
            initialized = true;

            PitPositions[0] = BuildGrid(4, 4, 0.32f, 0.18f);  // small: 4x4 = 16
            PitPositions[1] = BuildGrid(2, 2, 0.6f, 0.28f);   // medium: 2x2 = 4
            PitPositions[2] = new Vector3[] { new Vector3(0f, 0f, 0.35f) }; // heavy: 1
        }

        // A centred rows x cols grid of local cell positions in the block's XY plane, pushed out to +Z
        // by `forward` so the loaded rounds sit at the launch mouth.
        private static Vector3[] BuildGrid(int rows, int cols, float spacing, float forward)
        {
            Vector3[] result = new Vector3[rows * cols];
            float halfR = (rows - 1) * 0.5f;
            float halfC = (cols - 1) * 0.5f;
            int index = 0;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    result[index] = new Vector3((c - halfC) * spacing, (r - halfR) * spacing, forward);
                    index++;
                }
            }
            return result;
        }
    }
}
