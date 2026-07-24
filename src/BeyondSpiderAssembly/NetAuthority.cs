namespace BeyondSpiderAssembly
{
    // The mod's single MP authority test (mirrors the WW2-Naval / ModernAirCombat convention):
    // host does ALL physics, target locking, and damage; clients do display and smoothing only.
    // Every damage/physics call site fences on these instead of re-deriving StatMaster flags, so
    // the authority rule reads the same everywhere.
    public static class NetAuthority
    {
        // True on the MP host and always in singleplayer: "I simulate, damage, and decide."
        public static bool IsAuthority { get { return !StatMaster.isMP || !StatMaster.isClient; } }

        // True only on an MP client: "I display and smooth."
        public static bool IsClient { get { return StatMaster.isMP && StatMaster.isClient; } }
    }
}
