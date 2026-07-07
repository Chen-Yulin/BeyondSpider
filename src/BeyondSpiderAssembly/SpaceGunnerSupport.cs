using System.Collections.Generic;
using Modding;
using Modding.Blocks;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class SpaceGunnerNet : SingleInstance<SpaceGunnerNet>
    {
        public override string Name { get { return "BeyondSpider Space Gunner Net"; } }

        public static MessageType ActiveMsg = ModNetworking.CreateMessageType(DataType.Integer, DataType.Integer, DataType.Boolean);

        private readonly Dictionary<string, SpaceGunnerBlock> gunners = new Dictionary<string, SpaceGunnerBlock>();

        public void Register(SpaceGunnerBlock gunner)
        {
            string key = Key(gunner.PlayerID, gunner.GuidHash);
            if (gunners.ContainsKey(key))
            {
                gunners[key] = gunner;
            }
            else
            {
                gunners.Add(key, gunner);
            }
        }

        public void Unregister(SpaceGunnerBlock gunner)
        {
            gunners.Remove(Key(gunner.PlayerID, gunner.GuidHash));
        }

        public void ActiveReceiver(Message msg)
        {
            SpaceGunnerBlock gunner = Find((int)msg.GetData(0), (int)msg.GetData(1));
            if (gunner != null)
            {
                gunner.ReceiveActive((bool)msg.GetData(2));
            }
        }

        private SpaceGunnerBlock Find(int playerId, int guid)
        {
            SpaceGunnerBlock gunner;
            gunners.TryGetValue(Key(playerId, guid), out gunner);
            return gunner;
        }

        private static string Key(int playerId, int guid)
        {
            return playerId.ToString() + ":" + guid.ToString();
        }
    }

    public class SpaceGunnerHingeController : SingleInstance<SpaceGunnerHingeController>
    {
        public override string Name { get { return "BeyondSpider Space Gunner Hinge Controller"; } }

        private void Awake()
        {
            Events.OnBlockInit += AddHinge;
        }

        private void AddHinge(Block block)
        {
            BlockBehaviour blockBehaviour = block.GameObject.GetComponent<BlockBehaviour>();
            if (blockBehaviour == null)
            {
                return;
            }

            SteeringWheel wheel = blockBehaviour.GetComponent<SteeringWheel>();
            if (wheel == null || blockBehaviour.GetComponent<SpaceGunnerHinge>() != null)
            {
                return;
            }

            blockBehaviour.gameObject.AddComponent<SpaceGunnerHinge>();
        }
    }

    public static class SpaceGunnerHingeRegistry
    {
        private static readonly Dictionary<int, List<SpaceGunnerHinge>> hingesByPlayer = new Dictionary<int, List<SpaceGunnerHinge>>();

        public static IList<SpaceGunnerHinge> HingesFor(int playerId)
        {
            List<SpaceGunnerHinge> hinges;
            if (!hingesByPlayer.TryGetValue(playerId, out hinges))
            {
                hinges = new List<SpaceGunnerHinge>();
                hingesByPlayer.Add(playerId, hinges);
            }
            return hinges;
        }

        public static void Register(SpaceGunnerHinge hinge)
        {
            if (hinge == null)
            {
                return;
            }

            List<SpaceGunnerHinge> hinges = (List<SpaceGunnerHinge>)HingesFor(hinge.PlayerID);
            if (!hinges.Contains(hinge))
            {
                hinges.Add(hinge);
            }
        }

        public static void Unregister(SpaceGunnerHinge hinge)
        {
            if (hinge == null)
            {
                return;
            }

            List<SpaceGunnerHinge> hinges;
            if (hingesByPlayer.TryGetValue(hinge.PlayerID, out hinges))
            {
                hinges.Remove(hinge);
            }
        }
    }

    public class SpaceGunnerHinge : MonoBehaviour
    {
        private const int OverrideCycle = 10;

        public int PlayerID { get; private set; }
        public MPTeam Team { get; private set; }
        public SteeringWheel Wheel { get; private set; }
        public BlockBehaviour Block { get; private set; }

        private readonly List<object> owners = new List<object>();
        private bool originalCaptured;
        private bool originalReturnToCenter;
        private float originalSpeed;
        private int fixedCounter;

        public MKey LeftKey
        {
            get { return Wheel != null && Wheel.KeyList != null && Wheel.KeyList.Count > 0 ? Wheel.KeyList[0] : null; }
        }

        public MKey RightKey
        {
            get { return Wheel != null && Wheel.KeyList != null && Wheel.KeyList.Count > 1 ? Wheel.KeyList[1] : null; }
        }

        public bool IsValid
        {
            get { return Block != null && Wheel != null && Block.isSimulating; }
        }

        private void Awake()
        {
            Block = GetComponent<BlockBehaviour>();
            Wheel = GetComponent<SteeringWheel>();
            if (Block != null)
            {
                PlayerID = Block.ParentMachine.PlayerID;
                Team = Block.Team;
            }
        }

        private void Start()
        {
            SpaceGunnerHingeRegistry.Register(this);
        }

        private void OnDestroy()
        {
            RestoreOriginals();
            SpaceGunnerHingeRegistry.Unregister(this);
        }

        private void FixedUpdate()
        {
            if (owners.Count <= 0 || Wheel == null || Block == null || !Block.isSimulating)
            {
                return;
            }

            EnsureOriginalCaptured();
            fixedCounter++;
            if (fixedCounter % OverrideCycle == 0)
            {
                Wheel.ReturnToCenterToggle.SetValue(true);
                Wheel.SpeedSlider.SetValue(0.01f);
            }
            else
            {
                Wheel.ReturnToCenterToggle.SetValue(false);
                Wheel.SpeedSlider.SetValue(originalSpeed);
            }
        }

        public bool MatchesLeft(MKey key)
        {
            return key != null && LeftKey != null && LeftKey.Equals(key);
        }

        public bool MatchesRight(MKey key)
        {
            return key != null && RightKey != null && RightKey.Equals(key);
        }

        public void AddOwner(object owner)
        {
            if (owner == null || owners.Contains(owner))
            {
                return;
            }
            owners.Add(owner);
            EnsureOriginalCaptured();
        }

        public void RemoveOwner(object owner)
        {
            if (owner == null)
            {
                return;
            }
            owners.Remove(owner);
            if (owners.Count == 0)
            {
                RestoreOriginals();
            }
        }

        public float SignedAngleTo(Vector3 currentForward, Vector3 desiredForward)
        {
            if (Wheel == null)
            {
                return 0f;
            }

            Vector3 axisWorld = transform.rotation * Wheel.axis;
            if (axisWorld.sqrMagnitude < 0.0001f)
            {
                return 0f;
            }
            axisWorld.Normalize();

            Vector3 current = Vector3.ProjectOnPlane(currentForward, axisWorld);
            Vector3 desired = Vector3.ProjectOnPlane(desiredForward, axisWorld);
            if (current.sqrMagnitude < 0.0001f || desired.sqrMagnitude < 0.0001f)
            {
                return 0f;
            }

            current.Normalize();
            desired.Normalize();
            return Mathf.Atan2(
                Vector3.Dot(axisWorld, Vector3.Cross(current, desired)),
                Vector3.Dot(current, desired)) * Mathf.Rad2Deg;
        }

        public void AddAngle(float delta)
        {
            if (Wheel == null)
            {
                return;
            }

            Wheel.AngleToBe += delta;
            ClampAngleToLimits();
        }

        private void ClampAngleToLimits()
        {
            if (Wheel == null || Wheel.LimitsSlider == null || !Wheel.LimitsSlider.IsActive)
            {
                return;
            }

            Wheel.AngleToBe = Mathf.Clamp(Wheel.AngleToBe, -Wheel.LimitsSlider.Min, Wheel.LimitsSlider.Max);
        }

        private void EnsureOriginalCaptured()
        {
            if (originalCaptured || Wheel == null)
            {
                return;
            }

            originalReturnToCenter = Wheel.ReturnToCenterToggle.IsActive;
            originalSpeed = Wheel.SpeedSlider.Value;
            originalCaptured = true;
        }

        private void RestoreOriginals()
        {
            if (!originalCaptured || Wheel == null)
            {
                return;
            }

            Wheel.ReturnToCenterToggle.SetValue(originalReturnToCenter);
            Wheel.SpeedSlider.SetValue(originalSpeed);
            originalCaptured = false;
        }
    }
}
