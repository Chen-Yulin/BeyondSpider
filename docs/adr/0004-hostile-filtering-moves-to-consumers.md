# Radar reports everyone; consumers decide who's hostile

Adding the captain radar/lock UI required showing both friendly and hostile ships/heavy missiles on the same picture, but `RadarPanelBlock`'s `Iff` toggle (default on) was dropping same-team tracks from `ship.Tracks` entirely, and `CiwsBlock`/`SpaceGunnerBlock` only ever read `ship.DefensiveSolution` (auto-computed by `DefenseDirectorBlock`). Rather than add a second, parallel "unfiltered" track list, `RadarPanelBlock` now reports every trackable in range/cone regardless of team, and `DefenseDirectorBlock` applies `SpaceBallistics.IsHostile` itself before scoring — matching the existing rule that RadarPanel senses and directors decide. On top of that, the previously-unused `ShipState.Priority` (`AntiAir`/`AntiShip`, already toggleable from the captain block) now arbitrates which solution `CiwsBlock`/`SpaceGunnerBlock` fire on when both a manual captain lock and an automatic defensive solution are live: `AntiShip` prefers the manual lock, `AntiAir` prefers `DefenseDirector`'s solution. Heavy missile guidance is unaffected by this priority — it always follows the manual lock when one exists.

**Status**: accepted

**Consequences**

`RadarPanelBlock.Iff` no longer filters `ship.Tracks`; it's left in place for build/save compatibility but is currently unused (a future pass may repurpose or remove it, see `docs/agent-besiege-mod-guide.md`). Any future consumer of `ship.Tracks` (not just `DefenseDirectorBlock`) must do its own hostility check via `SpaceBallistics.IsHostile` — tracks are not pre-filtered. `ship.Priority` now has real gameplay effect instead of only being shown in the debug HUD.
