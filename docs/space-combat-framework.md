# Space Combat Mod Framework

This design is based on `策划案.md`, with implementation patterns borrowed from `../WW2-Naval Project` and `../ModernAirCombat Project`.

## Design Goal

Build space combat as a capital-ship systems game rather than a pile of independent weapons. A ship has one core identity, a finite total-power output, sensor tracks, directors that turn tracks into firing solutions, and weapons/defenses that consume those outputs.

The main implementation rule should be: blocks register capability, managers aggregate state, directors calculate decisions, and weapons execute.

## New Blocks

**舰船核心 / FusionShipCore**

- Replaces the WW2 naval `Controller` role as the ship identity and command anchor.
- Defines ship-local coordinate frame, ship center, approximate volume, team/player ownership, and radar lock point.
- Exposes build sliders for total power output and power share ratios across nano armor, shield, and weapons.
- Applies reactor mass/power penalty so bigger output means heavier ship.
- Hosts or opens the 3D radar/command UI, derived from WW2 naval captain UI and MAC radar display concepts.

**超级电容 / SuperCapacitor**

- Stores transient energy for one bus: `WeaponOnly`, `ShieldOnly`, `ArmorOnly`, or `Universal`.
- Build size/capacity determines stored energy and mass.
- Buffers bursts from railguns, shield impacts, and nano armor repair.
- Should be a registered provider in `EnergyGrid`, not a free-floating value on each consumer.

**雷达面 / RadarPanel**

- Forward cone sensor similar to MAC `RadarBlock`, but registry-first.
- Produces `SensorTrack` records for ships, heavy missiles, point-defense missiles, and large projectiles.
- Build settings: range, cone angle, scan rate, track type filters, IFF, optional visible scan volume.
- Multiple panels on a ship contribute to one shared ship track picture.

**防御指挥官 / DefenseDirector**

- Consumes sensor tracks and assigns defensive targets.
- Calculates time-to-hit, lead point, priority, and suitable weapon classes.
- Feeds point-defense missiles, CIWS, single-block medium guns, and gunners in AA mode.
- Build settings: defended radius, target priorities, ammo/energy conservatism, friendly-fire/IFF.

**护盾发生器 / ShieldProjector**

- Creates a configurable paraboloid electromagnetic field.
- Consumes shield energy when an eligible physical projectile intersects the field.
- Can fully intercept, partially slow, or fail based on projectile speed, mass/caliber, shield allocation, and available capacitor power.
- Does not affect laser beams.

**单零件近防炮 / CiwsBlock**

- Single-block defensive gun derived from WW2 naval `AABlock`.
- No physical bullet required for small calibers; animation/particles plus DPS-style damage to missiles/projectiles is enough.
- Effective against missiles, large shells, and players in range; damage scales down against faster targets.
- Consumes weapon energy and may be assigned by DefenseDirector.

**单零件中口径炮 / MediumAutoGunBlock**

- Single-block 155/203 style turret for anti-ship and anti-heavy-missile work.
- Uses WW2 large AA style physical shells when needed.
- Longer range than CIWS, worse precision than custom player-built turrets.
- Can obey ship priority: anti-ship first or anti-air first.

**点防御导弹 / PointDefenseMissile**

- Small kinetic interceptor derived from MAC `SRAAMBlock` guidance, but target source comes from DefenseDirector tracks.
- Automatically assigned and launched, with target deconfliction so several missiles do not waste themselves on the same target unless needed.
- Can attack missiles, shells, and players.

**重型核导弹 / HeavyNuclearMissile**

- Slow, massive, shield-penetration-by-survivability weapon.
- On detonation, creates wide thermal/system-disabling effect and damages nano armor by distance.
- Uses MAC missile lifecycle, trail, launch, network sync, and explosion scaffolding, but with new damage model.

**重型反物质导弹 / HeavyAntimatterMissile**

- Slow, massive, direct-hit weapon.
- Small blast radius, ignores nano armor, damages or breaks structural connection points.
- Should be rarer and more precise than nuclear missile.

**激光炮管 / LaserBarrel**

- Barrel-only weapon for player-built turrets.
- Instant or raycast beam, shield-immune, range falloff and dispersion.
- Against intact nano armor, only 5% damage passes through; damage rises as local armor integrity falls.

**电磁炮管 / RailgunBarrel** — implemented, replaces the earlier plain kinetic gun placeholder (`RailgunBarrelBlock`, block ID 10)

- Barrel-only kinetic weapon with build sliders for caliber and muzzle velocity.
- Energy cost and damage are derived from a kinetic-energy-shaped formula (∝ mass·velocity²), not a flat slider — pushing muzzle velocity up is what makes a shot expensive.
- Very high speed makes shield interception more expensive: `ShieldProjectorBlock` reacts to any `SpaceKineticRound` above a hyper-velocity threshold (2200 m/s), slowing or fully stopping it depending on available Shield-bus power.
- Zero-gravity projectile (`SpaceKineticRound`, `useGravity=false`), consistent with the rest of this mod's space-combat kinetic rounds — not gravity-affected like the original WW2 naval `Gun`/`BulletBehaviour` reference.
- Pierce/impact effect is the actual WW2-Naval `Perice` prefab (from the ported `space-perice` asset bundle) plus its ported audio clip; no explosion effect, since a railgun slug has no charge to detonate.

**炮手 / SpaceGunner**

- Adapt WW2 `Gunner`: bind to hinge controls and firing keys, find grouped barrels, and emulate player keys.
- Consumes firing solutions from ShipCore or DefenseDirector.
- Can switch between anti-ship and defensive mode.

## Reuse Map

**Reuse from WW2-Naval**

- `Controller` pattern: ship command block, lock data, UI lifecycle, owner-only GUI, network sync.
- `FireControlManager`: registry grouped by caliber/type; extend to `WeaponRegistry`.
- `AAController`: central AA firing-solution calculation; reshape as `DefenseDirector`.
- `AABlock`: single-block turret appearance, activation sync, tracking, small AA particle fire, large AA shell spawning.
- `Gunner`: hinge discovery, key emulation, group binding, tolerance-based firing.
- `Gun`/`BulletBehaviour`: ballistic shell flight, caliber/velocity concepts, gravity and drag calculations.
- `WoodenArmour` / `DefaultArmour`: basis for nano armor on wood blocks.
- `Battery`: visual/capacity concept for SuperCapacitor, but not its exact gameplay model.
- `BlockUIManager` follower icons and `ModNetworking` message style.

**Reuse from ModernAirCombat**

- `RadarBlock`: non-alloc scan shape, target data, Doppler/IFF ideas, radar panel visualization.
- `DataManager`: global per-player shared sensor/control data, but rename and narrow it for ship systems.
- `CentralController`: multiple subsystem simulators controlled from one block.
- `SRAAMBlock` / `MRAAMBlock`: missile launch state machine, trail/explosion effects, active/passive guidance, proximity fuse.
- `RWRBlock`: optional future sensor-warning UI for ships being painted by radar.
- `DisplayerBlock` / radar screen assets: basis for radar UI if a 3D ship view is too expensive at first.
- Asset bundles: missile trails, explosions, gun effects, screen glass, radar scan assets.

## Core Managers

**ShipRegistry**

- `ShipCore` registers one ship per player/machine.
- Stores transform, team, approximate bounds, center, velocity, alive state, priority mode, and subsystem references.
- Used by radar, missiles, and fire control instead of raycasting the whole machine repeatedly.

**EnergyGrid**

- Owned by ShipCore.
- Aggregates reactor generation, capacitor stores, and consumers.
- Buses: `Armor`, `Shield`, `Weapon`, `Universal`.
- Consumers request `EnergyRequest { bus, amount, peakPower, priority, consumerId }`.
- Returns a satisfaction ratio. Systems degrade by ratio rather than simply turning off.

**TrackRegistry**

- Global list of `ITrackable` combat objects.
- Trackable types: `Ship`, `HeavyMissile`, `PointDefenseMissile`, `LargeProjectile`.
- Each trackable exposes position, velocity, radius/cross-section, team/player, and threat class.
- RadarPanel filters this registry into per-ship `SensorTrack` objects.

**WeaponRegistry**

- Generalizes WW2 `FireControlManager`.
- Registers single-block weapons, barrels, gunners, and missile launchers by ship, group, class, caliber, muzzle velocity, and defensive/offensive role.

**DamageRouter**

- Centralizes shield, nano armor, and direct structural damage.
- Order of operations:
  1. If projectile crosses a shield field, ask ShieldProjector to intercept or slow it.
  2. If it hits hull, apply nano armor mitigation/repair.
  3. If bypassing armor, apply structural or joint damage.
  4. Emit visual effects and network messages.

## Wire-Up Flow

**Build mode**

1. ShipCore updates ship origin, rough size, total transmitted power, and power share ratios.
2. SuperCapacitors register capacity by bus.
3. RadarPanels, ShieldProjectors, weapons, barrels, launchers, and gunners register to the nearest/own ShipCore by player and machine.
4. Weapon groups are configured with text/menu fields like WW2 `Gunner.GunGroup`.

**Simulation start**

1. ShipCore creates/clears `EnergyGrid`, `TrackPicture`, and command UI.
2. Trackable ships, missiles, and projectiles register in `TrackRegistry`.
3. Weapons register in `WeaponRegistry`.
4. RadarPanels begin scan ticks. DefenseDirector initializes available defensive weapons.

**Per fixed update**

1. TrackRegistry refreshes trackable positions cheaply from registered transforms/rigidbodies.
2. RadarPanels filter visible trackables into `SensorTrack`s and update the ship track picture.
3. ShipCore handles manual lock/priority commands.
4. DefenseDirector picks defensive targets and writes firing solutions.
5. Offensive director or ShipCore writes anti-ship firing solutions for selected groups.
6. Gunners, single-block guns, and missile launchers consume firing solutions and request weapon energy.
7. ShieldProjectors and NanoArmor request energy reactively when hit.
8. EnergyGrid resolves generation/capacitor draw and returns effect ratios.

## Energy Model

Use a simple first version:

- Reactor output per second comes from ShipCore total power.
- Armor/Shield/Weapon share sliders are relative power charging/supply ratios; `1/2/2` means Armor receives 20%, Shield 40%, and Weapon 40% of core output.
- Reactor mass scales superlinearly with output, for example `mass = base + k * pow(totalPower, 1.25)`.
- SuperCapacitor capacity scales with part size and mass.
- Consumers first draw from matching capacitors, then universal capacitors, then reactor remainder.
- If a consumer receives partial energy, apply nonlinear degradation:
  - Nano armor repair: `effect = ratio^2` fits the策划案 example where 50% power gives about 25% effect.
  - Shield interception: reduce projectile speed by `ratio`, fully stop only above threshold.
  - Weapon firing: delay reload/charge, reduce beam duration, or refuse launch below threshold.

## Defensive Interactions

**Shield vs kinetic**

- Eligible: railgun slugs, large shells, missiles.
- Ineligible: laser beams.
- Cost should scale with kinetic threat, roughly `mass * speed^2`, with tuning caps.
- Fast objects are more expensive but more strongly affected by the electromagnetic field; slow heavy missiles can slip through if not physically destroyed.

**Nano armor vs laser**

- Armor integrity starts at 1.0 per hull block or per ship section.
- Laser damage multiplier is `0.05 + damageFactorFromIntegrityLoss`.
- Physical impacts reduce local integrity.
- Repair consumes armor energy and capacitor peak power.

**Point defense**

- DefenseDirector should prioritize by time-to-impact, then threat class, then current assignment count.
- CIWS is best for close, small, fast threats.
- Point-defense missiles are best for medium-range missiles/shells.
- Medium auto guns are best for heavy slow missiles and ships.

## Implementation Phases

**Phase 1: Skeleton and registration**

- Create `BeyondSpiderAssembly` with `Mod`, `CustomBlockController`, `ShipRegistry`, `EnergyGrid`, `TrackRegistry`, `WeaponRegistry`.
- Add placeholder XML blocks for ShipCore, SuperCapacitor, RadarPanel, DefenseDirector, ShieldProjector, CiwsBlock, SpaceGunner, LaserBarrel, RailgunBarrel, PointDefenseMissile, HeavyNuclearMissile, HeavyAntimatterMissile.
- Wire `Mod.xml` assemblies, block entries, and minimal resources.

**Phase 2: Ship identity and energy**

- Implement ShipCore, SuperCapacitor, and EnergyGrid.
- Show debug HUD values for total power, power share ratios, capacitor charge, and bus draw.
- Apply reactor/capacitor mass changes.

**Phase 3: sensors and tracks**

- Implement TrackRegistry and RadarPanel.
- Register ShipCore as ship target.
- Register placeholder heavy missile and projectile targets.
- Add simple radar UI listing tracks first; 3D radar view can come after data is stable.

**Phase 4: fire control**

- Port WW2 `AAController` firing solution math into DefenseDirector.
- Port WW2 `Gunner` to SpaceGunner.
- Port small/large `AABlock` behavior into CiwsBlock and MediumAutoGunBlock.

**Phase 5: weapons and defenses**

- Port MAC missile lifecycle for point-defense and heavy missiles.
- Implement ShieldProjector collision/intersection handling.
- Implement LaserBarrel and RailgunBarrel.
- Implement DamageRouter and NanoArmor behavior on wood blocks.

**Phase 6: polish and balance**

- Replace placeholder visuals with reused/modded assets.
- Tune energy costs, shield slowdown, armor repair, laser reflection, and missile survivability.
- Add multiplayer sync only for authoritative state and visible effects, not every local calculation.

## Validation Slices

Two validation slices exist for this framework. Build whichever is marked current; treat the other as a regression target, not the active focus. See `docs/adr/0002-prioritize-energy-firepower-armor-chain.md` for why the priority switched.

**Current priority: Energy-Firepower-Armor slice**

Build ShipCore, EnergyGrid, SuperCapacitor, one weapon (`RailgunBarrelBlock` or `SpaceFlakTurretBlock`), and nano armor on a target hull section. This validates total power and power-share ratios across buses, weapon energy draw and degrade-under-low-power behavior, and armor integrity loss/repair against incoming fire. `RailgunBarrelBlock`/`SpaceFlakTurretBlock` already draw from the Weapon bus, and nano armor/`DamageRouter` are now implemented (`NanoArmorBlock.cs`, `DamageRouter.cs`) — the energy-firepower-armor loop is closed end to end. Radar/defense systems are not required for this slice.

**Secondary priority: Missile-Defense slice**

Build ShipCore, EnergyGrid, RadarPanel, DefenseDirector, CIWS, and one incoming heavy missile. This validates energy spikes, radar tracks, shield interception, and point defense. This was the original first playable slice and already has a working skeleton (RadarPanel, DefenseDirector, ShieldProjector, CiwsBlock, SpaceInterceptorLauncher, HeavyNuclearMissile). Offensive railguns/lasers can eventually reuse the same target, energy, and damage pathways once the Energy-Firepower-Armor slice is validated.
