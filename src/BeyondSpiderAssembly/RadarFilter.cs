namespace BeyondSpiderAssembly
{
    // 雷达筛选 rows — the kinds of contact the radar screen's filter bar can switch on and
    // off, one independent configuration per fire channel (ADR-0013).
    public enum RadarCategory
    {
        Ship = 0,
        HeavyMissile = 1,
        MediumMissile = 2,
        SmallMissile = 3,
        // Cannon shells of ShellMinCaliberMm and up. Display-only row (see Allows): it decides
        // whether big shells draw as blips, never whether channel 0 may point-defend against
        // them. Defaults OFF, unlike every other row.
        Shell = 4
    }

    // Which contacts a fire channel shows on the radar and is allowed to lock. Each channel
    // carries its own category bitmask (ShipState.ChannelFilters); the filter gates both the
    // radar screen's markers and target acquisition, channel 0's automatic air-defence selection
    // included. See docs/adr/0013-per-channel-radar-filter.md.
    public static class RadarFilter
    {
        public const int Count = 5;
        public const int AllMask = (1 << Count) - 1;

        // Short enough to fit the filter bar's buttons, and ASCII: the radar screen is IMGUI, and
        // nothing has established that Besiege's IMGUI skin font carries CJK glyphs (the Chinese
        // strings this mod already ships all live in wrench-menu widgets, which draw with the
        // game's own font). Reads as 舰船 / 重型导弹 / 中导弹 / 小导弹 / 炮弹.
        public static readonly string[] Labels = { "SHIP", "HVY", "MED", "SML", "SHL" };

        // Missile size tiers. Every missile size reports TrackKind.HeavyMissile, so S/M/L are told
        // apart by physical radius (MissileLauncherAssets.MissileRadius = 3/5/10). Thresholds sit
        // at the midpoints between those three radii so a track still classifies correctly if that
        // table gets retuned a little.
        private const float SmallMaxRadius = 4f;
        private const float MediumMaxRadius = 7.5f;

        // Only shells at least this caliber ever draw on the radar, even with the SHL row on —
        // small-arms/CIWS-grade rounds would flood the screen with dots.
        public const float ShellMinCaliberMm = 76f;

        // Every channel starts unfiltered EXCEPT the shell row: ships/missiles keep the
        // pre-filter behaviour of seeing and locking everything, while big-shell blips are
        // opt-in (the row exists to let a player turn them ON, not to hide them).
        public static int[] NewChannelFilters()
        {
            int[] filters = new int[FireChannels.Count];
            for (int i = 0; i < filters.Length; i++)
            {
                filters[i] = AllMask & ~(1 << (int)RadarCategory.Shell);
            }
            return filters;
        }

        public static bool Contains(int mask, RadarCategory category)
        {
            return (mask & (1 << (int)category)) != 0;
        }

        public static int Toggle(int mask, RadarCategory category)
        {
            return (mask ^ (1 << (int)category)) & AllMask;
        }

        // False for anything the radar draws no marker for — today that is cannon shells under
        // ShellMinCaliberMm, which have no category of their own (big shells classify as Shell).
        public static bool TryGetCategory(ITrackable target, out RadarCategory category)
        {
            category = RadarCategory.Ship;
            if (target == null)
            {
                return false;
            }
            if (target.Kind == TrackKind.Ship)
            {
                return true;
            }
            if (target.Kind == TrackKind.LargeProjectile)
            {
                SpaceKineticRound round = target as SpaceKineticRound;
                if (round != null && round.Caliber >= ShellMinCaliberMm)
                {
                    category = RadarCategory.Shell;
                    return true;
                }
                return false;
            }
            if (target.Kind != TrackKind.HeavyMissile)
            {
                return false;
            }

            float radius = target.Radius;
            category = radius < SmallMaxRadius ? RadarCategory.SmallMissile
                : radius < MediumMaxRadius ? RadarCategory.MediumMissile
                : RadarCategory.HeavyMissile;
            return true;
        }

        // Whether the radar screen draws a marker for this contact on a channel with this mask.
        // Uncategorized contacts are never drawn — the same kinds the radar has always skipped.
        public static bool Shows(int mask, ITrackable target)
        {
            RadarCategory category;
            return TryGetCategory(target, out category) && Contains(mask, category);
        }

        // Whether a channel with this mask may target this contact. Shells always pass, whatever
        // the SHL row says: that row is a display toggle (it defaults OFF), and letting it gate
        // targeting would silently disable channel 0's point defense against shells by default,
        // with nothing about the "show blips" checkbox suggesting it did that. Other
        // uncategorized contacts always pass for the same no-checkbox-governs-them reason.
        public static bool Allows(int mask, ITrackable target)
        {
            if (target != null && target.Kind == TrackKind.LargeProjectile)
            {
                return true;
            }

            RadarCategory category;
            if (!TryGetCategory(target, out category))
            {
                return true;
            }
            return Contains(mask, category);
        }
    }
}
