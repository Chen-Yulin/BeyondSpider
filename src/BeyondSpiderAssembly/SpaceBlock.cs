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
    }
}
