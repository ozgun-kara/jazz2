﻿using Duality;
using Jazz2.Actors;
using Lidgren.Network;

namespace Jazz2.Networking.Packets.Server
{
    public struct CreateRemotePlayer : IServerPacket
    {
        public NetConnection SenderConnection { get; set; }

        byte IServerPacket.Type => 13;


        public byte Index;
        public PlayerType Type;
        public Vector3 Pos;

        void IServerPacket.Read(NetIncomingMessage msg)
        {
            Index = msg.ReadByte();

            Type = (PlayerType)msg.ReadByte();

            float x = msg.ReadUInt16();
            float y = msg.ReadUInt16();
            float z = msg.ReadUInt16();
            Pos = new Vector3(x, y, z);
        }

        void IServerPacket.Write(NetOutgoingMessage msg)
        {
            msg.Write((byte)Index);

            msg.Write((byte)Type);

            msg.Write((ushort)Pos.X);
            msg.Write((ushort)Pos.Y);
            msg.Write((ushort)Pos.Z);
        }
    }
}