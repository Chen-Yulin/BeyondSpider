# Host-authoritative multiplayer: clients render, the host decides

The mod's MP rule is now enforced everywhere, matching the proven WW2-Naval / ModernAirCombat convention: **the host runs all physics, target locking, and damage; clients only display and smooth**. Besiege vanilla already syncs block key presses and machine transforms, so the mod layers only small event/state messages on top.

**Authority fence** (`NetAuthority.cs`): `IsAuthority = !isMP || !isClient` (host in MP, always in SP), `IsClient = isMP && isClient`. Every damage/physics call site fences on these instead of re-deriving StatMaster flags. The load-bearing simplification: *every* `SpaceKineticRound`/`MissileProjectile` on a client is by definition a visual mirror (authoritative ones only ever spawn host-side), so `NetAuthority.IsClient` **is** the "visual only" flag — no per-projectile bool.

**Fenced call sites** (client → early-return / visual branch):

- `SpaceKineticRound.FixedUpdate` → `ClientVisualMarch`: same sweep raycast but no damage/knockback — pass through breached targets and plain blocks like the host march, die with a pierce spark at the first standing armor plate or hard geometry, and **ricochet locally** (user decision): the client rolls its own random through the shared `RollRicochet` gate and bounces via the shared `ApplyRicochetKinematics` — the mirror's bounce may diverge from the authoritative round's fate (accepted; rounds are short-lived, and a round that would breach — budget ≥ net-synced `RemainingKineticHP` — never rolls, matching the host's resolution order). Defensive fences in `ResolveTarget`/`ApplyShieldDeceleration`.
- `MissileProjectile.FixedUpdate` → `MirrorStep` (below); fences in `OnTriggerEnter`, `ApplyDamage`, `ApplyShieldDeceleration`, `ApplyBlastDamageToArmor`.
- `NanoArmorBehaviour.ApplyPhysicalDamage` is the single armor choke point (kinetic/laser/blast all funnel through it; structural weakening, breach force, and joint changes have no other caller).
- `DamageRouter.RouteLaserHit`; `SpaceGunnerHinge.FixedUpdate` (+ clients never `AddOwner` in `RefreshBindings` — replica turrets are posed by vanilla sync, forcing wheel state there only fights it).

**Net singletons** (all `SingleInstance<T>` on Mod.Root, registry keyed `PlayerID + GuidHash` like the pre-existing flak/captain/launcher nets; callbacks wired in `Mod.OnLoad`):

| Net | Message | Payload | Cadence |
|---|---|---|---|
| `RailgunNet` | `ShotMsg` | muzzlePos, forward, worldVelocity, caliber, lifetime | per shot (flak ShotMsg pattern; client spawns a mirror round, muzzle sound + tail glow come free from `SpawnKineticRound`) |
| `LaserNet` | `FireMsg` | endPoint, hitNormal, hitType (miss/reflect/sparks/pierce), reflectivity | per pulse (client replays beam + the impact flavor the hitType names; no damage rides along) |
| `MissileLauncherNet` (+) | `StateBatchMsg` | ids[] interleaved (ownerPlayerId, netId, burning01) + states[] interleaved (pos, vel) for EVERY live missile | one batch / 0.05 s, chained ≤40 missiles per message (~1.5 KB cap) |
| `MissileLauncherNet` (+) | `DetonateMsg` | netId, blastPos, blastVel | once; lost msg ⇒ mirror coasts to TTL without a fireball (accepted) |
| `NanoArmorNet` | `BatchMsg` | playerId, guidHashes[], interleaved [integrity, structural]× | dirty-flag flush 0.2 s, ≤60 blocks/msg, full re-send of touched blocks every 5 s as loss insurance |
| `ShieldNet` | `StateMsg` | active, upkeepRatio, intercepted | 0.08 s while intercepting/changed, 0.25 s heartbeat (flak SyncStateIfNeeded cadence) |
| `ShipEnergyNet` | `EnergyMsg` | coreGuidHash + 4 bus charge levels | 0.25 s per ship |

**Missile mirrors**: no guidance/fuze/retarget on clients — the mirror dead-reckons on its (non-kinematic) rigidbody velocity, bleeds the last snapshot's position error in at 20 %/tick (`pendingCorrection`), snaps beyond 12 m, and mirrors the host's burn flag for the engine glow. The snapshot source is a **central array batch** (NanoArmorNet's idiom): `MissileLauncherNet.FixedUpdate` on the host walks the missile registry every 0.05 s and emits one `StateBatchMsg` covering every live missile — chosen over the earlier per-missile 0.1 s messages because message COUNT, not bytes, was the scaling risk (50 missiles: 20 msg/s instead of the 500/s this cadence would otherwise cost; bytes rise only ~40 % for twice the refresh rate). Death arrives via `DetonateMsg` → cosmetic `PlayMissileBlast` + destroy; damage/forces stay host-side and reach clients through vanilla machine sync. Mirrors stay registered as `ITrackable`/`ILockable` (shared netId = the CaptainLockNet identity).

**Armor**: clients apply `BatchMsg` values verbatim (`ReceiveNetworkState` — no `ApplyStructuralWeakening`, no joints, no breach force), which keeps the HP overlay, info-panel aggregates, and the client rounds' breach pass-through honest. Batches, not per-hit events: hits are bursty (one blast dirties dozens of blocks in a tick) and repair re-dirties every damaged block every tick; `IntegerArray`/`SingleArray` payloads (proven in WW2-Naval CrewUpdateMessage) make a batch one message. Worst case (~300-block ship fully repairing) ≈ 25 msg/s.

**Shield**: authoritative interception + energy pricing stay host-only; `SimulateUpdateClient` toggles power locally for responsiveness (heartbeat corrects within 0.25 s); the client ticks `UpdateVisual` off received state. Clients ALSO run a **cosmetic interception pass** (`ClientIntercept`, user decision): same gates and `DesiredDeltaV` math against their own mirror rounds/missiles, driven by the synced `active` + `upkeepRatio` (which already carries starvation and the overload drain) — no energy is ever charged, and `ApplyShieldDeceleration` on the projectiles is deliberately un-fenced because slowing a visual mirror to match the host's field is display smoothing. The generator's velocity for the relative-speed gate is estimated from its own transform motion (replica rigidbody velocity is meaningless under vanilla sync). Missiles are additionally corrected by their 10 Hz stream; rounds live or die on this pass alone.

**Radar**: `RadarPanelBlock`'s scan moved `SimulateFixedUpdateHost` → `SimulateFixedUpdateAlways` — it only reads the local trackable registry (populated identically on clients: cores, mirror missiles, mirror rounds) and writes the local `ship.Tracks`, so client radar screens now show blips. Locking / channel-0 threat list remain host-authoritative (`CaptainLockNet`, unchanged).

**Energy display**: consumption is host-only, so a client's local grid drifts to 100 %. `ShipState.DisplayChargeLevel` prefers the host-streamed levels when fresh (<1 s); the info panel and the IMGUI fallback both read it.

**Status**: implemented, builds clean; pending host+client playtest.

**Considered options**

- *Per-projectile `isVisualOnly` flag* — rejected: `StatMaster.isClient` already encodes it; a flag invites desync between the flag and the peer's role.
- *Host-broadcast impact events for kinetic rounds (WW2-Naval ExploMsg style)* — rejected: rounds here are locally simulated visual dummies, so a local visual-stop raycast reproduces the impact point/FX without extra messages; with the client cosmetic shield pass, even the deceleration divergence is closed to the per-event energy-ratio residual.
- *Skipping shield deceleration on client mirrors entirely* — initially shipped, then reverted per user decision: a client watching shells sail unslowed through a visibly active field reads as a bug; the cosmetic pass keyed off the synced shield state fixes it without touching authority.
- *Kinematic client missile mirrors driven purely by snapshots* — rejected: keeping the rigidbody dynamic makes velocity integration free extrapolation between snapshots; kinematic would stutter or need per-frame interpolation code.
- *Per-missile state messages (initial implementation, 0.1 s each, netId-staggered)* — replaced by the array batch above when the user asked for a 0.05 s cadence: per-missile messages at that rate would hit ~500 msg/s in a 50-missile brawl.
- *Per-hit armor damage events* — rejected for unbounded message rate under repair + burst hits; see batch design above.
