# Remove the world "air wall" for space flight

**Status**: implemented, builds clean (`dotnet build`, 0 errors); pending in-game playtest.

New file: `SpaceBoundary.cs`. Touched: `Mod.cs` (registers the component on `Mod.Root`),
`BeyondSpiderAssembly.csproj` (`<Compile>` entry).

**Ask (user).** Research how `../ModernAirCombat` removes Besiege's boundary ("空气墙", the invisible
wall that clamps every machine inside a small box), and implement air-wall removal in BeyondSpider so
warships can fly freely across a space level.

---

## What the air wall actually is

Decompiled `Assembly-CSharp` (via `ilspycmd`, which sits in `~/.dotnet/tools`). Besiege's boundary is
**three** independent things — removing the first two still leaves the ground colliding, which is the
follow-up the first cut missed:

1. **Per-scene `BoundingBoxController`** — the invisible walls, present in every level.
   `Machine.boundingBoxController` (assigned in `Machine.Init` from a single
   `Object.FindObjectOfType<BoundingBoxController>()`). It owns:
   - `Collider[] colliders` — the six physical wall colliders you actually collide with.
   - The logical limits `StatMaster.Bounding.{floor,roof,front,back,left,right}Pos` and the master
     flag `StatMaster.Bounding.Enabled`.

   Its public **`DisableBounds(Machine)`** does all of it at once: `SetColliders(false)` (walls off),
   `SetFloorPos(false)` (limits pushed to ±10000), `StatMaster.Bounding.Enabled = false`. This is the
   exact call behind the game's own god-mode **"Disable Bounds"** button (`DisableBoundsButton`) and
   the level-authored **`StartWithoutBounds`** component. `EnableBounds(Machine)` is the inverse.

2. **Scene object literally named `"WORLD BOUNDARIES"`** — a *separate* box of collider walls carried
   by some levels (mainly multiplayer). This is the object `ModernAirCombat` finds and resizes, and
   the one the engine's own `DeleteWorldBounds` level component destroys.

3. **The ground / floor** — an ENTIRELY separate set of solid level colliders that `DisableBounds`
   never touches (hence "去边界之后地面碰撞还在"). Besiege itself defines "ground" as **layers 24
   (`Environment`), 28 (`BrickWall`), 29 (`Floor`)** — `MachineGround.CreateLayerMask(24, 28, 29)`,
   the mask it uses to drop a machine onto the ground. `ModernAirCombat` instead toggles a named
   `FloorBig` object per level; the layer set is more robust (works in any level, no name dependency).
   Layer indices were read straight out of the game's layer table in `globalgamemanagers`.

Two facts drove the design:

- **`StatMaster.Bounding.Enabled` also gates simulation start.** `AddPiece` refuses to enter sim while
  a machine is out of bounds *unless* `Bounding.Enabled` is false. A warship larger than the default
  box therefore can't begin a run at all until bounds are off — so removal must happen in **build
  mode** the moment a ship exists, not only during flight.
- **Simulation start/stop never re-enable bounds.** `EnableBounds` is only called from level load
  (`Machine.Init`), the god-mode button, and a couple of load helpers. So a removal done once persists
  across the whole build → simulate → stop cycle until the level reloads or someone toggles it back.

## How ModernAirCombat does it (reference), and why we don't copy it

MAC's `SkyBoxChanger` **expands** boundary 1's colliders and MAC's own `WORLD BOUNDARIES` box to
2 km / 20 km / a custom size, and sets `StatMaster.Bounding.worldExtents` +
`NetworkCompression.SetWorldBounds` to match. It only touches `WORLD BOUNDARIES` in multiplayer; its
single-player path leaves the wall alone (it relies on the player's god-mode toggle).

We take the wall **down** via the engine's own `DisableBounds` instead of pushing it far away:

- It is Besiege's documented lever for exactly this, so it survives changes to the boundary internals.
- Inflating `worldExtents` has a multiplayer cost: base-game block positions are quantised inside it by
  `NetworkCompression`, so a 20 km extent coarsens every networked hull position. `DisableBounds`
  leaves `worldExtents` alone.

We still disable `WORLD BOUNDARIES` colliders when that object is present, so removal covers both
systems.

## `SpaceBoundary`

A `MonoBehaviour` on the persistent `Mod.Root` (added in `Mod.OnLoad`, beside `SpaceCombatRuntime`).
Polls at 2 Hz, **trigger = presence of any `SpaceShipCore` in the scene**
(`Object.FindObjectOfType<SpaceShipCore>()`). Two lifetimes:

- **Invisible walls — while a ship core exists (build OR sim).** So an oversized hull can even start a
  run (the out-of-bounds gate above). Re-asserted against the live `StatMaster.Bounding.Enabled` flag
  each poll (a level reload re-enables bounds behind our back via `Machine.Init` → `EnableBounds`, and
  a player may re-arm the god-mode toggle). Guarded on that flag so we don't re-fade an already-correct
  box. Restored via `EnableBounds` once the last core is gone. A fallback path (no `Machine` for
  `DisableBounds`'s trailing `Check()`) sets the flag and toggles the box colliders directly.
- **Ground collision — only while `StatMaster.levelSimulating`.** Build mode keeps its ground because
  the machine-placement tool relies on it, and in zero-g there's nothing to fall onto, so dropping it
  at sim start is seamless. We disable only **solid (non-trigger)** colliders on the ground layers —
  gravity/water/kill volumes are triggers and must survive (same `isTrigger` = "not solid geometry"
  rule as `SpaceCombatUtil.IsUnrecognizedObstacle`) — and remember exactly the colliders we switched
  off, so restore-on-sim-stop re-enables only ours.
- **Multiplayer position-compression range — only while `StatMaster.levelSimulating`.** A networked
  block's position is packed into 17 bits (X/Z) / 14 bits (Y) *relative to* `NetworkCompression`'s
  `worldBounds` — the level's small boundary box (`NetworkAddPiece.CalculateWorldBoundaries`), set from
  the `WORLD BOUNDARIES` object — with **no clamp**. Confirmed the ship hull rides this: every
  simulating block is a `NetworkBlock : NetworkEntity`, and `NetworkEntity.CompressPosition` streams
  `posTracker.lastVec` (absolute world position) through it. So once the walls are gone, a hull flying
  past that box **wraps to a garbage position on remote clients** (a coordinate below the box min casts
  negative → huge `uint`, corrupting too). `UpdateNetworkBounds` widens the compression box to
  `MpNetworkWorldSize` (20 km) during MP flight and restores the level's real box afterwards. It's
  **deterministic across peers** — same level-derived centre (read back from the public
  `NetworkCompression.wMin*/wMax*`, identical on every peer) + a fixed size — so host encode and client
  decode stay agreed (the mod is required on all peers, so all of them run it). Idempotent and
  re-widens after a level reload resets the box. Trade-off: coarser fixed-point resolution while widened
  (20 km / 2¹⁷ ≈ 0.15 m on X/Z), which is why it's flight-only, not always-on. `StatMaster.Bounding`'s
  own `worldExtents` is left alone — it feeds the build-zone out-of-bounds test we already disabled, not
  the packet compression. **Single-player has no networking, so none of this runs there.**

## Deliberate limitations / departures

- **Presence is polled with `FindObjectOfType`, not a maintained core registry.** Self-correcting and
  needs no `OnDestroy` bookkeeping on `SpaceShipCore`; the 2 Hz scan is negligible next to the combat
  ticks. `SpaceCombatRegistry` was not reused because it only tracks *simulating* ships, and we need
  the build-mode signal too.
- **Global while a ship exists.** The wall is a scene-wide singleton, so removal affects the whole
  level (including any non-ship machines in it), not just the BeyondSpider hull. Acceptable for a
  space-combat conversion where the wall is never wanted.
