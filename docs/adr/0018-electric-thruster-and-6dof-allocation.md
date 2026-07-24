# Electric thruster block + captain 6-DOF control allocation

**Status**: implemented, builds clean (`dotnet build`, 0 errors); pending in-game playtest. Derived from
a 12-question design interview; every decision below was chosen by the user against stated
alternatives, which are recorded at the end.

New files: `ThrusterBlock.cs`, `ThrustAllocator.cs` (allocator + `ShipThrustControl`), `ThrusterFx.cs`,
`ThrusterNet.cs`, `BeyondSpider/Thruster.xml` (block ID 15). Touched: `SpaceCombatCore.cs` (bus, grid,
`ShipState`, runtime tick), `SpaceShipCore.cs`, `SuperCapacitorBlock.cs`, `SpaceCaptainBlock.cs`,
`ShipPartition.cs` (`ShipState.Blocks`), `SubsystemDetonation.cs`, `SpaceBalance.cs`,
`BeyondSpiderInfoPanel.cs`, `Mod.cs`, `Mod.xml`.

**Where the implementation departs from the design above**, all deliberate:

- **Columns are rebuilt every tick**, not cached behind the "partition rebuild / block loss / COM
  change" invalidation triggers §6 describes. It is one cross product and a few multiplies per
  thruster; doing it unconditionally means the live center of mass, a detonated nozzle and a shifted
  hull are all picked up correctly with no invalidation logic to get wrong. Only the deterministic
  Guid sort is gated behind a dirty flag, because that is what the replicated slot indices depend on.
- **Thrust's low discharge priority** is implemented as a reserve rather than a reordered charge
  cascade: Thrust may only borrow from the Universal pool above 25% (`EnergyGrid.UniversalFloorFor`).
  Each bus still charges from its own share, so the four sliders keep their obvious meaning.
- **The output batch** carries 16 thrusters per message (four outputs packed per `DataType.Integer`,
  8 fields total) at 10 Hz, skipped entirely while the ship is idle and the clients already know it.
- **Per-axis reachable authority is NOT surfaced yet.** §6 argues a player should learn "this ship has
  no lateral thrust" at the build stage rather than in flight; the allocator has the numbers but
  nothing displays them. Outstanding.
- Rotation is a proportional **rate loop**, not a full PD — releasing the key commands zero body rate,
  which is what produces the de-spin. A derivative term can be added if it hunts in playtest.

**Ask (user).** A new Thruster block whose max thrust follows its own size (shown in `MInfo`, with an
extra slider for a 0.1–1 output ratio); it draws from the ship-wide power grid, and the capacitor gains
a new "thrust" power option to feed it; particle + shader FX for a vacuum exhaust plume.

---

## 1. Power bus

`EnergyBus.Thrust = 4`, appended at the end of the existing enum; `Universal` stays at `3` so no
persisted machine re-maps. The ship core gains a **4th share slider**. The MP grid message widens from
6 to 7 fields.

The capacitor's new power option is a **mutually exclusive single-select** (a capacitor serves either
weapons/universal or thrust, never a split) — the capacitor's gameplay value is "which line do I lose
when this gets shot", and proportional mixing dilutes exactly that risk.

Thrust has the **lowest discharge priority**: when reactor output is short, weapons and shields are
served first and thrust starves first. "You can't maneuver while the guns are firing" is a dramatic
normal state, not an error.

## 2. Thrust from size — nozzle cross-section, not volume

```
MaxThrust = ThrustPerArea × (extent along right) × (extent along up)     // ⊥ to the thrust axis
mass      = 0.3 + MaxThrust / K                                          // linear
```

Cross-section, not volume (which is what the reactor and capacitor use), because the geometric quantity
has to feed **three** things at once: thrust ceiling, plume radius, and (see §7) efficiency. Volume gives
no plume radius, and lets a player stretch a long thin needle for free thrust. Length deliberately
contributes nothing, and no weak length term was added — one number, one sentence.

Mass is deliberately **linear**, not the reactor's 1.25-power penalty: stacking a size penalty on a
thruster is counterproductive (bigger engines would push their own weight worse).

**No scale clamp** — the user explicitly declined a min/max scale limit.

## 3. Force application — the block's own Rigidbody

Nozzle faces `+forward`, exhaust exits `+forward`, so:

```
Body.AddForce(-transform.forward * thrust)     // in FixedUpdate
```

Matches every existing force site in the mod (`RailgunBarrelBlock.cs:254` recoil,
`SubsystemDetonation.cs:146` blast). Torque is therefore **free and physically correct** — a misplaced
thruster spins the ship, so layout is a real design problem, consistent with the whole mod's "physics is
honest, the player owns the design" line. `Thruster.obj` is confirmed built with the nozzle on `+forward`,
so no load-time rotation fixup is needed.

Joint stress from thrust is to be solved by **tuning `ThrustPerArea`**, not by softening the physics.
ADR-0017 made mod blocks' own joints indestructible, so the thruster's own attachment holds; the wood/
vanilla structure it's bolted to does not, and players must learn to mount engines on solid structure.

## 4. Control architecture — per-block advanced toggle

Each thruster carries an **advanced-control toggle**:

- unchecked → local `MKey`, hold to thrust at full commanded output. No captain needed.
- checked → the block enrolls in the captain's allocator and ignores its own key.

The captain gains **12 driving keys** (3 translation + 3 rotation, two directions each) plus an
attitude-stabilizer toggle and a thrust-bus emergency cutoff — 14 bindings, all on the captain block
(precedent: the gunner already carries 5 keys including 4 directional ones,
`RailgunBarrelBlock.cs:327`). Captain-frame axis mapping, per the user's spec:

| player axis | captain axis |
|---|---|
| x — left/right | `transform.right` |
| y — up/down | `transform.forward` |
| z — fore/aft | `transform.up` |

Consequence to surface in the InfoPanel: **a ship with no captain can only use local-key thrusters**, so
a player who ticks "advanced" on a captainless ship isn't left wondering why nothing happens.

## 5. Control law — rotation closed-loop, translation open-loop

- **Rotation**: keys command a *desired angular velocity*; the captain runs a PD against the ship's
  current angular velocity. Releasing the key commands zero → **automatic de-spin**.
- **Translation**: keys command *force* directly. Releasing the key keeps the velocity → inertial coast.

The asymmetry is deliberate and matches the psychology, not the physics: drifting is romantic, tumbling
is nauseating. It costs nothing to implement, because both paths feed the same 6-vector wrench — only the
authorship of the three rotation components differs.

The **attitude-stabilizer toggle** on the captain disables the PD (full open-loop) for players who want
to fly manually, and for the case where de-spin isn't worth the power.

De-spin burns grid power **even with no key pressed** (correcting after a hit). That power is on
`EnergyBus.Thrust` like everything else and degrades gracefully when short — "the grid can't keep you
steady" is a textured failure mode.

## 6. Allocator — regularized normal equations with clamp-and-resolve

Each enrolled thruster contributes a fixed column in ship frame:

```
f_i   = -forward_i                      // unit force direction
τ_i   = (p_i - COM) × f_i               // lever arm
col_i = [f_i ; τ_i] × MaxThrust_i × Slider_i
B     = [col_0 … col_{N-1}]             // 6 × N
```

Solve `B·u = w` for `u ∈ [0,1]^N` (thrusters push only — that one-sided constraint is the source of all
the difficulty):

1. `u = Bᵀ (B Bᵀ + λI)⁻¹ w` — `B Bᵀ` is 6×6, hand-rolled Gaussian elimination, no matrix library.
2. Clamp componentwise to `[0,1]`; move clamped columns to a fixed set; re-solve the residual against the
   remaining columns.
3. **2–3 rounds** total.

Per-frame cost is `O(36N)` ≈ a few hundred multiplies at N=20, negligible at 50 Hz. `B` is rebuilt only
on **partition rebuild / block loss / COM change**.

The `λI` term is the load-bearing part: players *will* build thrust-degenerate ships (all nozzles
parallel → `B Bᵀ` singular). With regularization, unreachable directions simply get zero output and
least-squares returns the closest achievable wrench — **maneuvers you didn't build for just don't
happen, instead of NaN-ing the ship into orbit**. That graceful degradation is free.

**Torque rows are weighted ×3**: when saturated, attitude wins over translation. Flying crooked is
recoverable; not being able to turn is not. Power shortfall degrades the same way — translation is cut
first.

The reachable authority per axis should be surfaced in the InfoPanel / captain panel, so a player learns
"this ship has no lateral thrust" at the build stage rather than in flight.

## 7. Energy — power scales as thrust² / area

**No propellant.** Pure electric: grid power is the only resource, there is no second fuel tank to run
dry, no resupply, nothing to shoot a hole in.

```
P ∝ F² / Area                           // physically P = F²/(2·ṁ), with ṁ ∝ nozzle area
```

Because §2 already ties `MaxThrust ∝ Area`, **at full throttle power collapses to ∝ Area** — coincident
with a plain linear model, so balance stays easy to reason about — while **partial throttle is
quadratically cheap** (half thrust for a quarter of the power). Two consequences, both wanted: cruising
is very cheap and hard burns are very expensive, which is the correct space-flight feel; and one big
nozzle is no longer equivalent to four small ones — the big one is more efficient at equal thrust, paying
for it in volume and CG balance. This is the third job done by the single cross-section number, and the
main reason §2 chose area over volume.

## 8. Plume FX — mesh cone + sparse particles + throat light

**This mod cannot ship shaders.** Unity has no runtime shader compilation here and the repo has no Unity
editor project to build shader AssetBundles (`ShieldHexFx.cs:13`; the shipped `.ab` files are salvaged
from other mods). Every effect in the mod is `Shader.Find("Particles/Additive")`. So "shader" here means
**doing the shader's job in C#, rewriting vertex colors and UVs each frame** — exactly the shield ripple's
technique (`ShieldHexFx.cs:241`, `mesh.colors32` repainted per frame).

- **Body**: a procedurally generated bell/cone mesh (narrow throat, wide vacuum-plume flare) on
  `Particles/Additive`. Per-frame vertex colors run white-hot → blue → violet → transparent along the
  axis; throttle drives length, brightness and flare angle. Fully continuous from 0 to 1, fixed cost.
- **Edge**: a sparse particle layer for diffuse glow and flicker.
- **Throat**: a point `Light` scaled by throttle, lighting the surrounding hull — same cheap trick as the
  muzzle flash (`SpaceBallistics.cs:146` `CannonLight`).

Plume radius comes from §2's cross-section; brightness comes from the allocator's `u_i`. This yields a
free and valuable property: **during a maneuver every nozzle burns at a visibly different brightness**,
so the ship's exhaust directly renders what the allocator is thinking.

**Textures**: the axial gradient sheet (128×512, throat-white → blue → violet, soft radial falloff) will
be **procedurally generated for now**, behind a `ModResource` swap point so a hand-drawn replacement can
be dropped in without code changes. The seamless turbulence tile (256², scrolling UV) and the soft
particle blob (64² radial falloff) are procedural too.

## 9. MP (ADR-0015 convention)

Two flows, opposite directions:

- **`ThrusterCmdMsg`, client → host**: `shipId + 6 sbyte (3 translation, 3 rotation, quantized ±127) + 1
  bit stabilizer toggle`, **sent only on change** (a coasting ship sends nothing). The allocator runs on
  the host only; a client's captain never solves.
- **`u_i` broadcast, host → client**: one byte per thruster (`u_i ∈ [0,1]` quantized). A 20-thruster ship
  is **20 bytes**, ~200 B/s at 10 Hz — nothing next to the 7 existing messages. This makes *flame
  brightness = actual thrust* a cross-endpoint invariant, so brownout degradation, saturation clamping and
  every attitude micro-correction reproduce exactly on clients, and the plume becomes a debugging
  instrument for the allocator.

**The single most dangerous line in this change**: the client's `AddForce` must be fenced by
`NetAuthority`. §3 applies force to the block's own Rigidbody, so an unfenced client would double-apply
force and fight the host's position corrections, shaking the ship apart.

## 10. Damage

Destroyed thrusters **fail silently** — `B` is rebuilt next frame and the allocator automatically
rebalances across the survivors (lose the starboard bank and the ship keeps flying with a reduced
maneuver envelope). This is free from §6's least-squares.

Thrusters also **detonate** via `SubsystemDetonation` (ADR-0017) on direct hit or unshielded proximity
blast, with blast scale driven by `MaxThrust`. A thruster is a high-power electrical component like the
capacitor, and unlike the reactor it *must* be mounted on the hull exterior — so "the efficient big
nozzle is also a big bomb you can't bury" is a real design tension, free from the path ADR-0017 laid.

Detonation changes COM and the surviving block set, which is exactly §6's `B`-rebuild trigger. The loop
closes: **blow up an engine and the ship re-trims itself.**

## 11. Block UI

`MInfo`, four lines: `Max thrust xx kN` / `Full-throttle draw xx MW` / `Current output xx%` (the
replicated `u_i`) / `Nozzle area xx m²`.

Controls: output-ratio slider (0.1–1) and the advanced-control toggle. Nothing else. Under §4 the slider
means "how much force this thruster may contribute when responding to fleet commands" — a manual trim for
ships whose CG is off-center.

---

## Considered options

- **Thrust ∝ volume** (matching reactor/capacitor precedent) — rejected: gives no plume radius and no
  efficiency curve, and lets a long thin needle farm thrust. A weak length term on top of area was also
  considered and dropped to keep the rule to one sentence.
- **Distribute thrust across every rigidbody by mass fraction** — rejected: joints would never strain, but
  torque disappears entirely, so thruster placement stops mattering and layout dies as a design axis.
- **Apply force to own body but damp the resulting torque by a coefficient** — rejected: an
  unjustifiable magic number the player can't see or reason about.
- **Per-block throttle keys** — rejected: a 12-engine ship needs 12 keybinds and players would bind them
  all to one key anyway.
- **A single shared scalar throttle on the ship** — rejected: reverse thrusters would fire simultaneously
  with forward ones and cancel out; no directional maneuvering at all.
- **Pure open-loop force command on all six axes** — rejected: with no drag, a tap on yaw spins the ship
  forever and the player must manually null it. Pure closed-loop rate command on all six was likewise
  rejected: auto-braking translation kills inertial coasting.
- **Greedy per-thruster dot product against the desired wrench** — rejected: wrong on asymmetric ships
  (pure rotation leaks translation and vice versa), cannot express the differential pairs needed for pure
  torque, and the §5 PD would fight the coupling error and visibly judder.
- **Full NNLS active-set solve** — rejected: optimal, but with unbounded iteration count and double the
  code, and on the badly-laid-out ships that actually need help its answer is no better than three
  clamp-and-resolve rounds.
- **Propellant/working-mass tank as a second resource** — rejected (user did not ask): would grow an
  entire storage/resupply/get-shot-and-leak subsystem.
- **Proportional capacitor allocation across buses** — rejected: dilutes the "which line do I lose"
  stake that makes capacitor placement interesting.
- **Constant-power thrusters (thrust floats with available power)** — rejected: no throttle feel, and it
  fights the §5 PD as thrust jitters with other consumers' draw.
- **Pure particle plume** — rejected: discrete particle counts make low throttle look granular, and the
  wide vacuum bell is hard to hold in shape — it reads as smoke, not plasma.
- **Client-side re-solve of the allocator for FX** (7 bytes instead of 20) — rejected: requires the client
  to hold a bit-identical `B` (same COM, same surviving blocks, same sliders); one desynced frame points
  every flame the wrong way, **silently**. Not worth a three-digit B/s saving.
- **Stuck-at-full-thrust damage failure** — initially chosen by the user, then reverted to plain
  failure+detonation. Rejected because the §5 stabilizer PD would fight an unmodeled constant disturbance,
  burning the grid while the ship slowly tumbles, with no in-flight way for the player to identify or shut
  off the runaway nozzle (Besiege doesn't allow changing block settings mid-simulation). A survivable form
  was proposed — treat the stuck wrench as a *known* disturbance and solve `B·u = w_desired − w_stuck`,
  flag the nozzle in red, and add a thrust-bus emergency cutoff as the player's out — but the simpler
  no-failure design was chosen instead.
- **A separate Helm block carrying the driving keys** — rejected: would split combat and piloting into two
  physically occupiable stations (a natural fit for the ADR-0015 host-authoritative MP model, letting two
  players divide the roles), but the user chose to keep everything on the captain.
- **Scale clamp on block size** — rejected by the user; no min/max is enforced.
- **Rotation authority left out of the allocator** (translation only, players hand-place off-axis
  thrusters for attitude) — superseded: the user escalated to a full 6-DOF solve.
