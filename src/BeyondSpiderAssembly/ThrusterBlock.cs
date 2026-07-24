using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // Electric thruster (ADR-0018). Produces force along -forward (the nozzle faces +forward and
    // the exhaust leaves that way), drawing from the ship-wide grid's Thrust bus. Its ceiling comes
    // from the NOZZLE CROSS-SECTION — the two block-scale extents perpendicular to the thrust axis
    // — so the same number sets thrust, plume radius and efficiency; length buys nothing.
    //
    // Two control modes, chosen per block by the Advanced Control toggle:
    //   off — the block's own key, hold to burn. Needs no captain; a captainless hull can still fly.
    //   on  — the block enrolls in its ship's 6-DOF allocator and ignores its own key. Needs a
    //         seated captain to command it (ThrustAllocator / SpaceCaptainBlock's driving keys);
    //         with no captain aboard it simply idles, which the MInfo line says out loud so a
    //         player who ticks this on a captainless ship isn't left wondering.
    //
    // The block never applies force itself: ShipThrustControl solves and drives every thruster on
    // a ship together, host-only. Clients only ever render CurrentOutput (ADR-0015).
    public class ThrusterBlock : SpaceBlock
    {
        // Slider floor is 0.1, not 0: an enrolled thruster the player has dialed to nothing would
        // sit in the allocator's matrix as a zero column, which is a degenerate direction the
        // regularizer then has to absorb every tick for no reason.
        public MSlider OutputRatio;
        public MToggle AdvancedControl;
        public MKey ThrustKey;
        public MInfo MaxThrustInfo;
        public MInfo PowerInfo;
        public MInfo OutputInfo;
        public MInfo NozzleInfo;

        private const float MinScaleComponent = 0.05f;

        public float NozzleArea { get; private set; }
        public float MaxThrust { get; private set; }

        // Output ratio in [0,1] actually delivered this tick, after the allocator's split and the
        // grid's brownout degradation. Host-authored; on clients it arrives via ThrusterNet's
        // packed output batch. Drives the plume, so "flame brightness = real thrust" holds on
        // every machine — which makes the exhaust a live readout of what the allocator is doing.
        public float CurrentOutput;

        public int GuidHash { get; private set; }

        // Sampled once at simulate start (see the guide's 输入采样规则 — toggles/menus must not be
        // re-read mid-simulation).
        public bool Advanced { get; private set; }
        public float RatioCap { get; private set; }

        private bool registered;
        private bool detonated;
        private bool keyHeld;
        private bool lastSentKeyHeld;
        private float nextInfoRefresh;
        private ThrusterFx fx;

        public bool IsAlive { get { return !detonated && BlockBehaviour != null && BlockBehaviour.isSimulating; } }

        // True while this block's own key is down. In MP a client's key press reaches the host
        // through ThrusterNet.KeyMsg, so the host — the only machine that applies force — always
        // reads the owning player's intent rather than its own keyboard.
        public bool KeyHeld
        {
            get { return keyHeld; }
            set { keyHeld = value; }
        }

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Thruster";
            OutputRatio = AddSlider("Output Ratio", "BSThrusterRatio", 1f, 0.1f, 1f);
            AdvancedControl = AddToggle("Advanced Control", "BSThrusterAdvanced", false);
            ThrustKey = AddKey("Thrust", "BSThrusterKey", KeyCode.None);
            MaxThrustInfo = AddInfo("Max Thrust", "BSThrusterMaxThrust");
            PowerInfo = AddInfo("Full Throttle Draw", "BSThrusterPower");
            OutputInfo = AddInfo("Current Output", "BSThrusterOutput", "0 %");
            NozzleInfo = AddInfo("Nozzle Area", "BSThrusterNozzle");
            RecomputeThrust();
        }

        // Nozzle cross-section = the two scale components PERPENDICULAR to the thrust axis. The
        // thrust axis is local z (forward), so x·y — stretching the block along z lengthens the
        // engine bell and changes nothing else, by design.
        private void RecomputeThrust()
        {
            Vector3 scale = transform.localScale;
            float x = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.x));
            float y = Mathf.Max(MinScaleComponent, Mathf.Abs(scale.y));
            NozzleArea = x * y;
            MaxThrust = SpaceBalance.ThrusterMaxThrust(NozzleArea);
            MaxThrustInfo.Set(MaxThrust.ToString("F0") + " kN");
            PowerInfo.Set(SpaceBalance.ThrusterPowerDraw(NozzleArea, 1f).ToString("F0") + " MW");
            NozzleInfo.Set(NozzleArea.ToString("F2") + " m2");
        }

        public override void BuildingUpdate()
        {
            base.BuildingUpdate();
            RecomputeThrust();
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            RecomputeThrust();
            GuidHash = BlockBehaviour.BuildingBlock.Guid.GetHashCode();
            Advanced = AdvancedControl.IsActive;
            RatioCap = Mathf.Clamp(OutputRatio.Value, 0.1f, 1f);
            CurrentOutput = 0f;
            keyHeld = false;
            lastSentKeyHeld = false;

            ShipState ship = OwnShip();
            if (ship != null)
            {
                OnAssignedToShip(ship);
            }
            if (Body != null)
            {
                Body.mass = SpaceBalance.ThrusterMass(MaxThrust);
            }

            fx = gameObject.GetComponent<ThrusterFx>();
            if (fx == null)
            {
                fx = gameObject.AddComponent<ThrusterFx>();
            }
            fx.Configure(transform, NozzleArea);
        }

        public override void OnAssignedToShip(ShipState ship)
        {
            SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Thrusters);
            ship.Allocator.MarkRosterDirty();
            registered = true;
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Thrusters);
                ship.Allocator.MarkRosterDirty();
            }
            registered = false;
            CurrentOutput = 0f;
            if (fx != null)
            {
                fx.Shutdown();
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            if (registered)
            {
                return;
            }
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Thrusters);
                ship.Allocator.MarkRosterDirty();
                registered = true;
            }
        }

        // Key sampling runs from a plain FixedUpdate rather than SimulateUpdateHost/Client because
        // the engine does not dispatch the Simulate hooks on network clients at all (same reason
        // SpaceCaptainBlock.TryClaimShip and RadarPanelBlock's scan self-drive). The machine-
        // ownership gate is what keeps the local player's keyboard from driving every OTHER
        // player's thrusters on the host.
        private void FixedUpdate()
        {
            if (BlockBehaviour == null || !BlockBehaviour.isSimulating || Advanced)
            {
                return;
            }
            if (!ThrusterNet.IsLocallyOwned(BlockBehaviour))
            {
                return;
            }

            bool held = ThrustKey != null && ThrustKey.IsHeld;
            if (NetAuthority.IsAuthority)
            {
                keyHeld = held;
                return;
            }
            // Client: the host owns the physics, so only the intent travels — and only on the
            // edges, so a coasting ship sends nothing at all.
            if (held != lastSentKeyHeld)
            {
                lastSentKeyHeld = held;
                ModNetworking.SendToAll(ThrusterNet.KeyMsg.CreateMessage(PlayerID, GuidHash, held));
            }
        }

        private void Update()
        {
            if (fx != null)
            {
                fx.SetOutput(CurrentOutput);
            }
            if (Time.time >= nextInfoRefresh)
            {
                nextInfoRefresh = Time.time + 0.1f;
                OutputInfo.Set(BlockBehaviour != null && BlockBehaviour.isSimulating
                    ? (CurrentOutput * 100f).ToString("F0") + " %"
                    : (AdvancedControl.IsActive ? "captain-commanded" : "key-controlled"));
            }
        }

        // Host-only. `ratio` is the allocator's (or the key's) demand in [0,1] BEFORE the block's
        // own trim slider; the grid then decides how much of that it can actually pay for. Because
        // power goes as thrust squared, a half-fed request comes back out as ~71% thrust rather
        // than some invented degradation curve.
        public void DriveThrust(ShipState ship, float ratio, float deltaTime)
        {
            if (!NetAuthority.IsAuthority)
            {
                return;
            }
            if (!IsAlive)
            {
                CurrentOutput = 0f;
                return;
            }

            float demand = Mathf.Clamp01(ratio) * RatioCap;
            if (demand <= 0.0001f)
            {
                CurrentOutput = 0f;
                return;
            }

            float energyRatio = 1f;
            if (ship != null)
            {
                float megawatts = SpaceBalance.ThrusterPowerDraw(NozzleArea, demand);
                energyRatio = ship.Energy.Request(EnergyBus.Thrust, megawatts * deltaTime);
            }
            float delivered = demand * SpaceBalance.ThrusterOutputForEnergyRatio(energyRatio);
            CurrentOutput = delivered;

            if (Body != null && delivered > 0.0001f)
            {
                Body.AddForce(-transform.forward * (MaxThrust * delivered), ForceMode.Force);
            }
        }

        // The block's contribution column for the allocator: unit force direction and the torque
        // it generates about the ship's center of mass, both in world space, scaled by everything
        // that caps this particular nozzle.
        public void GetWrench(Vector3 centerOfMass, out Vector3 force, out Vector3 torque)
        {
            force = -transform.forward;
            torque = Vector3.Cross(transform.position - centerOfMass, force);
            float scale = MaxThrust * RatioCap;
            force *= scale;
            torque *= scale;
        }

        // Thruster detonation (ADR-0018 §10), same debris-not-erased pattern as the reactor and
        // capacitor: own joints zeroed plus an explosion force, so it tears free next physics step
        // instead of vanishing outside Besiege's own block deletion path. Losing it also changes
        // the ship's center of mass and surviving thruster set — which is exactly the allocator's
        // rebuild trigger, so the survivors re-trim themselves for free.
        public void Detonate()
        {
            if (detonated || !NetAuthority.IsAuthority)
            {
                return;
            }
            detonated = true;
            CurrentOutput = 0f;

            Vector3 position = transform.position;
            Vector3 driftVelocity = Body != null ? Body.velocity : Vector3.zero;

            ShipState ship = OwnShip();
            if (ship != null)
            {
                ship.Allocator.MarkRosterDirty();
            }

            if (StatMaster.isMP)
            {
                ModNetworking.SendToAll(SubsystemDetonationNet.DetonateMsg.CreateMessage(
                    position, driftVelocity, MaxThrust, 2));
            }

            SpaceEffectAssets.PlayCapacitorExplosion(position, driftVelocity, MaxThrust);
            BreakOwnConnectionJoints();
            SubsystemDetonation.ApplySecondaryBlast(position,
                SpaceBalance.ThrusterBlastRadius(MaxThrust),
                SpaceBalance.ThrusterBlastDamage(MaxThrust),
                SpaceBalance.ThrusterBlastForce(MaxThrust));
        }
    }
}
