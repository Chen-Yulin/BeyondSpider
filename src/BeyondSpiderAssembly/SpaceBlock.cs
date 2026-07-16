using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public abstract class SpaceBlock : BlockScript
    {
        public int PlayerID { get; protected set; }
        public MPTeam Team { get; protected set; }
        protected Rigidbody Body;

        public override void SafeAwake()
        {
            PlayerID = BlockBehaviour.ParentMachine.PlayerID;
            Team = BlockBehaviour.Team;
            Body = GetComponent<Rigidbody>();
        }

        // The ship this block is CONNECTED to (tick-3 connectivity partition, ADR-0011) — no
        // longer "the player's ship": one player may field several, and each block belongs to
        // whichever ship's hull it is physically part of.
        protected ShipState OwnShip()
        {
            return BlockBehaviour == null ? null : SpaceCombatRegistry.ShipOf(BlockBehaviour);
        }

        // Called by the tick-3 partition when this block lands in a ship's connectivity
        // component. Subsystem blocks override it with their RegisterSubsystem call. Runs on
        // host and clients alike (the partition is local to every machine), unlike the
        // host-gated per-tick registration retries, which stay only as self-healing for blocks
        // placed onto an already-simulating ship.
        public virtual void OnAssignedToShip(ShipState ship)
        {
        }

        // MInfo isn't part of Besiege's own AddKey/AddSlider/AddToggle/AddText/AddMenu/AddLimits
        // family -- see MInfo.cs -- but it's declared here so every SpaceBlock can call AddInfo(...)
        // the same bare, unqualified way it already calls those.
        public MInfo AddInfo(string displayName, string key, string initialValue = "")
        {
            return (MInfo)AddCustom(new MInfo(displayName, key, initialValue));
        }

        // Same bare-call idiom for the fire-channel mask mapper (see MFireChannel.cs). Default
        // is all four channels enabled, so a freshly placed weapon responds to any lock the
        // ship holds — the multi-channel equivalent of the old single-lock behavior.
        public MFireChannel AddFireChannel(string displayName, string key, int defaultMask = FireChannels.AllMask)
        {
            return (MFireChannel)AddCustom(new MFireChannel(displayName, key, defaultMask));
        }
    }
}
