using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;

class NatPunchServer : INetEventListener, INatPunchListener
{
    private NetManager _server;
    private readonly int _port;
    private TcpListener _tcpListener;

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
        // Railway sets PORT environment variable, fallback to 8000 for local testing
        var portStr = Environment.GetEnvironmentVariable("PORT") ?? "8000";
        _port = int.Parse(portStr);
    }

    public void Run()
    {
        try
        {
            // Start TCP health check server first
            StartTcpHealthServer();

            // Start UDP NAT punch server
            _server = new NetManager(this);
            _server.NatPunchEnabled = true;
            _server.UnconnectedMessagesEnabled = true; // Important for NAT punch
            _server.Start(_port);

            Console.WriteLine($"===============================");
            Console.WriteLine($"NAT Punch Server STARTED");
            Console.WriteLine($"UDP Port: {_port} (NAT punch)");
            Console.WriteLine($"TCP Port: {_port} (health checks)");
            Console.WriteLine($"Railway deployment successful!");
            Console.WriteLine($"===============================");

            while (true)
            {
                _server.PollEvents();
                CleanupOldMembers();
                System.Threading.Thread.Sleep(15);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL SERVER ERROR: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            Environment.Exit(1);
        }
    }

    private void StartTcpHealthServer()
    {
        try
        {
            _tcpListener = new TcpListener(IPAddress.Any, _port);
            _tcpListener.Start();

            Console.WriteLine($"TCP health server started on port {_port}");

            // Handle TCP connections asynchronously
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        var tcpClient = await _tcpListener.AcceptTcpClientAsync();

                        // Handle the HTTP request in a separate task
                        _ = Task.Run(() => HandleHttpRequest(tcpClient));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"TCP server error: {ex.Message}");
                        // Continue running even if there are TCP errors
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start TCP health server: {ex.Message}");
            throw;
        }
    }

    private void HandleHttpRequest(TcpClient tcpClient)
    {
        try
        {
            using (tcpClient)
            {
                var stream = tcpClient.GetStream();

                // Read the HTTP request (we don't need to parse it fully)
                byte[] buffer = new byte[1024];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);

                // Create a simple HTTP response
                var response = "HTTP/1.1 200 OK\r\n" +
                             "Content-Type: text/plain\r\n" +
                             "Content-Length: 47\r\n" +
                             "\r\n" +
                             "NAT Punch Server Running - UDP Ready for Clients";

                byte[] responseBytes = System.Text.Encoding.UTF8.GetBytes(response);
                stream.Write(responseBytes, 0, responseBytes.Length);
                stream.Flush();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HTTP request handling error: {ex.Message}");
        }
    }

    private void CleanupOldMembers()
    {
        var cutoff = DateTime.Now.AddMinutes(-5); // Remove members older than 5 minutes
        var roomsToRemove = new List<string>();

        foreach (var room in _roomMembers.ToList())
        {
            room.Value.RemoveAll(m => m.LastSeen < cutoff);
            if (room.Value.Count == 0)
            {
                roomsToRemove.Add(room.Key);
            }
        }

        foreach (var roomKey in roomsToRemove)
        {
            _roomMembers.Remove(roomKey);
            Console.WriteLine($"[CLEANUP] Removed empty room: {roomKey}");
        }
    }

    // --- INetEventListener ---
    public void OnPeerConnected(NetPeer peer)
    {
        Console.WriteLine($"[PEER] Connected: {peer.EndPoint}");
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        Console.WriteLine($"[PEER] Disconnected: {peer.EndPoint}, Reason: {info.Reason}");
    }

    public void OnNetworkError(System.Net.IPEndPoint ep, System.Net.Sockets.SocketError error)
    {
        Console.WriteLine($"[ERROR] Network error from {ep}: {error}");
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        Console.WriteLine($"[WARNING] Unexpected game data received from {peer.EndPoint}");
        reader.Recycle();
    }

    public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        Console.WriteLine($"[UNCONNECTED] Message from {remoteEndPoint}, Type: {messageType}");
        reader.Recycle();
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        Console.WriteLine($"[CONNECTION] Request from {request.RemoteEndPoint} - REJECTING (NAT punch server only)");
        request.Reject();
    }

    // --- INatPunchListener ---
    public void OnNatIntroductionRequest(System.Net.IPEndPoint localEndPoint, System.Net.IPEndPoint remoteEndPoint, string token)
    {
        Console.WriteLine($"");
        Console.WriteLine($"=== NAT INTRODUCTION REQUEST ===");
        Console.WriteLine($"  Time: {DateTime.Now}");
        Console.WriteLine($"  Local: {localEndPoint}");
        Console.WriteLine($"  Remote: {remoteEndPoint}");
        Console.WriteLine($"  Token: '{token}' (Length: {token?.Length ?? 0})");

        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine($"  ERROR: Empty token - REJECTING request");
            Console.WriteLine($"================================");
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

        Console.WriteLine($"  Room '{token}' current members ({room.Count} total):");
        foreach (var member in room)
        {
            Console.WriteLine($"    - {member.EndPoint} ({(member.IsHost ? "HOST" : "CLIENT")}) [Last seen: {(DateTime.Now - member.LastSeen).TotalSeconds:F1}s ago]");
        }

        // If we have both host and client, facilitate introduction
        if (room.Count >= 2)
        {
            var host = room.FirstOrDefault(m => m.IsHost);
            var clients = room.Where(m => !m.IsHost).ToList();

            if (host != null && clients.Any())
            {
                Console.WriteLine($"  FACILITATING INTRODUCTIONS for room '{token}':");

                foreach (var client in clients)
                {
                    Console.WriteLine($"    Introducing CLIENT {client.EndPoint} <-> HOST {host.EndPoint}");

                    try
                    {
                        // Introduce client to host and vice versa
                        _server.NatPunchModule.NatIntroduce(
                            host.EndPoint,      // host internal
                            host.EndPoint,      // host external  
                            client.EndPoint,    // client internal
                            client.EndPoint,    // client external
                            token               // room token
                        );
                        Console.WriteLine($"    Introduction request sent successfully!");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    ERROR during introduction: {ex.Message}");
                    }
                }
            }
            else
            {
                Console.WriteLine($"  ERROR: Could not find host or clients in room");
            }
        }
        else
        {
            Console.WriteLine($"  Waiting for more members (need at least 2, have {room.Count})");
            Console.WriteLine($"  Current member: {room[0].EndPoint} ({(room[0].IsHost ? "HOST" : "CLIENT")})");
        }

        Console.WriteLine($"================================");
        Console.WriteLine($"");
    }

    public void OnNatIntroductionSuccess(System.Net.IPEndPoint targetEndPoint, NatAddressType type, string token)
    {
        Console.WriteLine($"=== NAT INTRODUCTION SUCCESS ===");
        Console.WriteLine($"  Target: {targetEndPoint}");
        Console.WriteLine($"  Type: {type}");
        Console.WriteLine($"  Token: {token}");
        Console.WriteLine($"  Time: {DateTime.Now}");
        Console.WriteLine($"================================");
    }

    public static void Main(string[] args)
    {
        Console.WriteLine("Starting Railway NAT Punch Server...");
        Console.WriteLine($"Environment PORT: {Environment.GetEnvironmentVariable("PORT")}");
        Console.WriteLine("This server provides both TCP health checks and UDP NAT punch services.");
        new NatPunchServer().Run();
    }
}