using System.Collections.Generic;
using Modding;
using Modding.Blocks;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // MKey does not override Equals (reference equality) and Compare() requires an exact
    // same-length key list, so it rejects a match when either side has more than one key
    // bound. Blocks let a player bind several physical keys to one control, so "does this
    // control share a key with that control" has to be an any-key-in-A-matches-any-key-in-B
    // scan, same idea as the WW2-Naval mod's Gunner.FindHinge/FindGun.
    public static class MKeyMatch
    {
        public static bool SharesBinding(MKey a, MKey b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            if (a.useMessage || b.useMessage)
            {
                if (!a.useMessage || !b.useMessage || a.message == null || b.message == null)
                {
                    return false;
                }
                for (int i = 0; i < a.message.Length; i++)
                {
                    for (int j = 0; j < b.message.Length; j++)
                    {
                        if (a.message[i] == b.message[j])
                        {
                            return true;
                        }
                    }
                }
                return false;
            }

            for (int i = 0; i < a.KeysCount; i++)
            {
                KeyCode keyA = a.GetKey(i);
                for (int j = 0; j < b.KeysCount; j++)
                {
                    if (keyA == b.GetKey(j))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }

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
        public int PlayerID { get; private set; }
        public MPTeam Team { get; private set; }
        public SteeringWheel Wheel { get; private set; }
        public BlockBehaviour Block { get; private set; }

        private readonly List<object> owners = new List<object>();
        private bool originalCaptured;
        private bool originalReturnToCenter;
        private float originalSpeed;
        private Rigidbody hingeBody;
        private Joint hingeJoint;

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
            hingeBody = GetComponent<Rigidbody>();
            hingeJoint = GetComponent<Joint>();
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
            Wheel.ReturnToCenterToggle.SetValue(false);
            Wheel.SpeedSlider.SetValue(originalSpeed);
            WakeConnectedBodies();
        }

        // SteeringWheel.FixedUpdateBlock (reflected from Assembly-CSharp.dll) skips its own
        // Rigidbody.IsSleeping()/WakeUp() calls entirely whenever input==0 (always true here —
        // nothing is ever really pressing this hinge's key) and ReturnToCenterToggle is off,
        // which is exactly the steady state above holds it in. Left alone, a quiescent turret's
        // rigidbody can fall asleep and then silently ignore AngleToBe/targetRotation changes —
        // including the calibration nudge — until something wakes it. Rather than periodically
        // flipping ReturnToCenterToggle back on to piggyback on the vanilla wake calls (which
        // couples an unrelated toggle to a physics-engine detail, and briefly reintroduces the
        // vanilla auto-center pull even if only for a single tick), just wake both relevant
        // bodies directly every tick this hinge is owned.
        private void WakeConnectedBodies()
        {
            if (hingeBody != null && hingeBody.IsSleeping())
            {
                hingeBody.WakeUp();
            }
            if (hingeJoint != null && hingeJoint.connectedBody != null && hingeJoint.connectedBody.IsSleeping())
            {
                hingeJoint.connectedBody.WakeUp();
            }
        }

        public bool MatchesLeft(MKey key)
        {
            return key != null && LeftKey != null && MKeyMatch.SharesBinding(LeftKey, key);
        }

        public bool MatchesRight(MKey key)
        {
            return key != null && RightKey != null && MKeyMatch.SharesBinding(RightKey, key);
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

        public Vector3 ToLocalDirection(Vector3 worldDirection)
        {
            return Quaternion.Inverse(transform.rotation) * worldDirection;
        }

        // Local-space companion to SignedAngleTo: both directions must already be in this
        // hinge's local frame (via ToLocalDirection). Sampling the gun's forward in local
        // space makes the calibration measurement immune to the whole ship rotating between
        // the two samples.
        public float SignedAngleAroundAxis(Vector3 fromLocal, Vector3 toLocal)
        {
            if (Wheel == null)
            {
                return 0f;
            }

            Vector3 axis = Wheel.axis;
            if (axis.sqrMagnitude < 0.0001f)
            {
                return 0f;
            }
            axis.Normalize();

            Vector3 from = Vector3.ProjectOnPlane(fromLocal, axis);
            Vector3 to = Vector3.ProjectOnPlane(toLocal, axis);
            if (from.sqrMagnitude < 0.0001f || to.sqrMagnitude < 0.0001f)
            {
                return 0f;
            }

            from.Normalize();
            to.Normalize();
            return Mathf.Atan2(
                Vector3.Dot(axis, Vector3.Cross(from, to)),
                Vector3.Dot(from, to)) * Mathf.Rad2Deg;
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
