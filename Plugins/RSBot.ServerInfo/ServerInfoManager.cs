using System.Collections.Generic;
using RSBot.General.Components;
using RSBot.General.Models;

namespace RSBot.ServerInfo
{
    public class ServerInfoManager
    {
        public static List<Server> GetServers()
        {
            return Serverlist.Servers;
        }
    }
}
