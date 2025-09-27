using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;

class NatPunchServer : INetEventListener, INatPunchListener
{
    private NetManager _server;
    private readonly int _port;

    private Dictionary<string, List<System.Net.IPEndPoint>> _roomEndpoints =
        new Dictionary<string, List<System.Net.IPEndPoint>>();

    public NatPunchServer()
    {
        // Railway sets PORT environment variable
        var portStr = Environment.GetEnvironmentVariable("PORT") ?? "50000";
        _port = int.Parse(portStr);
    }

    public void Run()
    {
        try
        {
            _server = new NetManager(this);
            _server.NatPunchEnabled = true;
            // NatPunchListener is automatically set when implementing INatPunchListener

            _server.Start(_port);
            Console.WriteLine($"NAT Punch server started on port {_port}");
            Console.WriteLine("Railway deployment successful!");

            while (true)
            {
                _server.PollEvents();
                System.Threading.Thread.Sleep(15);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Server error: {ex.Message}");
            Environment.Exit(1);
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
        Console.WriteLine($"Connection request from {request.RemoteEndPoint} - Rejecting (punch server)");
        request.Reject();
    }

    // --- INatPunchListener ---
    public void OnNatIntroductionRequest(System.Net.IPEndPoint localEndPoint, System.Net.IPEndPoint remoteEndPoint, string token)
    {
        Console.WriteLine($"NAT Introduction Request:");
        Console.WriteLine($"  Local: {localEndPoint}");
        Console.WriteLine($"  Remote: {remoteEndPoint}");
        Console.WriteLine($"  Token: {token}");

        if (!_roomEndpoints.ContainsKey(token))
        {
            _roomEndpoints[token] = new List<System.Net.IPEndPoint>();
        }

        if (!_roomEndpoints[token].Contains(remoteEndPoint))
        {
            _roomEndpoints[token].Add(remoteEndPoint);
            Console.WriteLine($"  Added to room '{token}' (Total in room: {_roomEndpoints[token].Count})");
        }

        Console.WriteLine($"  Current endpoints in room '{token}':");
        foreach (var ep in _roomEndpoints[token])
        {
            Console.WriteLine($"    - {ep}");
        }
    }

    public void OnNatIntroductionSuccess(System.Net.IPEndPoint targetEndPoint, NatAddressType type, string token)
    {
        Console.WriteLine($"NAT Introduction SUCCESS:");
        Console.WriteLine($"  Target: {targetEndPoint}");
        Console.WriteLine($"  Type: {type}");
        Console.WriteLine($"  Token: {token}");
    }

    public static void Main(string[] args)
    {
        Console.WriteLine("Starting Railway NAT Punch Server...");
        new NatPunchServer().Run();
    }
}