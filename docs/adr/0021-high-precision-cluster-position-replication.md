# High-precision per-cluster ship position replication (mod-side)

**Status**: **implemented** (`ShipPoseNet.cs`), builds clean (`dotnet build`, 0 errors); **pending
double-client playtest** — the reflection-based client apply (§5) and cluster-topology handling (§6)
cannot be verified without a live two-peer session. It supersedes, for ship hulls, the multiplayer
stopgap in ADR-0019 (widening the engine's position-compression box); that stopgap stays for anything
still on the engine's stream (loose projectiles) and as the fallback.

**Ask (user).** After removing the boundary, MP ships flying far out replicate imprecisely. Send our own
full-precision ship pose instead, reuse the engine's cluster system so we don't send one packet per
block, and disable the engine's own 6-byte transform stream for those blocks so nothing is sent twice.

---

## 1. Problem

Besiege replicates a simulating machine by streaming, per rigid **cluster base block**, a position packed
into `NetworkCompression.CompressPosition` — **17 bits X/Z, 14 bits Y (log-scaled), relative to
`NetworkCompression.worldBounds`**, with no clamp (see ADR-0019). Two consequences once the air wall is
gone and ships fly far:

- **Precision** falls off with the box size. ADR-0019 widens the box to 20 km so far hulls don't *wrap*,
  but that fixes wrapping at the cost of ~0.15 m X/Z resolution everywhere, and the **14-bit log Y** is
  coarse (metres) at altitude.
- The packet is a **hard 48-bit / 6-byte engine format**. `POS_SIZE = 6` is a compile-time const inlined
  into every caller; there is **no Harmony** in this install. So the wire format cannot be widened to,
  e.g., 20 bits/axis — 20+20+20 = 60 bits doesn't fit 6 bytes, and the buffer sizing is baked into the
  base-game serialisation. Adding bits is off the table.

The only way to beat the engine's position precision is to replicate ship position **ourselves**, at full
float, outside the engine's fixed packet.

## 2. Decision

A host-authoritative, mod-side **per-cluster-base pose stream** (fits NetAuthority / ADR-0015: host
simulates and sends, clients display), that **reuses the engine's cluster decomposition** for granularity
and **suppresses the engine's own transform stream** for the blocks it takes over, so nothing is sent
twice.

Verified engine facts this rests on (decompiled `Assembly-CSharp`):

- **Only cluster *base* blocks stream a transform.** `NetworkBlock.IsChanged` reports pos/rot only when
  `isBaseBlock`; `GetDataSize` counts pos/rot bytes only for a base; non-base blocks carry state only.
  Child blocks are reconstructed on the client from the base:
  `trackTransform.position = baseNetworkBlock.transformMatrix.MultiplyPoint3x4(...)`. So correcting the
  base corrects the whole rigid cluster.
- **The cluster decomposition is public to the mod.** `Machine.simClusters` (`SimCluster[]`), and each
  `SimCluster` exposes `Base` / `BaseTransform` / `Blocks[]` / `count` / `intact`; `BlockBehaviour.ClusterIndex`
  is public.
- **The engine's transform stream has a public off switch.** `NetworkEntity.pollTransform` is a
  `public bool`; when false, `PollObject` sends **neither** position nor rotation for that entity. The
  engine already flips it in normal operation (sleep/deactivate), so toggling it is a supported move.
- **`NetworkCompression.CompressRotation` is public** (smallest-three quaternion, 7 bytes, bounded
  domain → high precision, no range problem) — reusable for our own rotation bytes.

## 3. Wire format

Per **intact** cluster, one record:

| field | bytes | notes |
| --- | --- | --- |
| base block GuidHash | 4 | cross-peer identity (`BuildingBlock.Guid.GetHashCode()`); do NOT use cluster array index — host/client ordering can differ |
| position | 12 | full-float `Vector3`, world space — the whole point |
| rotation | 7 | reuse `NetworkCompression.CompressRotation` (precision is already fine) |

≈ 23 B per cluster, one per rigid part (a welded hull is ~1 cluster; each hinge/turret/broken-off chunk
is its own). This is the **same granularity the engine streams** — we are not adding packets per block.
Batched: N clusters per ship per message, sent at the ship replication rate.

Bandwidth vs alternatives, per moving cluster: engine-only ≈ 13 B (6 pos + 7 rot); **send-both** ≈ 25 B
(duplication — rejected); **this (suppress + resend)** ≈ 23 B, no duplication.

## 4. Suppressing the engine stream (the "no duplicate" part)

On the **host**, for every block that is currently a cluster base of one of our ships, set its
`NetworkBlock.pollTransform = false`. That stops the engine's 6-byte pos + 7-byte rot for exactly those
blocks. It's **all-or-nothing** (no poll-position-only), which is *why* §3 also carries rotation.

Non-base blocks are left alone — they already send no transform.

## 5. Client apply — and the reflection wall (found during implementation)

Client receives the batch, resolves each record's block by GuidHash, and drives that base. But *how*
the engine reconstructs a rigid cluster on the client makes the naive "just set `baseTransform`" wrong.
Verified in `NetworkBlock.UpdateEntity` (runs every frame per block on the client):

- A **cluster base** sets `trackTransform` from its `posTracker` and caches
  `transformMatrix = myTransform.localToWorldMatrix` — but **only inside `if (posTracker.isActive)`**.
- A **child** re-derives `trackTransform.position = baseNetworkBlock.transformMatrix.MultiplyPoint3x4(posTracker.Vector)`
  **every frame**, gated on a private `posActive` flag that is set once and **never reset** — so a child
  keeps re-deriving from its base's cached matrix forever.

Consequence of suppressing the base's stream (§4): its `posTracker` goes inactive → the base neither
moves itself **nor refreshes its cached `transformMatrix`** → children keep re-deriving from a **frozen**
matrix → the whole cluster freezes on the client, even if we set `baseTransform` by hand. To drive it we
must keep the base's `transformMatrix` **current**, and:

- `NetworkBlock.transformMatrix` is **`private`**, `posTracker`/`rotTracker` are **`protected`**, and
  there is **no public setter / teleport / SetPosition** on `NetworkBlock` or `NetworkEntity` (grepped).
- `posActive` (the thing that would otherwise let us stop the engine re-deriving) is also `private`.

**So a correct, non-fighting client apply requires runtime reflection into an engine private field** —
each frame, after setting `base.transform`, write `base`'s private `transformMatrix =
base.transform.localToWorldMatrix` (cache the `FieldInfo`). Then the engine's own per-frame child
re-derivation places every child of that cluster correctly, using the **client's** cluster membership
(which the engine keeps in sync with the host via `ClusterResultData`), so we never position children
ourselves. The base's own `UpdateEntity` won't fight us — with `posTracker` inactive it touches neither
`trackTransform` nor `transformMatrix`.

The alternative to reflection is to fight the engine: re-assert base + child transforms in `LateUpdate`
every frame and hope to run after `UpdateEntity`. That's more fragile (ordering-dependent, double-positions
every child) than one cached reflected field write, so reflection is preferred.

**This is a departure worth calling out**: the mod has so far used no Harmony and no runtime reflection
into game internals. Reflection ≠ Harmony (no patching, just a field write), the `FieldInfo` is cached so
it's cheap, and the whole subsystem is gated on `BoundaryOff` — but it does couple us to a private field
name that a future Besiege version could rename. Accepted for now as the only clean path to full-precision
ships; revisit if the engine's cluster networking changes.

Apply with the mod's own smoothing (lerp `base.transform` toward the latest received pose; ADR-0015's
client display smoothing is the model).

## 6. Implementation rules — the four pitfalls, pinned

1. **Cluster bases change on damage.** When a machine loses blocks or a joint breaks, clusters
   re-partition and *different* blocks become bases (`isBaseBlock`/`isClusterBase` are re-derived). On
   every re-partition: **re-enable `pollTransform` on blocks that are no longer bases, disable it on the
   new bases**, and re-key the stream by the new base Guids. Hook the same moment the engine recomputes
   clusters (`intact` flips / `SendClusterResults` fires). Getting this wrong is asymmetric: a stale
   *enabled* base merely double-sends (wasteful, harmless); a base we suppressed but no longer drive
   **freezes on the client** (harmful) — so err toward re-enabling when unsure.
2. **Dead-band + keyframe.** The engine only sends a base past `WithinThreshold` (a stationary ship
   costs nothing). Match it: skip records whose pose hasn't moved past a threshold, so we're never worse
   than the engine on idle hulls. Add a low-rate **keyframe** (every cluster, every ~1 s) for late
   joiners and packet loss — the same reason HostControlNet heartbeats.
3. **Client interpolation is now ours.** We took over from `NetworkInterpolation`; do our own smoothing
   so remote hulls don't stutter between updates.
4. **Identity by Guid, never by index.** Host and client may order/partition clusters differently mid-
   flight; the base block's `BuildingBlock.Guid` is the stable cross-peer key.

## 7. Relationship to ADR-0019 / 0020

- Once ships are on this stream, the ADR-0019 **worldBounds widening no longer matters for ship hulls**
  (their positions no longer go through `CompressPosition`). Keep the widening anyway: **loose
  projectiles and any non-cluster networked entity still use the engine stream**, and it's the fallback
  for ships until this ships. Do not remove it.
- Trigger stays the host **无边界** toggle world (ADR-0020): this stream is only worth running when ships
  are meant to fly far, i.e. when 无边界 is on. Simplest: gate the whole subsystem on
  `SpaceBoundary.Instance.BoundaryOff`, so vanilla-range play keeps the engine's own (cheaper, adequate)
  replication untouched.

## 8. Alternatives rejected

- **Widen the engine packet to 20 bits/axis.** Impossible: fixed 48-bit/6-byte format, `POS_SIZE` const
  inlined, no Harmony. (This ADR exists because the user asked for 20-bit and it can't be done in-packet.)
- **Send our precise pose *in addition* to the engine stream.** Works, but doubles position bytes per
  cluster for no reason — §4 removes the engine half instead.
- **Widen worldBounds further (100 km+).** Still coarsens precision (0.76 m at 100 km) and never gets to
  full float; only defers the problem.

## 9. Implementation notes (`ShipPoseNet.cs`)

Built as `ShipPoseNet : SingleInstance`, registered in `Mod.cs` beside the other `*Net` singletons, with
the §6 rules as the acceptance checklist. As built:

- **Message** `PoseMsg(playerId, coreGuidHash, baseGuidHash, position:Vector3, rotationEuler:Vector3)`,
  one per cluster base (no `DataType.Quaternion` exists, so rotation goes as full-float euler — bounded,
  round-trips fine). Position is full-float `DataType.Vector3` — the whole point.
- **Host** (`Update`, 20 Hz, gated `isMP && !isClient && levelSimulating && BoundaryOff`): walks each
  ship's `machine.simClusters`, and per intact cluster (≥ `MinClusterBlocks`, capped `MaxRecordsPerShip`
  so a shattered hull can't flood) sets the base `NetworkBlock.pollTransform = false`, dead-bands
  (`PosThreshold`/`RotThresholdDeg`) with a 1 s keyframe, and sends. Bases that drop out of the live set
  (re-partition, ship gone, cap, feature off) get `pollTransform = true` back — err toward re-enabling.
- **Client** (`LateUpdate`): lerps the resolved base's transform toward the received pose and writes the
  base's private `transformMatrix` via the cached `FieldInfo`; drops a base after `StaleTimeout` with no
  update so the engine stream self-heals. Base resolved by GuidHash within `ship.Blocks`, cached.

**Acceptance (needs the double-client playtest):** fly a ship tens of km out and confirm its remote
position stays smooth and accurate; confirm suppressing `pollTransform` left no block frozen on the
client; break the ship apart mid-flight and confirm it re-keys cleanly with no frozen debris; toggle
无边界 off and confirm the engine stream resumes with no stuck blocks.

**Known limitations of the first cut:** client smoothing is a plain lerp toward the latest pose (no
velocity extrapolation — fast hulls may show minor latency between 20 Hz updates); reflection
`SetValue` boxes the `Matrix4x4` per driven base per frame (minor GC, fine for a handful of clusters);
rotation is euler, not the engine's smallest-three. All are tunable/replaceable if playtest shows need.
