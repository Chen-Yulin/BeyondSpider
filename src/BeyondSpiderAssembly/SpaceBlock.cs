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

        protected ShipState OwnShip()
        {
            return SpaceCombatRegistry.FindShip(PlayerID);
        }

        // MInfo isn't part of Besiege's own AddKey/AddSlider/AddToggle/AddText/AddMenu/AddLimits
        // family -- see MInfo.cs -- but it's declared here so every SpaceBlock can call AddInfo(...)
        // the same bare, unqualified way it already calls those.
        public MInfo AddInfo(string displayName, string key, string initialValue = "")
        {
            return (MInfo)AddCustom(new MInfo(displayName, key, initialValue));
        }
    }
}
