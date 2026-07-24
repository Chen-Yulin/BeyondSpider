# High-precision per-cluster ship position replication (mod-side)

**Status**: **proposed — design only, not implemented.** This ADR is the plan; no code has been written
for it yet. It supersedes, for ship hulls, the multiplayer stopgap in ADR-0019 (widening the engine's
position-compression box); that stopgap stays for anything still on the engine's stream (loose
projectiles) and as the fallback until this lands.

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

## 5. Client apply

Client receives the batch, resolves each record's block by GuidHash, and drives that base's transform:

- Set `baseTransform.position` = streamed full-float position; `baseTransform.rotation` = decompressed
  rotation. Children follow via the engine's existing `transformMatrix` derivation.
- Because we stopped sending on the host, the client's `NetworkInterpolation` for that base gets no new
  data and goes inactive on its own; if it still fights, mark it stopped / set the client base's
  `pollTransform` handling to defer to us. Apply with the mod's own smoothing (ADR-0015 already has
  client display smoothing to model on).

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

## 9. Scope / status

Not implemented. When built it should be its own component (e.g. `ShipPoseNet` + a per-ship sender/
applier), registered in `Mod.cs` beside the other `*Net` singletons, gated on `BoundaryOff`, with the §6
rules as the acceptance checklist. Test on a double-client session: fly a ship tens of km out and confirm
its remote position stays smooth and accurate, that suppressing `pollTransform` left no block frozen, and
that breaking the ship apart mid-flight re-keys cleanly with no frozen debris.
