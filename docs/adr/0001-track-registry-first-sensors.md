# Use track registries before broad physics scans

Space combat sensors will treat ships, heavy missiles, point-defense missiles, and large projectiles as registered trackable objects, with radar volumes filtering that registry into sensor tracks. This is preferable to broad per-radar physics scans because the mod needs many long-range sensors and high-speed objects, while the references already show both sides of the trade-off: ModernAirCombat's `OverlapBoxNonAlloc` scan is safer than trigger spam, but the design brief explicitly asks for special objects to register into lists for performance.

**Status**: proposed

**Consequences**

Radar can remain deterministic and cheap for known combat objects, while physics queries are reserved for legacy or unregistered objects. Every projectile and missile class that should be detectable must register and unregister cleanly.
