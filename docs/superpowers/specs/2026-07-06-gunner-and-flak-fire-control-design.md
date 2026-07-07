# Gunner and single-block flak fire-control design

## Context

This work extends the already-implemented captain radar lock and ship-wide `LockedSolution` into two weapon families that still have incomplete control behavior:

- `SpaceGunnerBlock` currently only checks whether grouped `RailgunBarrelBlock` instances are already pointed near a firing solution, then calls `TryFireControl`. It does not bind or drive any player-built turret hinges yet.
- `SpaceFlakTurretBlock` already has separate visual yaw and pitch transforms, zero-gravity rounds, AA-style muzzle effects, active-state networking, and energy use, but it only reads `ship.DefensiveSolution`.
- `SpaceCaptainBlock` already computes `ship.LockedSolution`, but the current lead point uses `track.Position + track.Velocity * clamp(track.TimeToImpact, 0, 2)`, which is not per-weapon and does not use each gun's muzzle velocity.

The new requirement is to make the gunner and the single-block flak turret behave like the WW2/Naval reference while respecting space-combat differences: arbitrary turret orientation on a ship, no gravity for space rounds, and fire-control lead derived from target position/velocity and each weapon's projectile speed.

## Reference Facts

The project Markdown was read as UTF-8. `mod-interaction-manual.md` records the WW2/Naval behavior relevant to this feature:

- WW2 `Gunner` binds by shared key mappings, not by entering a seat or selecting a block in a UI. It scans same-player hinges and guns, then matches the gunner's emulator keys against the target block keys.
- WW2 hinge control writes to Besiege's `SteeringWheel.AngleToBe`, with `SteeringWheel.Flipped` affecting the sign. The reference does not drive `HingeJoint.motor` directly.
- WW2 `WW2Hinge` is a helper attached alongside `SteeringWheel`; it registers hinges for the gunner and temporarily overrides return-to-center/speed behavior while a gunner controls it.
- WW2 `AABlock` does not use physical hinges. It builds a base transform and a gun transform, then writes local yaw/pitch angles directly for the visual turret.
- The Modern Project `HingeDriver` independently confirms the same low-level control surface: automatic hinge control writes `SteeringWheel.AngleToBe`.
- Reflection against `Besiege_Data/Managed/Assembly-CSharp.dll` confirms that `SteeringWheel` exposes `AngleToBe`, `Flipped`, `LimitsSlider`, `ReturnToCenterToggle`, `SpeedSlider`, `Keys`, and a public `KeyList`. It also has private `leftKey`/`rightKey` fields, but WW2-style consumers can avoid private reflection by reading the public `KeyList` in the steering-wheel key order.
- Reflection confirms `MKey` has `Equals`, `GetKey`, `EmulationHeld`, `EmulationPressed`, and `EmulationReleased`, so physical and emulator key matching can use Besiege's key abstraction instead of raw input polling.

## Proposed Terms

**单零件防空炮** should be the canonical name for the existing `SpaceFlakTurretBlock` role in this feature. The current glossary already says `单零件炮` is the broad category and avoids using "AA" as the canonical term, so "AA单零件炮" should be treated as informal shorthand only.

**火控命中点** should mean the predicted world-space point where a weapon should aim, calculated from shooter position, target position, target velocity, and that weapon's projectile speed in zero gravity.

**炮塔铰链组** should mean the two bound `SteeringWheel` hinge sets controlled by one gunner: orientation/yaw hinges and pitch hinges.

## Design Pressure

The hard part is not simply calculating a lead point. The difficult part is choosing a stable sign convention for arbitrary player-built turrets:

- In naval combat, turret orientation can often assume a ship-aligned deck plane.
- In this mod, a space turret's orientation hinge might be mounted sideways, upside down, or on a ship whose captain transform is unrelated to the hinge frame.
- Therefore the controller must judge turn direction from the hinge's own `transform.rotation`, the gun/barrel's current `transform.forward`, and `SteeringWheel.Flipped`, not from ship global yaw/pitch assumptions.

For single-block flak, the same rule applies at the weapon level: yaw/pitch should be derived from the turret block's own transform and visual child transforms, not from the ship orientation.

## Decisions

### 1. Fallback behavior when no captain lock exists

The requirement says "when a locked target exists" the gunner and single-block flak should aim at the lead point. It does not explicitly say what they do without a lock.

Decision: keep the current defensive fallback. In `AntiShip` priority with a valid `ship.LockedSolution`, both weapon families use the captain lock. Otherwise they continue using `ship.DefensiveSolution`, preserving current missile-defense behavior and matching the existing `CiwsBlock`/`SpaceGunnerBlock` priority pattern.

### 2. Gunner hinge binding surface

The requirement says the gunner should bind pitch and orientation hinges exactly like WW2/Naval, via virtual or physical key binding.

Decision: implement the same key-matching model with five gunner keys: left/right orientation, up/down pitch, and fire. The gunner discovers same-player `SteeringWheel` blocks whose key mappings match those keys, groups them into orientation and pitch hinge sets, discovers grouped `RailgunBarrelBlock` guns by matching fire key and `GunGroup`, then drives only those bound hinges while active.

This implies adding emulator keys to `SpaceGunnerBlock` rather than only a `GunGroup` text field:

- orientation left/right keys bind orientation hinges
- pitch up/down keys bind pitch hinges
- fire key binds railgun barrels in the same group

The existing `GunGroup` remains useful as a safety filter, so accidentally sharing a key across unrelated weapons does not bind every same-player gun on the machine.

### 3. Reading SteeringWheel left/right keys

Reflection against `Assembly-CSharp.dll` shows `SteeringWheel.Keys` is public and `SteeringWheel.KeyList` is a public `List<MKey>`. The semantically named `leftKey` and `rightKey` fields are private, but they are also represented in the public key list used by Besiege's mapper.

Decision correction: do not use reflection for normal hinge binding. Use `SteeringWheel.KeyList` in the same WW2-style way: key index 0 is the left-side control and key index 1 is the right-side control. If a future Besiege version exposes a different public shape, the binding helper should fail closed rather than falling back to private-field reflection by default.

### 4. Hinge turn direction in arbitrary turret frames

Reflection confirms `SteeringWheel` also exposes a public `axis` vector. The gunner therefore has enough public data to reason from the hinge's own transform and configured axis, rather than from ship/captain orientation.

Decision correction: do not use candidate micro-rotations to infer direction. A rotating ship body and physical hinge latency can make a "try a tiny rotation, see whether it got closer" test noisy or wrong. Instead calculate the signed angle directly around the hinge axis:

```csharp
Vector3 axisWorld = hinge.transform.rotation * hinge.axis;
Vector3 current = Vector3.ProjectOnPlane(primaryGun.transform.forward, axisWorld).normalized;
Vector3 desired = Vector3.ProjectOnPlane(aimDirection, axisWorld).normalized;
float signedAngle = Mathf.Atan2(
    Vector3.Dot(axisWorld, Vector3.Cross(current, desired)),
    Vector3.Dot(current, desired)) * Mathf.Rad2Deg;
```

The sign of `signedAngle` is the desired turn direction around this hinge's own axis; its magnitude can scale the per-tick `AngleToBe` step. `SteeringWheel.Flipped` still inverts the control command after the geometric sign is known.

This avoids special-casing "yaw" as ship-up or "pitch" as ship-right. The labels orientation/pitch describe the binding group, not a world or ship axis.

### 5. Quantized per-weapon lead from captain fire control

The current `ShipState.LockedSolution` stores one `AimPoint`, but the new requirement says fire control must use the target's velocity/position and each weapon type's muzzle velocity. Shooter position matters too: two railgun barrels on opposite ends of a capital ship can need slightly different lead directions.

Decision correction: keep Captain as the lock and fire-control authority, but quantize weapon fire-control requests so every individual gun does not recalculate the same lead every tick. `SpaceCaptainBlock` should maintain a current locked-track snapshot (`target`, `position`, `velocity`, `valid`) and expose a captain-owned query such as `TryGetLockedFireSolution(Vector3 approximateMuzzlePosition, float muzzleVelocity, out FireSolution solution)`.

The query rounds `approximateMuzzlePosition` into a coarse 10 m bucket and combines that with a muzzle-velocity bucket/type key. Captain caches the resulting `FireSolution` for the current fixed tick. Multiple guns whose muzzle positions fall within the same 10 m bucket and share the same projectile speed reuse one lead point. This preserves important bow/stern or port/starboard differences on a large ship without making fire-control cost scale with every individual barrel.

The zero-gravity solver should be `SpaceBallistics.EstimateInterceptTime` / `AimPoint`, not the WW2 gravity/drag iteration, because all current BeyondSpider kinetic rounds use `Rigidbody.useGravity = false`.

### 6. Hinge discovery and registration

`NanoArmorController` already proves this project can safely attach helper behaviours to vanilla blocks via `Events.OnBlockInit`, matching the same class of technique WW2 used for wooden armour and hinge helpers.

Decision: add a `SpaceGunnerHingeController` singleton on `Mod.Root`. It subscribes to `Events.OnBlockInit`, detects vanilla `SteeringWheel` blocks, and attaches a lightweight `SpaceGunnerHinge` helper. The helper stores `BlockBehaviour`, `SteeringWheel`, player/team, public `KeyList`, and original `ReturnToCenterToggle`/`SpeedSlider` values; it registers/unregisters itself in a small `SpaceGunnerHingeRegistry` keyed by player ID.

`SpaceGunnerBlock` then binds by querying this registry instead of calling `FindObjectsOfType<SteeringWheel>()` each tick. Binding can refresh on simulate start and at a low rate while active, but ordinary aiming ticks operate on cached hinge lists.

When any active gunner owns a hinge, the helper can apply the WW2-style return-to-center/speed override. When no gunner owns it, it restores the original values.

### 7. Single-block flak target source

`SpaceFlakTurretBlock.UpdateFireControlTarget()` currently reads only `ship.DefensiveSolution`, then recalculates a zero-gravity lead using its own caliber-derived muzzle velocity. It already has the right local yaw/pitch visual split and active-state behavior; the missing piece is choosing the same fire-control source as the rest of the ship.

Decision: use the same priority rule as `CiwsBlock` and `SpaceGunnerBlock`: when `ship.Priority == AntiShip` and the captain has a valid locked target, the flak turret asks Captain for a quantized locked fire solution using `GetMuzzlePosition()` and `InitialVelocity(caliber)`. Otherwise it falls back to `ship.DefensiveSolution` and can continue recalculating lead for its own muzzle velocity. The active toggle remains a hard gate in both modes.

The turret should still enforce its own range, yaw limit, pitch clamp, aim readiness, energy request, small-particle damage path, large-round spawning path, and multiplayer shot/state sync.

### 8. Gunner active icon

`BeyondSpider/Mod.xml` already registers `"BS Migrated Gunner Alert Texture"` from `WW2Migrated/Gunner/alert.png`, and `RailgunBarrelBlock.OnGUI()` already uses the project-standard `Camera.main.WorldToScreenPoint` + `GUI.DrawTexture` pattern for world-anchored weapon UI.

Decision: when a `SpaceGunnerBlock` is active, draw a small world-anchored OnGUI icon above the gunner block using `"BS Migrated Gunner Alert Texture"`. This mirrors the WW2/Naval active icon closely enough without importing the full `BlockUIManager` follower system. The active state itself should be network-synced, so the icon state is consistent in multiplayer, but the drawing remains a local UI concern on each client.

### 9. Gunner firing path

The requirement asks for hinge binding to be WW2-like and mentions virtual/physical keys for binding. The current `SpaceGunnerBlock` directly calls `RailgunBarrelBlock.TryFireControl(solution)`, which bypasses manual key emulation but preserves the railgun's own reload, energy, recoil, projectile, and effect logic.

Decision correction: use key emulation for firing, matching WW2/Naval. The gunner's fire emulator key both discovers/binds guns and synthesizes the fire input while the bound turret is aimed within tolerance. `SpaceGunnerBlock` should declare `EmulatesAnyKeys`, create its fire/orientation/pitch keys via `AddEmulatorKey`, and call protected `EmulateKeys(new MKey[0], FireKey, true/false)` in the same style as the Modern Project `KeyEmulator`.

`RailgunBarrelBlock` must therefore treat emulated fire the same as real fire: its input latch should check `FireKey.IsPressed || FireKey.EmulationPressed()` for single-shot behavior, or `FireKey.IsHeld || FireKey.EmulationHeld()` if the final firing model becomes hold-to-fire. The railgun still owns reload, energy, recoil, projectile spawning, and visual effects; the gunner only emulates the player's trigger at the right moment.

### 10. Railgun `FireControl` toggle under key emulation

`RailgunBarrelBlock` already has a `FireControl` toggle. Before key emulation, `SpaceGunnerBlock` checked this toggle before directly calling `TryFireControl`. After key emulation, the railgun can distinguish real player input (`FireKey.IsPressed`) from emulated input (`FireKey.EmulationPressed()`).

Decision correction: `FireControl` means "this gun can be used as the gunner's fire-control reference gun" and can also serve as the opt-in for gunner/automatic control. The gunner should choose one bound railgun with `FireControl.IsActive` as the primary reference for aim direction, muzzle position bucket, muzzle velocity, and aim-tolerance checks. Other bound guns in the same group can receive the emulated fire key when the reference gun is on target, but guns without `FireControl` should not determine where the turret aims.

Manual fire remains independent of `FireControl`. Emulated fire also does not require `FireControl`: railguns should blindly respond to their fire key being emulated, matching the WW2/Naval shared-key model. `FireControl` only decides whether a bound railgun can be selected as the gunner's reference gun for aiming.

`GunGroup` is likewise a gunner binding/reference selection filter, not a hard gate on the lower-level emulated key event. A railgun whose fire key is emulated should respond according to its own fire-key state; the gunner uses `GunGroup` to decide which guns it considers bound and which `FireControl` gun can be the reference.

### 11. Choosing the gunner reference gun

If a gunner binds multiple railguns and more than one has `FireControl.IsActive`, the turret needs one stable reference gun for aiming. Otherwise hinge commands can oscillate between barrels that are offset or mounted slightly differently.

Decision: choose the nearest bound `FireControl` railgun to the gunner block at bind refresh time and keep it as the reference until it becomes invalid, disabled, out of group, or no longer has `FireControl` enabled. If it becomes invalid, reselect with the same rule. This is deterministic, player-controllable by placement, and does not require adding a new priority slider.

### 12. Gunner hinge drive speed

`SpaceGunnerBlock` currently has `AimTolerance` but no tracking-speed setting. Vanilla `SteeringWheel` has its own `SpeedSlider`, but the WW2 helper temporarily overrides that while the gunner controls the hinge, so relying on the vanilla slider directly would make automation tuning opaque.

Decision: add a `TrackingSpeed` slider to `SpaceGunnerBlock` and use it to cap the per-fixed-tick `AngleToBe` step. The signed hinge angle from section 4 provides the error; the command step is `sign(error) * min(abs(error), TrackingSpeed.Value * Time.fixedDeltaTime * scale)` or an equivalent tuned step. The helper still applies the WW2-style `ReturnToCenterToggle`/`SpeedSlider` override only to keep vanilla hinge behavior from fighting the gunner.

### 13. Gunner active-state networking

`SpaceGunnerBlock` currently toggles `active` locally on the host and does not sync that state. The requested WW2-style active icon needs every client to know whether the gunner is active.

Decision: add `GuidHash` to `SpaceGunnerBlock` and a small `SpaceGunnerNet` singleton mirroring `SpaceFlakTurretNet.ActiveMsg`: `(playerId, guidHash, active)`. Register/unregister gunners in the network singleton on simulate start/stop. When the active switch is pressed, update local state and broadcast. Receivers call `ReceiveActive(bool)`. No continuous aim-state sync is required for player-built physical hinges; the game/host physics remains authoritative, and the icon only needs the active bool.

### 14. SteeringWheel limits

The gunner writes `SteeringWheel.AngleToBe`, so it can push the desired angle past the player's configured steering-wheel limits unless it clamps after each command step.

Decision: obey the vanilla `SteeringWheel.LimitsSlider`. After applying a gunner command step, if limits are active, clamp `AngleToBe` into that hinge's allowed range before returning. This preserves the player's original turret firing arc controls and keeps the gunner from forcing the build beyond its configured mechanical envelope.

