using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // MP replication for propulsion (ADR-0018 §9, under ADR-0015's host-authoritative rule).
    // Three messages, two directions:
    //
    //   DriveMsg  (client → host) — the helm's six axes plus its two toggles, sent only when they
    //                               change, so a coasting ship generates no traffic at all.
    //   KeyMsg    (client → host) — one key-controlled thruster's key state, on the edges only.
    //   OutputMsg (host → clients) — every thruster's ACTUAL output, one byte each, 16 per message.
    //
    // The output stream is what makes "flame brightness = real thrust" hold on every machine: a
    // client could re-run the allocator locally for 7 bytes instead of 16, but that needs a
    // bit-identical B matrix (same center of mass, same surviving blocks, same trim sliders) and
    // one desynced frame would point every plume the wrong way, silently. A few hundred bytes per
    // second is not worth a class of bug you cannot see. The upside is that the exhaust becomes a
    // live readout of the allocator — brownouts, saturation and attitude micro-corrections all
    // reproduce exactly.
    public class ThrusterNet : SingleInstance<ThrusterNet>
    {
        public override string Name { get { return "BeyondSpider Thruster Net"; } }

        private const int SlotsPerMessage = 16;
        private const float OutputSyncInterval = 0.1f;

        // Reused so the 10 Hz per-ship stream doesn't allocate.
        private static readonly int[] packScratch = new int[4];

        // (shipPlayerId, coreGuidHash, translation, rotation, attitudeHold, thrustCutoff)
        public static MessageType DriveMsg = ModNetworking.CreateMessageType(
            DataType.Integer, DataType.Integer, DataType.Vector3, DataType.Vector3,
            DataType.Boolean, DataType.Boolean);

        // (playerId, thrusterGuidHash, held)
        public static MessageType KeyMsg = ModNetworking.CreateMessageType(
            DataType.Integer, DataType.Integer, DataType.Boolean);

        // (shipPlayerId, coreGuidHash, startIndex, count, packed0..packed3) — four output bytes per
        // packed integer, so one message carries 16 thrusters in 16 bytes of payload.
        public static MessageType OutputMsg = ModNetworking.CreateMessageType(
            DataType.Integer, DataType.Integer, DataType.Integer, DataType.Integer,
            DataType.Integer, DataType.Integer, DataType.Integer, DataType.Integer);

        // Whether the local machine is the one that owns this block's machine — i.e. whether the
        // player sitting at this keyboard is the one whose key presses should count. Without this
        // gate a host would fly every client's ship with its own keyboard.
        public static bool IsLocallyOwned(BlockBehaviour block)
        {
            if (block == null)
            {
                return false;
            }
            if (!StatMaster.isMP)
            {
                return true;
            }
            Machine machine = block.ParentMachine;
            return machine != null && machine.PlayerID == SpaceCombatRegistry.LocalPlayerId();
        }

        // Host-side output stream, rate-limited per ship. Skipped entirely while the whole ship is
        // idle and already known idle on the clients, which is the common case.
        public static void BroadcastOutputs(ShipState ship)
        {
            if (!StatMaster.isMP || !NetAuthority.IsAuthority || Instance == null)
            {
                return;
            }
            if (Time.time < ship.NextThrustSync)
            {
                return;
            }

            bool idle = true;
            for (int i = 0; i < ship.Thrusters.Count; i++)
            {
                ThrusterBlock thruster = ship.Thrusters[i];
                if (thruster != null && thruster.CurrentOutput > 0.002f)
                {
                    idle = false;
                    break;
                }
            }
            if (idle && ship.ThrustWasIdle)
            {
                return;
            }

            ship.NextThrustSync = Time.time + OutputSyncInterval;
            ship.ThrustWasIdle = idle;

            for (int start = 0; start < ship.Thrusters.Count; start += SlotsPerMessage)
            {
                int count = Mathf.Min(SlotsPerMessage, ship.Thrusters.Count - start);
                int[] packed = packScratch;
                packed[0] = 0;
                packed[1] = 0;
                packed[2] = 0;
                packed[3] = 0;
                for (int slot = 0; slot < count; slot++)
                {
                    ThrusterBlock thruster = ship.Thrusters[start + slot];
                    float output = thruster != null ? Mathf.Clamp01(thruster.CurrentOutput) : 0f;
                    int quantized = Mathf.RoundToInt(output * 255f) & 0xFF;
                    packed[slot >> 2] |= quantized << ((slot & 3) * 8);
                }
                ModNetworking.SendToAll(OutputMsg.CreateMessage(
                    ship.PlayerID, ship.CoreGuidHash, start, count,
                    packed[0], packed[1], packed[2], packed[3]));
            }
        }

        public void DriveReceiver(Message msg)
        {
            ShipState ship = SpaceCombatRegistry.FindShip((int)msg.GetData(0), (int)msg.GetData(1));
            if (ship == null)
            {
                return;
            }
            ship.DriveTranslation = (Vector3)msg.GetData(2);
            ship.DriveRotation = (Vector3)msg.GetData(3);
            ship.AttitudeHold = (bool)msg.GetData(4);
            ship.ThrustCutoff = (bool)msg.GetData(5);
        }

        public void KeyReceiver(Message msg)
        {
            int playerId = (int)msg.GetData(0);
            int guidHash = (int)msg.GetData(1);
            bool held = (bool)msg.GetData(2);

            ThrusterBlock thruster = FindThruster(playerId, guidHash);
            if (thruster != null)
            {
                thruster.KeyHeld = held;
            }
        }

        // Clients only. The host authored these numbers by actually spending energy and applying
        // force; a client that recomputed them locally would be guessing.
        public void OutputReceiver(Message msg)
        {
            if (NetAuthority.IsAuthority)
            {
                return;
            }
            ShipState ship = SpaceCombatRegistry.FindShip((int)msg.GetData(0), (int)msg.GetData(1));
            if (ship == null)
            {
                return;
            }
            int start = (int)msg.GetData(2);
            int count = (int)msg.GetData(3);

            for (int slot = 0; slot < count && slot < SlotsPerMessage; slot++)
            {
                int index = start + slot;
                if (index < 0 || index >= ship.Thrusters.Count)
                {
                    continue;
                }
                ThrusterBlock thruster = ship.Thrusters[index];
                if (thruster == null)
                {
                    continue;
                }
                int packed = (int)msg.GetData(4 + (slot >> 2));
                int quantized = (packed >> ((slot & 3) * 8)) & 0xFF;
                thruster.CurrentOutput = quantized / 255f;
            }
        }

        private static ThrusterBlock FindThruster(int playerId, int guidHash)
        {
            foreach (ShipState ship in SpaceCombatRegistry.Ships)
            {
                for (int i = 0; i < ship.Thrusters.Count; i++)
                {
                    ThrusterBlock thruster = ship.Thrusters[i];
                    if (thruster != null && thruster.PlayerID == playerId && thruster.GuidHash == guidHash)
                    {
                        return thruster;
                    }
                }
            }
            return null;
        }
    }
}
