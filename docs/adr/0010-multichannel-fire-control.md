# Multi-channel fire control (fire channels)

The ship used to hold a single radar lock (`ShipState.LockedTarget`) that every fire-control consumer shared. We replace it with **four independent fire channels** (`ShipState.ChannelTargets[4]`, see `FireChannels.cs`), so the captain can lock several targets at once and assign different weapon groups to each.

**Channel roles and colors.** Channel 0 is the **air-defence channel**: host-side, the captain auto-locks the highest-threat hostile — 距离近 且 速度矢量指向本舰, scored as closing speed over distance, with a 1.3× hysteresis so the lock doesn't flap between similar threats, and gated on `ILockable` + sensor coverage (`ship.Tracks`). The player may override it by hand; a manual channel-0 lock sets `ShipState.Channel0ManualLock`, which pauses auto selection until the lock is cleared or its target dies. Channels 1–3 are purely player-assigned. Each channel has a fixed color used everywhere it appears — tab button, radar lock reticle, mapper button: **0 red, 1 yellow, 2 cyan, 3 purple** (`FireChannels.Colors`).

**Radar UI.** `CaptainRadarView` gains a tab strip along the panel's top-left edge, one tab per channel in its color; the active tab decides which channel a left-click on a blip (un)locks. The radar picture itself is identical on every tab. All four channels' lock reticles draw simultaneously (each in its channel color, active channel bright, others dimmed, higher channels ringed slightly wider so locks on the same blip nest concentrically) — each tab "owns" its lock for clicking purposes while the whole fire-control picture stays visible. Tab clicks are handled in `Update` (`TrySelectTabAtMouse`), not `GUI.Button`, because `ConsumePanelMouseEvent` eats IMGUI mouse events over the panel.

**The MFireChannel mapper.** Every automatic weapon (flak turret, missile launcher) and the gunner carries a new wrench-menu mapper, 火控通道选择 `MFireChannel` — four channel buttons laid out horizontally (effectively four MToggles in a row), each tinted with its channel color. `Value` is a bitmask; default is all four channels, which reproduces the old "any lock" behavior for existing machines. It is built on the same `MCustom<T>`/`CustomSelector` extension point as `MInfo`, but interactive: buttons are `Elements.MakeTexture` quads (solid-color procedural textures — the template material's shader is unknown, so color rides the texture, not a material tint) with `Elements.AddButton` click handlers, persisted via `XInteger`.

**Priority when several channels hold targets** (`FireChannels.TrySelectSolution` / `SelectTarget`):

1. Channel 0's target outranks every other channel outright.
2. A target outside the weapon's range or fire arc (射界) is not considered at all. The flak turret's arc check mirrors its own yaw-limit/pitch clamps (`IsAimPointInArc`); the gunner and missiles have no arc check (hinge limits are opaque; missiles turn).
3. Otherwise channels 1–3 compete on the score `1/(目标与炮管夹角度数 + 10°) × i + 1/目标距离` (`FireChannels.Score`; `i = AngleScoreWeight`, currently 1) — prefer the target nearest the boresight, tie-break on distance.

**Consumers.** The captain's `TryGetLockedFireSolution` becomes per-channel `TryGetChannelFireSolution(channel, …)` with the same muzzle-position/velocity bucket cache (channel folded into the key). The captain's IFF override (`CanCommandLockedFireAt`) is applied per candidate, so a locked friendly stays engageable with IFF off exactly as before. `MissileFireControl.SelectTarget` orders: defensive solution → fire channels → nearest hostile, where the nearest-hostile fallback now applies **only to in-flight missiles** (a launcher deciding whether to auto-fire is strictly channel-driven — otherwise the mapper would be meaningless for launchers). A missile copies its launcher's mask at spawn; the mask rides the MP spawn message so client mirrors re-acquire from the same channels. `CanEngage`'s lock-identity check becomes "is locked on any channel" (`FireChannels.IsChannelTarget`). The `DefensiveSolution` fallback (point-defence reflex from the defence director) is unchanged and still consulted before the channels by flak/gunner/missiles.

**Networking.** `CaptainLockNet.LockMsg` gains two fields: the channel index and a `manual` flag (`shipPlayerId, channel, targetPlayerId, targetGuidHash, hasLock, manual`). Manual radar clicks broadcast with `manual = true`; the captain's channel-0 auto lock and host-side dead-target pruning broadcast with `manual = false`, so every machine agrees on `Channel0ManualLock`.

**Status**: accepted; pending in-game playtest (mapper button layout coordinates and radar tab sizing are untuned guesses).

**Considered Options**

- *Keep `LockedTarget` as an alias for channel 0* — rejected; every consumer was rewritten to channels anyway, and a shadow single-lock field invites silent divergence.
- *Show only the active tab's reticle* (most literal reading of "每个tab有各自独立的锁定目标") — rejected in favour of all-channels-visible with the active one highlighted; hiding the other locks hides the ship's actual fire-control state and colors already disambiguate.
- *Four separate `MToggle`s per weapon* — rejected; the spec explicitly asks for one horizontal four-button mapper, and four toggle rows would bloat the wrench menu.
- *Per-weapon arc predicate as an interface* — a plain delegate (`FireChannels.ArcCheck`) is enough; the flak turret caches one instance so the per-tick walk doesn't allocate.
- *Keep nearest-hostile fallback for launcher auto-fire* — rejected (see above); it would make the channel mapper a no-op for launchers whenever anything hostile was in range.

**Consequences**

- Channel-0 auto-lock means AA-subscribed weapons now engage the top closing threat **without any player lock** (that's its job). A weapon that should only ever fire on manual command should have channel 0 deselected in its mapper.
- Old saves' machines get the default all-channels mask; the old single lock's semantics (any weapon with fire control engages the lock) are preserved by that default. The old 4-field `LockMsg` wire format changed — all players must run the same mod build (already true for this unpublished mod).
- Auto-lock hysteresis (`AutoLockHysteresis`) and the threat gate (`AutoLockMinClosingSpeed`) are feel constants pending playtest, as are `FireChannels.AngleScoreWeight` (the spec's 权重系数 i) and the selector button layout in `MFireChannel.cs`.
- The captain now clears all channels on `OnSimulateStop`, so locks don't leak across simulation restarts.
