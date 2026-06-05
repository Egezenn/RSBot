using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RSBot.Core.Components;
using RSBot.Core.Objects.Spawn;

namespace RSBot.Core.Network.Handler.Agent.Guild
{
    internal class GuildEntityUpdateResponse : IPacketHandler
    {
        public ushort Opcode => 0x30FF;

        public PacketDestination Destination => PacketDestination.Client;

        public void Invoke(Packet packet)
        {
            uint entityId = packet.ReadUInt();
            packet.ReadUInt(); //guildId
            string guildName = packet.ReadString();
            packet.ReadString(); //grandName
            packet.ReadUInt(); //guild emblem
            packet.ReadUInt(); //unionId
            packet.ReadUInt(); //union emblem
            packet.ReadByte(); //isFriendly
            packet.ReadByte(); //memberSiegeAuthority

            if (SpawnManager.TryGetEntityIncludingMe(entityId, out SpawnedEntity entity))
                entity.GuildName = guildName;
        }
    }
}
