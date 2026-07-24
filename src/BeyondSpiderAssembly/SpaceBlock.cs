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

        // Indestructible connection points (damage-refinement spec, WW2-Naval Gun.cs precedent —
        // `blockJoint.breakForce/breakTorque = float.PositiveInfinity` on the cannon barrel so
        // recoil never rips it off): every mod block's own joint to its neighbor is unbreakable
        // from the moment simulation starts, so ships hold together under recoil, collision and
        // hard maneuvering instead of shaking themselves apart. Subsystems that SHOULD still blow
        // off under a direct hit (SpaceShipCore/SuperCapacitorBlock) override this at detonation
        // time via BreakOwnConnectionJoints. Vanilla wood/log armor blocks are a separate system
        // (NanoArmorBehaviour) with its own progressive structural-weakening pipeline and are
        // untouched by this.
        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            if (BlockBehaviour != null && BlockBehaviour.blockJoint != null)
            {
                BlockBehaviour.blockJoint.breakForce = float.PositiveInfinity;
                BlockBehaviour.blockJoint.breakTorque = float.PositiveInfinity;
            }
        }

        // Detonation counterpart to the above: zeroes this block's own connection joints so the
        // next physics step tears it free, then the caller's own explosion force sends it flying —
        // the same outcome NanoArmorBehaviour.ApplyStructuralWeakening reaches gradually, but done
        // in one shot for a subsystem that blows up outright.
        protected void BreakOwnConnectionJoints()
        {
            if (BlockBehaviour == null)
            {
                return;
            }
            foreach (var joint in BlockBehaviour.iJointTo)
            {
                if (joint == null)
                {
                    continue;
                }
                joint.breakForce = 0f;
                joint.breakTorque = 0f;
            }
            foreach (var joint in BlockBehaviour.jointsToMe)
            {
                if (joint == null)
                {
                    continue;
                }
                joint.breakForce = 0f;
                joint.breakTorque = 0f;
            }
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
