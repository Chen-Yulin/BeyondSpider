# Indestructible connection joints + reactor/capacitor secondary detonation

**Ask (user, 毁伤优化 spec).** Four requests: (1) every mod block's own connection point should be
unbreakable/rigid, referencing WW2-Naval `Gun.cs`'s cannon barrel (`blockJoint.breakForce/breakTorque
= float.PositiveInfinity` in `OnSimulateStart`); (2) a particle-effect capacitor explosion emphasizing
lightning and scattering ions; (3) a particle-effect reactor explosion, like the missile blast but
bigger and with lightning; (4) cannon shells and missiles should be able to directly detonate reactors
and capacitors, via a direct hit, or — for missiles only — landing within blast radius with clear line
of sight (no armor blocking the straight line from blast to subsystem center), referencing
WW2-Naval `Bomb.cs`'s armor-in-the-way check.

**Indestructible joints (`SpaceBlock.OnSimulateStart`).** Every `SpaceBlock` subclass already calls
`base.OnSimulateStart()` first (railgun barrel, laser lance, missile launcher, radar, shield
projector, captain seat, flak turret, gunner seat, ship core, capacitor — verified by grep), so adding
the WW2-Naval barrel's exact pattern to the shared base covers the whole mod block roster in one
place: `BlockBehaviour.blockJoint.breakForce/breakTorque = float.PositiveInfinity`. Deliberately
untouched: `NanoArmorBehaviour` (attached to vanilla wood/log blocks, not a `SpaceBlock`) keeps its
own pre-existing progressive joint-weakening pipeline (`ApplyStructuralWeakening`, ADR-0007) — that's
a designed damage-feedback mechanic, not something the new blanket rule should override.

**Detonation counterpart (`SpaceBlock.BreakOwnConnectionJoints`).** A subsystem that SHOULD still blow
off under a direct hit needs to defeat its own new indestructible default. Rather than destroying the
block's `GameObject` outright (risky — Besiege's block/joint bookkeeping isn't designed for a placed
block vanishing outside its own deletion path), `SpaceShipCore.Detonate()` /
`SuperCapacitorBlock.Detonate()` mirror `NanoArmorBehaviour`'s breach mechanic exactly: zero the
block's own `iJointTo`/`jointsToMe` joints, then let an explosion force (`SubsystemDetonation.
ApplySecondaryBlast`) tear it free next physics step. The block survives as drifting debris, same as
breached armor — it just stops counting. Both blocks gained `bool detonated` + `IsAlive = !detonated
&& BlockBehaviour.isSimulating`; `SpaceCombatRuntime`'s reactor sum already gated on `core.IsAlive`
(pre-existing), the capacitor sum did not and now does too, so a detonated reactor/capacitor drops out
of the ship's power grid immediately with zero registry bookkeeping (no `Cores`/`Capacitors` list
mutation — `UnregisterCore` was found to remove the *whole* `ShipState` on any core loss, which would
be wildly wrong to trigger from a non-primary reactor's secondary explosion, so `Detonate()` never
calls it).

**Trigger logic (`SubsystemDetonation.cs`, new file).** Two entry points:

- `TryDirectHit(Collider)` — the struck collider's parent chain has a live `SpaceShipCore` or
  `SuperCapacitorBlock`; detonates it and returns true. Wired into `SpaceKineticRound`'s host penetration
  march (before the "plain block passes through" fallback — previously a shell hitting a reactor/
  capacitor collider had **no** interaction at all, since neither type implements `IKineticTarget`)
  and into `MissileProjectile.OnTriggerEnter`/`CheckSweepHit` (contact and sweep fuzes alike, called
  before the normal `Detonate(...)` so the missile's own warhead detonation and the subsystem's
  secondary explosion both fire).
- `TryProximityBlast(origin, blastRadius)` — walks every ship's `Cores`/`Capacitors`, detonating each
  one within `blastRadius` whose straight line to `origin` isn't blocked by intact (non-breached) nano
  armor (`HasLineOfSight`, binary — the user's spec says "no armor blocking", not a thickness budget
  like WW2-Naval `Bomb.ExploDestroy`'s sum, so the check was deliberately simplified rather than
  ported verbatim). Wired into `MissileProjectile.Detonate(...)` alongside the existing
  `ApplyBlastDamageToArmor`. This also covers the "direct hit" case for free — a contact-fuze
  detonation sits at ~0 distance from whatever it just touched, trivially within radius with clear
  LOS — so missiles don't need a second explicit direct-hit path beyond the shared `TryDirectHit`
  calls already listed above. Cannon shells have no blast radius (ADR-0007, pure kinetic penetrators)
  so only `TryDirectHit` applies to them, matching the user's spec (proximity+LOS is explicitly
  "导弹的爆炸范围内" — missiles' blast radius only).

Both entry points are safe to call redundantly: `Detonate()` gates on its own `detonated` flag, so a
missile's `TryDirectHit` immediately followed by its own `Detonate → TryProximityBlast` is a harmless
double-call.

**Secondary blast damage/force (`SubsystemDetonation.ApplySecondaryBlast`).** Same shape as
`MissileProjectile.ApplyBlastDamageToArmor`/`ApplyBlastForce` (linear damage falloff to every armor
plate in range, `OverlapSphere` force falloff floored at `SpaceBalance.BlastForceFalloffFloor`), driven
by new `SpaceBalance` constants scaled cube-root off the driving quantity (reactor: `totalPower`;
capacitor: `Capacity`) — the same shape `MissileBlastRadius` uses off warhead charge. Coefficients
picked so even a modest single-block reactor (~1200 MW) already outranges and outdamages the heaviest
missile (200-charge, ~23 m / ~3200 dmg) — the user's "更大规模". Capacitor coefficients sit below
that: a stored-energy pop, not a fireball. **Placeholder scale, pending in-game tuning** — same status
every other blast constant in `SpaceBalance` already carries.

**FX (`SpaceEffectAssets.PlayReactorExplosion`/`PlayCapacitorExplosion`, `SpaceBallistics.cs`).** Both
reuse `PlayMissileBlast`'s vacuum-physics blast-layer recipe (`CreateBlastLayer`: local-space,
gravity-free, momentum-conserving under `MissileBlastDrift`) — flash / gas / spark layers, scaled off
`totalPower`/`Capacity` instead of warhead charge — plus a new shared `SpawnLightningBurst` (the user's
explicit lightning ask, absent from the plain missile blast). Reactor: missile-blast shape but bigger
and slower-fading, orange-white gas with a violet tint, a moderate 16-bolt corona. Capacitor:
deliberately NOT a scaled-down fireball — a small hot flash, a dense fast-scattering electric-blue
"ion spark" layer (the user's "四散的离子"), and the densest/brightest/shortest-lived lightning corona
of the two (a capacitor discharges instantly, it doesn't keep burning). `SpawnLightningBurst` spawns
`DetonationLightningArc` components — a one-shot jagged `LineRenderer` per bolt, built once from the
blast center to a random point at ~radius and re-jittered every frame for a crackle (same jitter idea
as `LaserFx.RebuildBolt`'s continuously re-aimed beam bolts, but self-contained and short-lived rather
than tracking a moving beam), fading via a linear alpha/width ramp and self-destructing after its own
random `Lifetime`.

**MP (`SubsystemDetonationNet`, ADR-0015 convention).** The real damage/joint-break is host-only
(`NetAuthority.IsAuthority` fence inside both `Detonate()` methods) and reaches clients through
vanilla machine sync — the block physically tearing free — exactly like an armor breach already does.
`SubsystemDetonationNet.DetonateMsg` carries only `(position, driftVelocity, scale, kind)` so a client
can play the matching FX at the right place; no block identity/lookup needed, unlike
`MissileLauncherNet`'s per-missile mirror stream, since the FX is 100% cosmetic and self-contained.

**Status**: implemented, builds clean (`dotnet build`, 0 errors); pending in-game playtest (verify: a
shell/missile hitting a reactor/capacitor collider directly detonates it even through an otherwise-solid
hull; a missile blasting near an *unarmored* reactor detonates it, one shielded by intact armor does
not; a detonated reactor visibly tears free and the ship's whole power grid drops; ordinary blocks no
longer shake loose under recoil/collision that used to snap their joints).

**Considered options**

- *Destroy the reactor/capacitor `GameObject` outright on detonation* — rejected: Besiege's own
  block/joint machinery isn't designed for a placed block disappearing outside its normal deletion
  path; the existing `NanoArmorBehaviour` breach precedent (weaken joints + explosion force, leave the
  object as debris) already solves "this subsystem is functionally gone" without that risk.
- *Thickness-budget line-of-sight (port WW2-Naval `Bomb.ExploDestroy` verbatim, summing armor
  thickness along the ray)* — rejected: the user's literal spec is "no armor blocking", a binary
  condition; a thickness sum is a different, stricter design the user didn't ask for.
- *Have `TryDirectHit` also drive missiles' `Detonate(ITrackable, ...)` overload directly* — rejected:
  the proximity+LOS check already resolves a true contact hit (distance ≈ 0, trivial LOS) for free, so
  a second explicit code path would only duplicate the outcome, not add coverage.
