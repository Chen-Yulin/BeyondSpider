# Gunner and Single-Block Flak Fire-Control Implementation Plan

> **For agentic workers:** This plan records the implementation of `docs/superpowers/specs/2026-07-06-gunner-and-flak-fire-control-design.md`. Steps use checkbox (`- [x]`) syntax because the implementation was completed in commit `a243fb6`.

**Goal:** Make `SpaceGunnerBlock` behave like the WW2/Naval gunner by binding same-player vanilla steering wheels and railguns through shared emulator keys, then aiming player-built turrets at the captain-computed fire-control lead point. Make `SpaceFlakTurretBlock` use the same locked-target fire-control source while keeping its single-block visual yaw/pitch model and zero-gravity projectile behavior.

**Architecture:** Captain remains the lock and fire-control authority. `SpaceCaptainBlock.TryGetLockedFireSolution(Vector3, float, out FireSolution)` returns a zero-gravity lead point for a coarse 10 m muzzle-position bucket and muzzle-velocity bucket, caching solutions per fixed tick. Player-built turrets use a new `SpaceGunnerHinge` helper attached to vanilla `SteeringWheel` blocks via `Events.OnBlockInit`; active gunners bind hinge groups and railguns by matching emulator keys, write signed-angle steps into `SteeringWheel.AngleToBe`, respect `Flipped` and `LimitsSlider`, and fire through Besiege key emulation. Single-block flak turrets do not bind hinges; they keep their direct visual base/gun transform drive and ask Captain for the same locked fire-control solution when the ship is in anti-ship priority.

**Tech Stack:** C# (.NET Framework 3.5), Besiege ModLoader (`Modding`/`Modding.Blocks`), Unity legacy API surface.

## Global Constraints

- Read and write project Markdown as UTF-8.
- Keep WW2/Naval-style binding key-based: physical keys and emulator keys must be matched through Besiege `MKey` abstractions, not custom raw input polling.
- Do not use private reflection for `SteeringWheel`; public `KeyList`, `AngleToBe`, `axis`, `Flipped`, `ReturnToCenterToggle`, `SpeedSlider`, and `LimitsSlider` are enough.
- Do not use candidate micro-rotations to infer hinge direction. Ship/body motion can corrupt that signal; compute the signed angle directly around each hinge's own world axis.
- `FireControl` on `RailgunBarrelBlock` only selects a reference gun for aiming and fire-control opt-in. Railguns still blindly respond to their fire key when that key is emulated.
- `GunGroup` filters gunner binding/reference selection only; it is not a lower-level gate on emulated key delivery.
- Keep single-block flak fire/projectile effects aligned with existing naval-derived effects, but no gravity is applied to the projectile physics.
- Follow existing codebase conventions: `SpaceBlock` lifecycle methods, `SingleInstance<T>` network/controller singletons, `ModNetworking.CreateMessageType`, and C# syntax compatible with the existing project target.

---

### Task 1: Captain exposes quantized locked-target fire control

**Files:**
- Modify: `src/BeyondSpiderAssembly/SpaceCaptainBlock.cs`

**Interfaces:**
- Produces: `SpaceCaptainBlock.TryGetLockedFireSolution(Vector3 approximateMuzzlePosition, float muzzleVelocity, out FireSolution solution)`.
- Consumes: `ship.LockedTarget`, `ship.Tracks`, `SpaceBallistics.EstimateInterceptTime`, `SpaceBallistics.AimPoint`.

- [x] Add 10 m position bucketing and muzzle-velocity bucketing constants.
- [x] Cache locked fire-control solutions per fixed tick so nearby barrels sharing muzzle speed reuse the same calculation.
- [x] Resolve the current locked target through the latest ship track before computing lead.
- [x] Compute the lead point using the target's tracked position/velocity and zero-gravity projectile speed.
- [x] Fail closed when there is no alive locked target, no current track, or no valid ship/captain transform.

### Task 2: Attach and register WW2-style hinge helpers

**Files:**
- Create: `src/BeyondSpiderAssembly/SpaceGunnerSupport.cs`
- Modify: `src/BeyondSpiderAssembly/Mod.cs`
- Modify: `src/BeyondSpiderAssembly/BeyondSpiderAssembly.csproj`

**Interfaces:**
- Produces: `SpaceGunnerHingeController`, `SpaceGunnerHingeRegistry`, `SpaceGunnerHinge`.
- Consumes: vanilla `SteeringWheel` and `BlockBehaviour` instances created by Besiege.

- [x] Add `SpaceGunnerHingeController` as a `Mod.Root` singleton and subscribe to `Events.OnBlockInit`.
- [x] Detect vanilla `SteeringWheel` blocks and attach one `SpaceGunnerHinge` helper per block.
- [x] Register helpers by player ID for cheap gunner lookup.
- [x] Read left/right controls from public `SteeringWheel.KeyList` indices 0 and 1.
- [x] Preserve original `ReturnToCenterToggle` and `SpeedSlider` values, then apply the WW2-style override only while at least one gunner owns the hinge.
- [x] Clamp `SteeringWheel.AngleToBe` to `LimitsSlider` when vanilla limits are active.

### Task 3: Drive arbitrary-orientation player-built turrets

**Files:**
- Modify: `src/BeyondSpiderAssembly/RailgunBarrelBlock.cs` (`SpaceGunnerBlock`)

**Interfaces:**
- Consumes: `SpaceGunnerHingeRegistry.HingesFor(PlayerID)`, bound railguns in `ship.Guns`, captain fire-control query.
- Produces: gunner hinge commands through `SteeringWheel.AngleToBe`.

- [x] Add emulator keys for fire, orientation left/right, and pitch up/down.
- [x] Add `TrackingSpeed` so the gunner owns automated hinge tracking speed instead of relying on the player-facing steering-wheel speed slider.
- [x] Refresh bindings periodically while active, grouping hinges by shared orientation/pitch keys.
- [x] Discover bound railguns by shared fire key and matching `GunGroup`.
- [x] Choose the nearest bound `FireControl` railgun as the stable reference gun until it becomes invalid or disabled.
- [x] Ask Captain for a locked-target fire-control solution in anti-ship priority; otherwise keep the existing defensive-solution fallback.
- [x] Compute signed hinge error directly from `hinge.transform.rotation * hinge.Wheel.axis`, the reference gun's `transform.forward`, and the desired aim direction.
- [x] Invert the applied command when `SteeringWheel.Flipped` is set.
- [x] Stop emulating fire whenever the gunner is inactive, lacks a reference gun, lacks a valid solution, or the reference gun is outside `AimTolerance`.

### Task 4: Fire through emulator keys instead of direct gun calls

**Files:**
- Modify: `src/BeyondSpiderAssembly/RailgunBarrelBlock.cs`

**Interfaces:**
- Consumes: Besiege `MKey.EmulationPressed()` / `MKey.EmulationHeld()`.
- Produces: normal railgun fire requests through the existing railgun trigger path.

- [x] Change `RailgunBarrelBlock` manual fire input to accept real key presses and emulated key presses/holds.
- [x] Make `SpaceGunnerBlock` report `EmulatesAnyKeys`.
- [x] Use protected `EmulateKeys(new MKey[0], FireKey, true/false)` from the gunner instead of calling `RailgunBarrelBlock.TryFireControl`.
- [x] Leave reload, energy draw, recoil, projectile spawning, and visual effects owned by `RailgunBarrelBlock`.

### Task 5: Sync gunner active state and draw active icon

**Files:**
- Create/modify: `src/BeyondSpiderAssembly/SpaceGunnerSupport.cs`
- Modify: `src/BeyondSpiderAssembly/RailgunBarrelBlock.cs`
- Modify: `src/BeyondSpiderAssembly/Mod.cs`

**Interfaces:**
- Produces: `SpaceGunnerNet.ActiveMsg`.
- Consumes: `"BS Migrated Gunner Alert Texture"`.

- [x] Add `SpaceGunnerNet` mirroring `SpaceFlakTurretNet` registration and active-state callback style.
- [x] Register/unregister gunners by player ID and building block GUID hash on simulate start/stop.
- [x] Broadcast active toggles from the host path.
- [x] Receive active state on clients for consistent icon display.
- [x] Draw the WW2/Naval-style gunner alert icon above active gunners with the existing world-anchored `OnGUI` pattern.

### Task 6: Let single-block flak use captain fire control

**Files:**
- Modify: `src/BeyondSpiderAssembly/SpaceFlakTurretBlock.cs`

**Interfaces:**
- Consumes: `SpaceCaptainBlock.TryGetLockedFireSolution`, `ship.DefensiveSolution`, existing flak muzzle/velocity helpers.
- Produces: `fireControlTarget` / `hasTarget` for the existing visual aiming and firing loop.

- [x] In anti-ship priority, ask Captain for a locked-target fire-control solution using the flak muzzle position and caliber-derived muzzle velocity.
- [x] Fall back to the existing defensive solution when anti-ship lock fire control is unavailable.
- [x] Keep existing local yaw/pitch transform split for the base and gun visuals.
- [x] Preserve range checks, yaw limit, pitch clamp, aim readiness, energy request, projectile/effect spawning, and multiplayer shot/state sync.
- [x] Keep zero-gravity projectile behavior.

### Task 7: Verification and review

**Commands:**
- `dotnet build .\src\BeyondSpiderAssembly\BeyondSpiderAssembly.csproj`
- `git diff --check`
- `rg -n "TODO|TBD|FindObjectsOfType|ApplyShieldEffect|Iff.IsActive && target.Team" src\BeyondSpiderAssembly -S`

- [x] Build passes. Remaining warning is the pre-existing `DynamicText` architecture mismatch.
- [x] Diff check passes; Git reports only CRLF conversion warnings.
- [x] Main-source scan has no hits for placeholder markers or obsolete implementation patterns. Older design docs and `src/BeyondSpiderAssembly_bkp` may still contain historical references.
- [x] Manual review found no blocking issue in the implemented fire-control path.
- [x] Commit completed as `a243fb6 feat: add gunner hinge fire control`.

## Follow-Up Test Notes

- In-game validation should place one active `SpaceGunnerBlock`, two vanilla steering-wheel hinge groups, and multiple railguns sharing the gunner fire key. Toggle `FireControl` on exactly one nearby railgun first, then add a second reference gun to confirm nearest-reference stability.
- Mount one turret sideways or upside down to confirm signed-angle hinge drive uses the hinge's own transform instead of ship/captain axes.
- Flip one steering wheel and confirm the gunner reverses command sign correctly.
- Lock an enemy ship from Captain in anti-ship priority and confirm player-built railguns plus `SpaceFlakTurretBlock` aim at the lead point; switch to anti-air priority or clear the lock and confirm defensive fallback still works.
