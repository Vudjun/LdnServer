using LanPlayServer.Network;
using LanPlayServer.Network.Types;
using LanPlayServer.Utils;
using NetCoreServer;
using Ryujinx.HLE.HOS.Services.Ldn.Types;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LanPlayServer
{
    public class LdnSession : TcpSession
    {
        private const int ExternalProxyTimeout = 2;

        public HostedGame   CurrentGame { get; set; }
        public Array6<byte> MacAddress  { get; private set; }
        public uint         IpAddress   { get; private set; }
        public uint         RealIpAddress { get; private set; }
        public string       Passphrase  { get; private set; } = "";

        public string     StringId => Id.ToString().Replace("-", "");

        private LdnServer      _tcpServer;
        private RyuLdnProtocol _protocol;
        private NetworkInfo[]  _scanBuffer = new NetworkInfo[1];

        private long _lastMessageTicks = Stopwatch.GetTimestamp();
        private int _waitingPingID = -1;
        private byte _pingId = 0;

        /// <summary>
        /// Node ID when in a game. This does not change while the user is still in that game.
        /// </summary>
        public int NodeId { get; set; }

        private bool _initialized = false;
        private bool _disconnected = false;
        private object _connectionLock = new();

        private bool _connected = false;

        public LdnSession(LdnServer server) : base(server)
        {
            _tcpServer = server;

            MacAddress = new Array6<byte>();

            Random.Shared.NextBytes(MacAddress.AsSpan());

            _protocol = new RyuLdnProtocol();

            _protocol.Initialize               += HandleInitialize;
            _protocol.Passphrase               += HandlePassphrase;
            _protocol.CreateAccessPoint        += HandleCreateAccessPoint;
            _protocol.CreateAccessPointPrivate += HandleCreateAccessPointPrivate;
            _protocol.Reject                   += HandleReject;
            _protocol.SetAcceptPolicy          += HandleSetAcceptPolicy;
            _protocol.SetAdvertiseData         += HandleSetAdvertiseData;
            _protocol.Scan                     += HandleScan;
            _protocol.Connect                  += HandleConnect;
            _protocol.ConnectPrivate           += HandleConnectPrivate;
            _protocol.Disconnected             += HandleDisconnect;

            _protocol.ProxyConnect      += HandleProxyConnect;
            _protocol.ProxyConnectReply += HandleProxyConnectReply;
            _protocol.ProxyData         += HandleProxyData;
            _protocol.ProxyDisconnect   += HandleProxyDisconnect;

            _protocol.ExternalProxyState += HandleExternalProxyState;
            _protocol.Ping               += HandlePing;

            //_protocol.Any += HandleAny;
        }

        private void HandleAny(LdnHeader obj)
        {
            Console.WriteLine($"  ({PrintIp()}) -> {(PacketId)obj.Type}");
        }

        private string PrintIp()
        {
            return $"{RealIpAddress >> 24}.{(RealIpAddress >> 16) & 0xFF}.{(RealIpAddress >> 8) & 0xFF}.{RealIpAddress & 0xFF}";
        }

        public void Ping()
        {
            if (_waitingPingID != -1)
            {
                // The last ping was not responded to. Force a disconnect (async).
                Console.WriteLine($"Closing session with Id {Id} due to idle.");
                Task.Run(Disconnect);
            }
            else
            {
                long ticks      = Stopwatch.GetTimestamp();
                long deltaTicks = ticks - _lastMessageTicks;
                long deltaMs    = deltaTicks / (Stopwatch.Frequency / 1000);

                if (deltaMs > LdnServer.InactivityPingFrequency)
                {
                    byte pingId = _pingId++;

                    _waitingPingID = pingId;

                    SendAsync(RyuLdnProtocol.Encode(PacketId.Ping, new PingMessage { Id = pingId, Requester = 0 }));
                }
            }
        }

        private void DisconnectFromGame()
        {
            HostedGame game = CurrentGame;

            game?.Disconnect(this, false);

            if (game?.Owner == this)
            {
                game.Closing = true;
                _tcpServer.CloseGame(game.Id);
            }
        }

        private void HandlePing(LdnHeader header, PingMessage ping)
        {
            if (ping.Requester == 0 && ping.Id == _waitingPingID)
            {
                // A response from this client. Still alive, reset the _waitingPingID. (getting the message will also reset the timer)
                _waitingPingID = -1;
            }
        }

        private void HandleInitialize(LdnHeader header, InitializeMessage message)
        {
            if (_initialized)
            {
                return;
            }

            MacAddress = _tcpServer.MacAddresses.TryFind(Convert.ToHexString(message.Id.AsSpan()), message.MacAddress.AsSpan(), StringId);

            Array16<byte> id = new();
            Convert.FromHexString(StringId).CopyTo(id.AsSpan());

            SendAsync(RyuLdnProtocol.Encode(PacketId.Initialize, new InitializeMessage() { Id = id, MacAddress = MacAddress }));

            _initialized = true;
        }

        private void HandlePassphrase(LdnHeader header, PassphraseMessage message)
        {
            string passphrase = StringUtils.ReadUtf8String(message.Passphrase.AsSpan());
            Regex  match      = new("Ryujinx-[0-9a-f]{8}");
            bool   valid      = passphrase == "" || (passphrase.Length == 16 && match.IsMatch(passphrase));

            Passphrase = valid ? passphrase : "";
        }

        private void HandleDisconnect(LdnHeader header, DisconnectMessage message)
        {
            DisconnectFromGame();
        }

        private void HandleReject(LdnHeader header, RejectRequest reject)
        {
            CurrentGame?.HandleReject(this, header, reject);
        }

        private void HandleSetAcceptPolicy(LdnHeader header, SetAcceptPolicyRequest policy)
        {
            CurrentGame?.HandleSetAcceptPolicy(this, header, policy);
        }

        private void HandleSetAdvertiseData(LdnHeader header, byte[] data)
        {
            CurrentGame?.HandleSetAdvertiseData(this, header, data);
        }

        private void HandleExternalProxyState(LdnHeader header, ExternalProxyConnectionState state)
        {
            CurrentGame?.HandleExternalProxyState(this, header, state);
        }

        private void HandleProxyDisconnect(LdnHeader header, ProxyDisconnectMessage message)
        {
            CurrentGame?.HandleProxyDisconnect(this, header, message);
        }

        private void HandleProxyData(LdnHeader header, ProxyDataHeader message, byte[] data)
        {
            CurrentGame?.HandleProxyData(this, header, message, data);
        }

        private void HandleProxyConnectReply(LdnHeader header, ProxyConnectResponse data)
        {
            CurrentGame?.HandleProxyConnectReply(this, header, data);
        }

        private void HandleProxyConnect(LdnHeader header, ProxyConnectRequest message)
        {
            CurrentGame?.HandleProxyConnect(this, header, message);
        }

        protected override void OnConnected()
        {
            if (!_connected)
            {
                try
                {
                    RealIpAddress = GetSessionIp();
                }
                catch
                {
                    Console.WriteLine("IP unavailable!");
                    // Already disconnected?
                }

                Console.WriteLine($"LDN TCP session with Id {Id} connected! ({PrintIp()})");

                _connected = true;
            }
        }

        protected override void OnDisconnected()
        {
            Task.Run(() =>
            {
                lock (_connectionLock)
                {
                    _disconnected = true;
                    DisconnectFromGame();
                }

                Console.WriteLine($"LDN TCP session with Id {Id} disconnected! ({PrintIp()})");

                _protocol.Dispose();
            });
        }

        protected override void OnReceived(byte[] buffer, long offset, long size)
        {
            try
            {
                OnConnected();

                _protocol.Read(buffer, (int)offset, (int)size);

                _lastMessageTicks = Stopwatch.GetTimestamp();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Closing session with Id {Id} due to exception: {e}");

                Disconnect();
            }
        }

        protected override void OnError(SocketError error)
        {
            Console.WriteLine($"LDN TCP session caught an error with code {error}");
        }

        private uint GetSessionIp()
        {
            IPAddress remoteIp = ((IPEndPoint)Socket.RemoteEndPoint).Address;
            byte[]    bytes    = remoteIp.GetAddressBytes();

            Array.Reverse(bytes);

            return BitConverter.ToUInt32(bytes);
        }

        public bool SetIpV4(uint ip, uint subnet, bool internalProxy)
        {
            if (_tcpServer.UseProxy)
            {
                IpAddress = ip;

                if (internalProxy)
                {
                    ProxyConfig config = new()
                    {
                        ProxyIp         = ip,
                        ProxySubnetMask = subnet
                    };

                    // Tell the client about the proxy configuration.
                    SendAsync(RyuLdnProtocol.Encode(PacketId.ProxyConfig, config));
                }

                return true;
            }
            else
            {
                return false;
            }
        }

        private void HandleScan(LdnHeader ldnPacket, ScanFilter filter)
        {
            Thread.Sleep(200);
            int games = _tcpServer.Scan(ref _scanBuffer, filter, Passphrase, CurrentGame);

            for (int i = 0; i < games; i++)
            {
                NetworkInfo info = _scanBuffer[i];

                SendAsync(RyuLdnProtocol.Encode(PacketId.ScanReply, info));
            }

            SendAsync(RyuLdnProtocol.Encode(PacketId.ScanReplyEnd));
        }

        private void HandleCreateAccessPoint(LdnHeader ldnPacket, CreateAccessPointRequest request, byte[] advertiseData)
        {
            if (CurrentGame != null || !_initialized)
            {
                // Cannot create an access point while in a game.
                SendAsync(RyuLdnProtocol.Encode(PacketId.NetworkError, new NetworkErrorMessage { Error = NetworkError.Unknown }));

                return;
            }

            string id = Guid.NewGuid().ToString().Replace("-", "");

            AddressList dhcpConfig = new();

            AccessPointConfigToNetworkInfo(id, request.NetworkConfig, request.UserConfig, request.RyuNetworkConfig, request.SecurityConfig, dhcpConfig, advertiseData);
        }

        private void HandleCreateAccessPointPrivate(LdnHeader ldnPacket, CreateAccessPointPrivateRequest request, byte[] advertiseData)
        {
            if (CurrentGame != null || !_initialized)
            {
                // Cannot create an access point while in a game.
                SendAsync(RyuLdnProtocol.Encode(PacketId.NetworkError, new NetworkErrorMessage { Error = NetworkError.Unknown }));

                return;
            }

            string id = Convert.ToHexString(request.SecurityParameter.SessionId.AsSpan());

            AccessPointConfigToNetworkInfo(id, request.NetworkConfig, request.UserConfig, request.RyuNetworkConfig, request.SecurityConfig, request.AddressList, advertiseData);
        }

        private void AccessPointConfigToNetworkInfo(string id, NetworkConfig networkConfig, UserConfig userConfig, RyuNetworkConfig ryuNetworkConfig, SecurityConfig securityConfig, AddressList dhcpConfig, byte[] advertiseData)
        {
            string userId = StringId;

            Array16<byte> sessionID = new();
            Convert.FromHexString(id).CopyTo(sessionID.AsSpan());

            NetworkInfo networkInfo = new()
            {
                NetworkId = new NetworkId()
                {
                    IntentId = new IntentId()
                    {
                        LocalCommunicationId = networkConfig.IntentId.LocalCommunicationId,
                        SceneId              = networkConfig.IntentId.SceneId
                    },
                    SessionId = sessionID
                },
                Common = new CommonNetworkInfo()
                {
                    Channel     = networkConfig.Channel,
                    LinkLevel   = 3,
                    NetworkType = 2,
                    MacAddress  = MacAddress,
                    Ssid        = new Ssid()
                    {
                        Length = 32,
                    }
                },
                Ldn = new LdnNetworkInfo()
                {
                    SecurityMode      = (ushort)securityConfig.SecurityMode,
                    NodeCountMax      = networkConfig.NodeCountMax,
                    NodeCount         = 0,
                    AdvertiseDataSize = (ushort)advertiseData.Length,
                    AuthenticationId  = 0
                }
            };

            "12345678123456781234567812345678"u8.ToArray().CopyTo(networkInfo.Common.Ssid.Name.AsSpan());
            advertiseData.CopyTo(networkInfo.Ldn.AdvertiseData.AsSpan());

            NodeInfo myInfo = new()
            {
                Ipv4Address               = IpAddress,
                MacAddress                = MacAddress,
                NodeId                    = 0x00,
                IsConnected               = 0x01,
                UserName                  = userConfig.UserName,
                LocalCommunicationVersion = networkConfig.LocalCommunicationVersion,
            };

            for (int i = 0; i < 8; i++)
            {
                networkInfo.Ldn.Nodes[i] = new NodeInfo();
            }

            if (ryuNetworkConfig.ExternalProxyPort != 0 && !IsProxyReachable(ryuNetworkConfig.ExternalProxyPort))
            {
                ryuNetworkConfig.ExternalProxyPort = 0;
                SendAsync(RyuLdnProtocol.Encode(PacketId.NetworkError, new NetworkErrorMessage { Error = NetworkError.PortUnreachable }));
            }

            /*
            if (networkInfo.NetworkId.IntentId.LocalCommunicationId == 0x0100abf008968000ul)
            {
                SendAsync(_protocol.Encode(PacketId.NetworkError, new NetworkErrorMessage { Error = NetworkError.Unknown }));
                return;
            }
            */

            HostedGame game = _tcpServer.CreateGame(id, networkInfo, dhcpConfig, userId);

            if (game == null)
            {
                SendAsync(RyuLdnProtocol.Encode(PacketId.NetworkError, new NetworkErrorMessage { Error = NetworkError.Unknown }));
                return;
            }

            lock (_connectionLock)
            {
                if (_disconnected)
                {
                    Console.WriteLine($"Emergency disconnect: {id}");
                    game = null;
                }

                game?.SetOwner(this, ryuNetworkConfig);
                game?.Connect(this, myInfo);
            }

            if (game == null)
            {
                Console.WriteLine($"Null close: {id}");
                _tcpServer.CloseGame(id);
            }
        }

        private bool IsProxyReachable(ushort port)
        {
            // Attempt to establish a connection to the p2p server owned by the host.
            // We don't need to send anything, just establish a TCP connection.
            // If that is not possible, then their external proxy isn't reachable from the internet.

            IPEndPoint searchEndpoint;
            try
            {
                searchEndpoint = Socket.RemoteEndPoint as IPEndPoint;
            }
            catch
            {
                return false;
            }

            IPEndPoint ep = new(searchEndpoint.Address, port);

            NetCoreServer.TcpClient client = new(ep);
            client.ConnectAsync();

            long endTime = Stopwatch.GetTimestamp() + Stopwatch.Frequency * ExternalProxyTimeout;

            while (Stopwatch.GetTimestamp() < endTime)
            {
                if (client.IsConnected)
                {
                    client.Dispose();

                    return true;
                }

                Thread.Sleep(1);
            }

            client.Dispose();

            return false;
        }

        private void ConnectImpl(string id, UserConfig userConfig, uint localCommunicationVersion)
        {
            HostedGame game = _tcpServer.FindGame(id);

            if (game != null)
            {
                NetworkInfo gameInfo = game.Info;

                // Node 0 will contain the expected version (the host). If there is no match, we cannot connect.
                uint hostVersion   = gameInfo.Ldn.Nodes[0].LocalCommunicationVersion;
                uint clientVersion = localCommunicationVersion;

                if (clientVersion > hostVersion)
                {
                    SendAsync(RyuLdnProtocol.Encode(PacketId.NetworkError, new NetworkErrorMessage { Error = NetworkError.VersionTooHigh }));

                    return;
                }
                else if (clientVersion < hostVersion)
                {
                    SendAsync(RyuLdnProtocol.Encode(PacketId.NetworkError, new NetworkErrorMessage { Error = NetworkError.VersionTooLow }));

                    return;
                }

                NodeInfo myNode = new()
                {
                    Ipv4Address               = IpAddress,
                    MacAddress                = MacAddress,
                    NodeId                    = 0, // Will be populated on insert.
                    IsConnected               = 0x01,
                    UserName                  = userConfig.UserName,
                    LocalCommunicationVersion = (ushort)localCommunicationVersion
                };

                bool result = game.Connect(this, myNode);

                if (!result)
                {
                    // There wasn't enough room in the game.

                    SendAsync(RyuLdnProtocol.Encode(PacketId.NetworkError, new NetworkErrorMessage { Error = NetworkError.TooManyPlayers }));
                }
            }
            else
            {
                SendAsync(RyuLdnProtocol.Encode(PacketId.NetworkError, new NetworkErrorMessage { Error = NetworkError.ConnectNotFound }));
            }
        }

        private void HandleConnect(LdnHeader ldnPacket, ConnectRequest request)
        {
            SecurityConfig securityConfig            = request.SecurityConfig;
            UserConfig     userConfig                = request.UserConfig;
            uint           localCommunicationVersion = request.LocalCommunicationVersion;
            uint           optionUnknown             = request.OptionUnknown;
            NetworkInfo    networkInfo               = request.NetworkInfo;

            if (!_initialized)
            {
                SendAsync(RyuLdnProtocol.Encode(PacketId.NetworkError, new NetworkErrorMessage { Error = NetworkError.ConnectFailure }));

                return;
            }

            string id = Convert.ToHexString(networkInfo.NetworkId.SessionId.AsSpan());

            ConnectImpl(id, userConfig, localCommunicationVersion);
        }

        private void HandleConnectPrivate(LdnHeader ldnPacket, ConnectPrivateRequest request)
        {
            SecurityConfig securityConfig = request.SecurityConfig;
            UserConfig userConfig = request.UserConfig;
            uint localCommunicationVersion = request.LocalCommunicationVersion;
            uint optionUnknown = request.OptionUnknown;

            if (!_initialized)
            {
                SendAsync(RyuLdnProtocol.Encode(PacketId.NetworkError, new NetworkErrorMessage { Error = NetworkError.ConnectFailure }));

                return;
            }

            string id = Convert.ToHexString(request.SecurityParameter.SessionId.AsSpan());

            ConnectImpl(id, userConfig, localCommunicationVersion);
        }
    }
}