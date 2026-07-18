# Channel 0 becomes a multi-target threat list with per-weapon lottery assignment

Channel 0 (the air-defence channel, ADR-0010/0012) used to hold ONE auto-selected lock, so the ship's entire point defense converged on a single incoming threat while its wingmen flew in untouched. It now holds a rank-ordered **threat list** of up to 4 targets (`ShipState.Channel0Threats`, `FireChannels.Channel0MaxTargets`), and every channel-0 subscriber picks its own target from that list — so a salvo of incoming missiles gets met by distributed fire.

**Threat list construction** (host, seated captain, every tick — `SpaceCaptainBlock.UpdateChannel0Threats`):

- **Candidates are ammunition only** (通道0只锁弹药, user decision): missiles and bare cannon shells; `TrackKind.Ship` never auto-enters. A ship can still sit in the list as the player's manual pin.
- Gates unchanged from before: sensor coverage (`ship.Tracks`), the IFF switch, channel 0's 雷达筛选 row (ADR-0013), and the hull-box impact prediction (`PredictHullImpact` — straight-line extrapolation must enter the hull OBB within 5 s).
- **威胁度 = sqrt(预估伤害) / (预计命中时间 + 0.5 s)** (user-confirmed formula). The square root keeps one huge warhead from permanently drowning out a swarm of small closers; the +0.5 s softening keeps an already-on-top contact finite. Estimated damage: a missile promises warhead blast + direct-hit bonus (`MissileProjectile.EstimatedThreatDamage`); a kinetic shell promises its relative-speed KE budget (`MassEstimate·v_rel²·KineticDamagePerKE`, the same number the ADR-0007 penetration model charges); unrecognized kinds are priced as a token light kinetic body.
- Top 4 by score enter the list. A **manual channel-0 lock is pinned at rank 0** (手动目标置顶) and the remaining slots keep auto-filling around it; clicking the pin again releases it to full-auto. `ChannelTargets[0]` always mirrors rank 0 for the code paths that still want "the" channel-0 target.

**Per-weapon assignment** (`FireChannelAssignment`, one instance per flak turret, gunner, missile launcher, AND per in-flight missile): when a subscriber needs a channel-0 target it draws from the list with the rank-weighted lottery (user-specified):

```
N = list size, rank n = 0..N-1, remaining = 1
p_n = remaining × (n == N-1 ? 1 : 0.5)      →  [1] / [.5 .5] / [.5 .25 .25] / [.5 .25 .125 .125]
```

The draw is **sticky — 目标失效才重抽** (user decision): a weapon keeps its target until it dies or drops off the list, so ranking churn doesn't make turrets flicker between targets; only weapons whose own target vanished re-roll. Out-of-arc/out-of-range targets are skipped for engagement (射界 rule unchanged) but do NOT trigger a re-roll — transient unreachability shouldn't reshuffle the assignment. Each fired missile carries its own ticket, so a ripple of interceptors spreads across the incoming salvo instead of ganging up on rank 0.

**Fire-solution plumbing**: the captain's `TryGetChannelFireSolution(channel, …)` became `TryGetTargetFireSolution(target, …)` — same muzzle/velocity bucket cache, keyed by target instead of channel, since channel 0 now hands different targets to different weapons while two channels locking the same target still share one cached solution. `FireChannels.TrySelectSolution`/`SelectTarget` take the caller's `FireChannelAssignment`; channels 1–3 are untouched single locks and channel 0 still outranks them.

**Networking**: new `CaptainLockNet.Channel0ListMsg` (shipPlayerId, coreGuidHash, count, 4×(playerId, guidHash)) broadcasts the list's ILockable projection whenever it changes, so every machine's radar rings the same threats. Bare shells have no cross-client identity and stay host-local (the radar draws no marker for them anyway). Weapon-side assignments are NOT synced — fire control is host-only; client copies of the list exist for radar display and missile-mirror engagement authorization.

**Radar**: channel 0 draws up to 4 red reticles simultaneously (`channel0Icons`), rank 0 at full channel alpha and each lower rank one `Channel0RankAlphaStep` dimmer, so the ordering reads at a glance. Channels 1–3 keep their single reticles.

**Status**: accepted; pending in-game playtest.

**Considered Options**

- *Ship-wide deterministic partition of weapons over targets (round-robin / hash)* — rejected in favour of the user-specified per-weapon lottery; the lottery's expected 50/25/12.5/12.5 split needs no central coordinator and degrades gracefully as targets die.
- *Re-rolling on every list change* — rejected (user decision): threat scores of similar closers cross constantly; sticky assignments keep turret slews purposeful.
- *Syncing per-weapon assignments to clients* — unnecessary; only the host runs weapon fire control. Client mirror missiles draw from their local list copy, which is close enough for a purely visual mirror.
- *Membership hysteresis for the 4th slot* — initially deferred, then implemented after the first playtest: the radar's lock lines (each list member's plumb line gains an origin leg) visibly flapped between two similar-scored threats. Incumbents now get a rank-weighted score bonus before re-ranking (`IncumbentStickiness` — prev rank 0 ×1.3 down to prev rank 3 ×1.075, so both membership churn and #1/#2 order swaps need a real score margin) and keep threat status slightly beyond the 5 s window (`IncumbentWindowGrace` ×1.3) so a contact hovering on the window boundary doesn't strobe in and out.
- *Qualification hold* (`ThreatHoldSeconds`, 0.6 s) — added after a second playtest symptom: ALL lock lines blinked in and out together. Root cause: the hull-impact gate is a binary line-vs-box test, and a PN missile's velocity vector weaves (single-nozzle bang-bang steering, ADR-0008), so an approaching salvo's extrapolated lines swing on and off the hull-box edge roughly in phase — the whole list emptied and refilled several times a second, which score hysteresis cannot absorb (the gate returns "not a threat" outright, not a lower score). An incumbent that fails the gate now keeps its spot at its last passing score until it has failed continuously for the hold duration; held targets must still be alive, tracked and filter-allowed. Bookkeeping lives in a per-captain `threatMemory` dictionary, pruned of dead/long-unqualified entries every tick.

**Consequences**

- The AA layer now degrades statistically: with 2 threats, on average half the guns take each; a lone threat still gets everything. Expected, per the formula.
- `Channel0ListMsg` fires on every membership/order change; with many similar-scored shells inbound the ILockable projection can reshuffle often. If MP traffic ever matters, throttle sends to ~5 Hz.
- Shells participate in the threat list but never appear on client radars or in the broadcast — a client's list can be shorter than the host's. Host-side fire control is unaffected.
- Feel constants pending playtest: `ThreatScoreTimeSoftening` (0.5 s), `Channel0RankAlphaStep`, `UnknownThreatMass`, `IncumbentStickiness` (0.3), `IncumbentWindowGrace` (1.3).
