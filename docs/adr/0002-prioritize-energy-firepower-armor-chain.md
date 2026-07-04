# Prioritize energy-firepower-armor validation over missile-defense

The project's first playable slice targeted defensive combat (ShipCore, EnergyGrid, RadarPanel, DefenseDirector, ShieldProjector, CiwsBlock, HeavyNuclearMissile) and treated offensive systems as a later reuse of the same pathways. In practice, the energy and firepower halves of the offensive path are already implemented and wired together — `SpaceShipCore`/`EnergyGrid` allocate an Armor bus, and `RailgunBarrelBlock`/`SpaceFlakTurretBlock` already draw from the Weapon bus — but nothing consumes the Armor bus and no `DamageRouter`/nano-armor behavior exists anywhere in `src/BeyondSpiderAssembly`. The missile-defense slice is already a complete, working skeleton; the energy-firepower-armor slice is one component (nano armor + damage routing) away from being a complete, independently testable loop.

**Status**: accepted

**Consequences**

Near-term development builds nano armor and `DamageRouter` first, and routes `RailgunBarrelBlock`/`SpaceFlakTurretBlock` hits through it, before continuing to deepen radar/defense-director/missile work. The missile-defense skeleton stays in place as a regression target, not the active development focus. `docs/agent-besiege-mod-guide.md`'s "推荐开发顺序" and `docs/space-combat-framework.md`'s validation-slice section have been updated to reflect this order.
