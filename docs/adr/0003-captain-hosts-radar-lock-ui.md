# Captain, not ShipCore, hosts the 3D radar/lock UI

`docs/space-combat-framework.md` originally assigned "hosts or opens the 3D radar/command UI" to `舰船核心`/`ShipCore`, folding it in alongside ship identity and energy budget. The original `策划案.md` design, however, put the 3D radar view, click-to-lock, and command-of-weapons behavior on `船长`/Captain — and current code already reflects that split: `SpaceCaptainBlock` is the priority-switching command block, while `SpaceShipCore` has no UI code at all. Rather than build the radar/lock feature onto ShipCore per the framework doc (which would leave Captain a near-empty block and contradict the original design), we're building it on Captain, matching `策划案.md` and the explicit ask that started this work.

**Status**: accepted

**Consequences**

`ShipCore` keeps ship identity, energy budget, and the spatial anchor used when this ship is a target on someone else's radar. `Captain` becomes the fire-control command entry point: it hosts the 3D radar/lock UI and owns the ship-wide locked-target state that heavy missiles read. `CONTEXT.md` and `docs/space-combat-framework.md` have been updated to match. A ship with no Captain block has no way to open the radar UI or lock targets, even though ShipCore/RadarPanel still populate sensor tracks normally.
