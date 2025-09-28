using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;

class NatPunchServer : INetEventListener, INatPunchListener
{
    private NetManager _server;
    private readonly int _port;

    // Store room info: room token -> list of endpoints with timestamps
    private Dictionary<string, List<RoomMember>> _roomMembers =
        new Dictionary<string, List<RoomMember>>();

    private class RoomMember
    {
        public System.Net.IPEndPoint EndPoint { get; set; }
        public DateTime LastSeen { get; set; }
        public bool IsHost { get; set; }
    }

    public NatPunchServer()
    {
        var portStr = Environment.GetEnvironmentVariable("PORT") ?? "50000";
        _port = int.Parse(portStr);
    }

    public void Run()
    {
        try
        {
            _server = new NetManager(this);
            _server.NatPunchEnabled = true;
            _server.Start(_port);

            Console.WriteLine($"NAT Punch server started on port {_port}");
            Console.WriteLine("Railway deployment successful!");

            while (true)
            {
                _server.PollEvents();
                CleanupOldMembers();
                System.Threading.Thread.Sleep(15);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Server error: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private void CleanupOldMembers()
    {
        var cutoff = DateTime.Now.AddMinutes(-5); // Remove members older than 5 minutes

        foreach (var room in _roomMembers.ToList())
        {
            room.Value.RemoveAll(m => m.LastSeen < cutoff);
            if (room.Value.Count == 0)
            {
                _roomMembers.Remove(room.Key);
                Console.WriteLine($"Cleaned up empty room: {room.Key}");
            }
        }
    }

    // --- INetEventListener ---
    public void OnPeerConnected(NetPeer peer)
    {
        Console.WriteLine($"Peer connected: {peer.EndPoint}");
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        Console.WriteLine($"Peer disconnected: {peer.EndPoint}, Reason: {info.Reason}");
    }

    public void OnNetworkError(System.Net.IPEndPoint ep, System.Net.Sockets.SocketError error)
    {
        Console.WriteLine($"Network error from {ep}: {error}");
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        Console.WriteLine($"Unexpected game data received from {peer.EndPoint}");
        reader.Recycle();
    }

    public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        Console.WriteLine($"Unconnected message from {remoteEndPoint}, Type: {messageType}");
        reader.Recycle();
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        Console.WriteLine($"Connection request from {request.RemoteEndPoint} - Rejecting (punch server only)");
        request.Reject();
    }

    // --- INatPunchListener ---
    public void OnNatIntroductionRequest(System.Net.IPEndPoint localEndPoint, System.Net.IPEndPoint remoteEndPoint, string token)
    {
        Console.WriteLine($"=== NAT Introduction Request ===");
        Console.WriteLine($"  Local: {localEndPoint}");
        Console.WriteLine($"  Remote: {remoteEndPoint}");
        Console.WriteLine($"  Token: {token}");

        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine($"  ERROR: Empty token - rejecting request");
            return;
        }

        // Initialize room if it doesn't exist
        if (!_roomMembers.ContainsKey(token))
        {
            _roomMembers[token] = new List<RoomMember>();
            Console.WriteLine($"  Created new room: {token}");
        }

        var room = _roomMembers[token];

        // Find or add this member
        var existingMember = room.FirstOrDefault(m => m.EndPoint.Equals(remoteEndPoint));
        if (existingMember != null)
        {
            existingMember.LastSeen = DateTime.Now;
            Console.WriteLine($"  Updated existing member: {remoteEndPoint}");
        }
        else
        {
            // First member in room becomes host
            bool isHost = room.Count == 0;

            room.Add(new RoomMember
            {
                EndPoint = remoteEndPoint,
                LastSeen = DateTime.Now,
                IsHost = isHost
            });

            Console.WriteLine($"  Added new member: {remoteEndPoint} (Role: {(isHost ? "HOST" : "CLIENT")})");
        }

        Console.WriteLine($"  Room '{token}' members ({room.Count} total):");
        foreach (var member in room)
        {
            Console.WriteLine($"    - {member.EndPoint} ({(member.IsHost ? "HOST" : "CLIENT")})");
        }

        // If we have both host and client, facilitate introduction
        if (room.Count >= 2)
        {
            var host = room.FirstOrDefault(m => m.IsHost);
            var clients = room.Where(m => !m.IsHost).ToList();

            if (host != null && clients.Any())
            {
                Console.WriteLine($"  Facilitating introductions for room '{token}':");

                foreach (var client in clients)
                {
                    // Introduce client to host
                    Console.WriteLine($"    Introducing CLIENT {client.EndPoint} to HOST {host.EndPoint}");
                    _server.NatPunchModule.NatIntroduce(
                        host.EndPoint,      // host internal
                        host.EndPoint,      // host external  
                        client.EndPoint,    // client internal
                        client.EndPoint,    // client external
                        token               // room token
                    );
                }
            }
        }
        else
        {
            Console.WriteLine($"  Waiting for more members (need at least 2, have {room.Count})");
        }

        Console.WriteLine($"================================");
    }

    public void OnNatIntroductionSuccess(System.Net.IPEndPoint targetEndPoint, NatAddressType type, string token)
    {
        Console.WriteLine($"=== NAT Introduction SUCCESS ===");
        Console.WriteLine($"  Target: {targetEndPoint}");
        Console.WriteLine($"  Type: {type}");
        Console.WriteLine($"  Token: {token}");
        Console.WriteLine($"================================");
    }

    public static void Main(string[] args)
    {
        Console.WriteLine("Starting Railway NAT Punch Server...");
        new NatPunchServer().Run();
    }
}