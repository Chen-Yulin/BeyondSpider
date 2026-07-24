using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // MP replication of the host control panel's world overrides (host → clients). The host owns the
    // two toggles (SpaceBoundary.BoundaryOff / DefogOn, set by HostControlPanel); this streams them so
    // every client removes its own boundary and defogs its own view in step — which the "no boundary"
    // case REQUIRES, since the widened MP position-compression range only round-trips if host and client
    // agree on it (see ADR-0019 / SpaceBoundary.UpdateNetworkBounds).
    //
    // Host broadcasts on change and on a slow heartbeat so a client that joins mid-session still catches
    // the current state. Clients never send — they only apply what the host reports.
    public class HostControlNet : SingleInstance<HostControlNet>
    {
        public override string Name { get { return "BeyondSpider Host Control Net"; } }

        // (boundaryOff, defogOn)
        public static MessageType StateMsg = ModNetworking.CreateMessageType(DataType.Boolean, DataType.Boolean);

        // Kept short: the widened MP compression box only round-trips when host and client agree on it,
        // so a client that joins while "no boundary" is on decodes EVERY block's position wrong until it
        // catches a heartbeat. 1 s bounds that window (it's two bools, bandwidth is a non-issue).
        private const float HeartbeatInterval = 1f;
        private float nextHeartbeat;
        private bool lastBoundaryOff;
        private bool lastDefogOn;
        private bool hasSent;

        private void Update()
        {
            // Host only (and never in single-player, where there are no clients to tell).
            if (!StatMaster.isMP || StatMaster.isClient)
            {
                return;
            }
            SpaceBoundary boundary = SpaceBoundary.Instance;
            if (boundary == null)
            {
                return;
            }

            bool boundaryOff = boundary.BoundaryOff;
            bool defogOn = boundary.DefogOn;
            bool changed = !hasSent || boundaryOff != lastBoundaryOff || defogOn != lastDefogOn;
            if (changed || Time.time >= nextHeartbeat)
            {
                hasSent = true;
                lastBoundaryOff = boundaryOff;
                lastDefogOn = defogOn;
                nextHeartbeat = Time.time + HeartbeatInterval;
                ModNetworking.SendToAll(StateMsg.CreateMessage(boundaryOff, defogOn));
            }
        }

        public void StateReceiver(Message msg)
        {
            // Only clients act on it; the host is the source of truth and already has these set.
            if (!NetAuthority.IsClient || SpaceBoundary.Instance == null)
            {
                return;
            }
            SpaceBoundary.Instance.BoundaryOff = (bool)msg.GetData(0);
            SpaceBoundary.Instance.DefogOn = (bool)msg.GetData(1);
        }
    }
}
