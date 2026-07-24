using System.Collections.Generic;
using System.Reflection;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // Full-precision ship position replication (ADR-0021), host -> clients, gated on the host's
    // "no boundary" toggle. Besiege compresses a networked block's position into 17/14 bits relative to
    // NetworkCompression.worldBounds, which coarsens (and, without ADR-0019's widened box, wraps) once a
    // hull flies far. This replaces that, for ships, with a full-float pose per rigid CLUSTER BASE:
    //
    //   Host: for each intact cluster of each ship (machine.simClusters — only the base block streams a
    //   transform, so one record per cluster covers the whole rigid part), stop the engine's own stream
    //   for that base (NetworkBlock.pollTransform = false — the "no duplicate" part), and send its exact
    //   world position (DataType.Vector3, full float) + rotation (euler Vector3; rotation has no range
    //   problem). Dead-band so a still ship costs nothing; keyframe every second for late joiners.
    //
    //   Client: drive the resolved base block's transform toward the received pose, and — the crux, see
    //   ADR-0021 §5 — write the base NetworkBlock's PRIVATE `transformMatrix` field by reflection each
    //   frame. On the client every rigid child re-derives its own position from that cached matrix every
    //   frame (NetworkBlock.UpdateEntity, `posActive` is set-once/never-reset), so keeping it current is
    //   what makes the whole cluster follow the precise base. Suppressing the base's stream froze that
    //   matrix, and there is no public setter, hence the reflection.
    //
    // Single-player does nothing here (no networking). Reflection, not Harmony: one cached FieldInfo,
    // one field write per driven base per frame; the coupling to a private field name is the accepted
    // fragility (ADR-0021).
    public class ShipPoseNet : SingleInstance<ShipPoseNet>
    {
        public override string Name { get { return "BeyondSpider Ship Pose Net"; } }

        // (playerId, coreGuidHash, baseGuidHash, position [full float], rotation [euler degrees])
        public static MessageType PoseMsg = ModNetworking.CreateMessageType(
            DataType.Integer, DataType.Integer, DataType.Integer, DataType.Vector3, DataType.Vector3);

        private const float SendInterval = 0.05f;        // 20 Hz host send
        private const float KeyframeInterval = 1f;       // force-resend each base at least this often
        private const float PosThreshold = 0.02f;        // dead-band: skip sends below this move (m)
        private const float RotThresholdDeg = 0.5f;      // dead-band: skip sends below this turn
        private const float SmoothRate = 25f;            // client lerp rate toward the received pose
        private const float StaleTimeout = 1f;           // client stops driving a base after this w/o updates
        private const int MinClusterBlocks = 1;          // clusters smaller than this stay on the engine stream
        private const int MaxRecordsPerShip = 48;        // safety cap so a shattered hull can't flood

        // Reflection into NetworkBlock's private cached transform matrix (ADR-0021 §5). Cached once.
        private static readonly FieldInfo TransformMatrixField =
            typeof(NetworkBlock).GetField("transformMatrix", BindingFlags.NonPublic | BindingFlags.Instance);

        // ---- host state ----
        private float nextSend;
        private readonly HashSet<NetworkBlock> suppressed = new HashSet<NetworkBlock>();
        private readonly HashSet<NetworkBlock> currentBases = new HashSet<NetworkBlock>();
        private readonly Dictionary<int, HostPoseState> hostState = new Dictionary<int, HostPoseState>();

        // ---- client state ----
        private readonly Dictionary<int, ClientPose> clientPoses = new Dictionary<int, ClientPose>();
        private readonly List<int> dropScratch = new List<int>();

        private class HostPoseState
        {
            public Vector3 lastSentPos;
            public Vector3 lastSentRot;
            public float nextKeyframe;
        }

        private class ClientPose
        {
            public int playerId;
            public int coreGuidHash;
            public Vector3 targetPos;
            public Quaternion targetRot;
            public float lastReceived;
            public BlockBehaviour block;   // resolved lazily by GuidHash
            public NetworkBlock net;
        }

        // ------------------------------------------------------------------ host

        private void Update()
        {
            // Host only, and never in single-player (no clients, no compression to fix). If we ever
            // lose that role or the feature is off, put every base we muted back on the engine stream.
            SpaceBoundary boundary = SpaceBoundary.Instance;
            bool hostActive = StatMaster.isMP && !StatMaster.isClient && StatMaster.levelSimulating
                              && boundary != null && boundary.BoundaryOff;
            if (!hostActive)
            {
                RestoreAllSuppressed();
                return;
            }

            if (Time.time < nextSend)
            {
                return;
            }
            nextSend = Time.time + SendInterval;

            currentBases.Clear();
            foreach (ShipState ship in SpaceCombatRegistry.Ships)
            {
                if (ship == null || ship.Machine == null)
                {
                    continue;
                }
                Machine.SimCluster[] clusters = ship.Machine.simClusters;
                if (clusters == null)
                {
                    continue;
                }

                int sent = 0;
                for (int i = 0; i < clusters.Length && sent < MaxRecordsPerShip; i++)
                {
                    Machine.SimCluster sc = clusters[i];
                    if (sc == null || !sc.intact || sc.count < MinClusterBlocks)
                    {
                        continue;
                    }
                    BlockBehaviour baseBlock = sc.Base;
                    if (baseBlock == null || !baseBlock.isSimulating)
                    {
                        continue;
                    }
                    NetworkBlock net = baseBlock.GetComponent<NetworkBlock>();
                    if (net == null)
                    {
                        continue;
                    }

                    // Mute the engine's own 6-byte stream for this base (the "no duplicate" part).
                    net.pollTransform = false;
                    currentBases.Add(net);
                    sent++;

                    int guidHash = baseBlock.BuildingBlock.Guid.GetHashCode();
                    Vector3 pos = baseBlock.transform.position;
                    Vector3 rot = baseBlock.transform.rotation.eulerAngles;

                    HostPoseState st;
                    if (!hostState.TryGetValue(guidHash, out st))
                    {
                        st = new HostPoseState { nextKeyframe = 0f };
                        hostState[guidHash] = st;
                    }
                    bool keyframe = Time.time >= st.nextKeyframe;
                    bool moved = (pos - st.lastSentPos).sqrMagnitude > PosThreshold * PosThreshold
                                 || Quaternion.Angle(Quaternion.Euler(rot), Quaternion.Euler(st.lastSentRot)) > RotThresholdDeg;
                    if (keyframe || moved)
                    {
                        st.lastSentPos = pos;
                        st.lastSentRot = rot;
                        st.nextKeyframe = Time.time + KeyframeInterval;
                        ModNetworking.SendToAll(PoseMsg.CreateMessage(
                            ship.PlayerID, ship.CoreGuidHash, guidHash, pos, rot));
                    }
                }
            }

            // Any base we muted last tick that is no longer a live cluster base (cluster re-partitioned
            // on damage, ship gone, capped out) goes back on the engine stream — err toward re-enabling.
            dropScratch.Clear();
            foreach (NetworkBlock net in suppressed)
            {
                if (!currentBases.Contains(net))
                {
                    if (net != null)
                    {
                        net.pollTransform = true;
                    }
                }
            }
            suppressed.Clear();
            foreach (NetworkBlock net in currentBases)
            {
                suppressed.Add(net);
            }
        }

        private void RestoreAllSuppressed()
        {
            if (suppressed.Count == 0)
            {
                return;
            }
            foreach (NetworkBlock net in suppressed)
            {
                if (net != null)
                {
                    net.pollTransform = true;
                }
            }
            suppressed.Clear();
            currentBases.Clear();
            hostState.Clear();
        }

        // ------------------------------------------------------------------ client

        public void PoseReceiver(Message msg)
        {
            // Only clients apply; the host is the authority and already has the real transforms.
            if (!NetAuthority.IsClient)
            {
                return;
            }
            int guidHash = (int)msg.GetData(2);
            ClientPose cp;
            if (!clientPoses.TryGetValue(guidHash, out cp))
            {
                cp = new ClientPose();
                clientPoses[guidHash] = cp;
            }
            cp.playerId = (int)msg.GetData(0);
            cp.coreGuidHash = (int)msg.GetData(1);
            cp.targetPos = (Vector3)msg.GetData(3);
            cp.targetRot = Quaternion.Euler((Vector3)msg.GetData(4));
            cp.lastReceived = Time.time;
        }

        private void LateUpdate()
        {
            if (!NetAuthority.IsClient || clientPoses.Count == 0)
            {
                return;
            }

            float t = Mathf.Clamp01(Time.deltaTime * SmoothRate);
            dropScratch.Clear();

            foreach (KeyValuePair<int, ClientPose> pair in clientPoses)
            {
                ClientPose cp = pair.Value;
                // Stop driving once the host stops sending (feature turned off, ship gone): the engine's
                // own stream resumes and overwrites transformMatrix on its next update, self-healing.
                if (Time.time - cp.lastReceived > StaleTimeout)
                {
                    dropScratch.Add(pair.Key);
                    continue;
                }

                if (cp.block == null || cp.net == null)
                {
                    Resolve(pair.Key, cp);
                    if (cp.block == null || cp.net == null)
                    {
                        continue;
                    }
                }

                Transform tr = cp.block.transform;
                tr.position = Vector3.Lerp(tr.position, cp.targetPos, t);
                tr.rotation = Quaternion.Slerp(tr.rotation, cp.targetRot, t);
                // The crux (ADR-0021 §5): refresh the base's private cached matrix so every rigid child
                // re-derives from the precise base this frame.
                if (TransformMatrixField != null)
                {
                    TransformMatrixField.SetValue(cp.net, tr.localToWorldMatrix);
                }
            }

            for (int i = 0; i < dropScratch.Count; i++)
            {
                clientPoses.Remove(dropScratch[i]);
            }
        }

        // Find the base block by GuidHash within its ship's block list (populated on the client too by
        // the tick-3 partition). Cached on the ClientPose once found.
        private static void Resolve(int guidHash, ClientPose cp)
        {
            ShipState ship = SpaceCombatRegistry.FindShip(cp.playerId, cp.coreGuidHash);
            if (ship == null)
            {
                return;
            }
            for (int i = 0; i < ship.Blocks.Count; i++)
            {
                BlockBehaviour block = ship.Blocks[i];
                if (block != null && block.BuildingBlock != null
                    && block.BuildingBlock.Guid.GetHashCode() == guidHash)
                {
                    cp.block = block;
                    cp.net = block.GetComponent<NetworkBlock>();
                    return;
                }
            }
        }
    }
}
