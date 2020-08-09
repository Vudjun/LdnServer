﻿using LanPlayServer.Network;
using LanPlayServer.Network.Types;
using NetCoreServer;
using Ryujinx.HLE.HOS.Services.Ldn.Types;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LanPlayServer
{
    class LdnSession : TcpSession
    {
        LdnServer _tcpServer;

        public HostedGame CurrentGame { get; set; }
        public byte[] MacAddress { get; private set; }
        public uint IpAddress { get; private set; }
        public string Passphrase { get; private set; } = "";

        private RyuLdnProtocol _protocol;

        private NetworkInfo[] _scanBuffer = new NetworkInfo[1];

        public LdnSession(LdnServer server) : base(server)
        {
            _tcpServer = server;

            MacAddress = new byte[6];
            new Random().NextBytes(MacAddress);

            _protocol = new RyuLdnProtocol("91ac8b112e1d4536a73c49f8eb9cb064");

            _protocol.Passphrase += HandlePassphrase;
            _protocol.CreateAccessPoint += HandleCreateAccessPoint;
            _protocol.SetAcceptPolicy += HandleSetAcceptPolicy;
            _protocol.SetAdvertiseData += HandleSetAdvertiseData;
            _protocol.Scan += HandleScan;
            _protocol.Connect += HandleConnect;
            _protocol.Disconnected += HandleDisconnect;

            _protocol.ProxyConnect += HandleProxyConnect;
            _protocol.ProxyConnectReply += HandleProxyConnectReply;
            _protocol.ProxyData += HandleProxyData;
            _protocol.ProxyDisconnect += HandleProxyDisconnect;

            _protocol.ExternalProxyState += HandleExternalProxyState;
        }

        private void DisconnectFromGame()
        {
            HostedGame game = CurrentGame;

            game?.Disconnect(this, false);

            if (game?.Id == Id.ToString().Replace("-", ""))
            {
                _tcpServer.CloseGame(game.Id);
            }
        }

        private void HandlePassphrase(LdnHeader header, PassphraseMessage message)
        {
            Passphrase = StringUtils.ReadUtf8String(message.Passphrase);
        }

        private void HandleDisconnect(LdnHeader header, DisconnectMessage message)
        {
            DisconnectFromGame();
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
            IpAddress = GetSessionIp();

            Console.WriteLine($"LDN TCP session with Id {Id} connected!"); ;
        }

        protected override void OnDisconnected()
        {
            DisconnectFromGame();

            Console.WriteLine($"LDN TCP session with Id {Id} disconnected!");
        }

        protected override void OnReceived(byte[] buffer, long offset, long size)
        {
            try
            {
                _protocol.Read(buffer, (int)offset, (int)size);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Closing session with Id {Id} due to exception: {e.ToString()}");
                Disconnect();
            }
        }

        protected override void OnError(SocketError error)
        {
            Console.WriteLine($"LDN TCP session caught an error with code {error}");
        }

        private uint GetSessionIp()
        {
            var remoteIp = ((IPEndPoint)Socket.RemoteEndPoint).Address;
            var bytes = remoteIp.GetAddressBytes();
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
                    ProxyConfig config = new ProxyConfig
                    {
                        ProxyIp = ip,
                        ProxySubnetMask = subnet
                    };

                    // Tell the client about the proxy configuration.
                    SendAsync(_protocol.Encode(PacketId.ProxyConfig, config));
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
            int games = _tcpServer.Scan(ref _scanBuffer, filter, Passphrase);

            for (int i = 0; i < games; i++)
            {
                NetworkInfo info = _scanBuffer[i];

                SendAsync(_protocol.Encode(PacketId.ScanReply, info));
            }

            SendAsync(_protocol.Encode(PacketId.ScanReplyEnd));
        }

        private void HandleCreateAccessPoint(LdnHeader ldnPacket, CreateAccessPointRequest request, byte[] advertiseData)
        {
            if (CurrentGame != null)
            {
                // Cannot create an access point while in a game.
                return;
            }

            AccessPointConfigToNetworkInfo(request, advertiseData);
        }

        private void AccessPointConfigToNetworkInfo(CreateAccessPointRequest request, byte[] advertiseData)
        {
            string id = Id.ToString().Replace("-", "");

            NetworkInfo networkInfo = new NetworkInfo()
            {
                NetworkId = new NetworkId()
                {
                    IntentId = new IntentId()
                    {
                        LocalCommunicationId = request.NetworkConfig.IntentId.LocalCommunicationId,
                        SceneId = request.NetworkConfig.IntentId.SceneId
                    },
                    SessionId = LdnHelper.StringToByteArray(id)
                },
                Common = new CommonNetworkInfo()
                {
                    Channel = 0,
                    LinkLevel = 3,
                    NetworkType = 2,
                    Bssid = new byte[6],
                    Ssid = new Ssid()
                    {
                        Length = 32,
                        Name = Encoding.ASCII.GetBytes("12345678123456781234567812345678")
                    }
                },
                Ldn = new LdnNetworkInfo()
                {
                    SecurityMode = 0,
                    UnknownRandom = new byte[0x10],
                    NodeCountMax = request.NetworkConfig.NodeCountMax,
                    NodeCount = 0,
                    Nodes = new NodeInfo[8],
                    AdvertiseDataSize = (ushort)advertiseData.Length,
                    AdvertiseData = advertiseData,
                    Unknown = new byte[0x94]
                }
            };

            Array.Resize(ref networkInfo.Common.Ssid.Name, 0x21);
            Array.Resize(ref networkInfo.Ldn.AdvertiseData, 0x180);

            NodeInfo myInfo = new NodeInfo()
            {
                Ipv4Address = IpAddress,
                MacAddress = MacAddress,
                NodeId = 0x00,
                IsConnected = 0x01,
                UserName = request.UserConfig.UserName,
                LocalCommunicationVersion = request.NetworkConfig.LocalCommunicationVersion,
                Unknown2 = new byte[0x10]
            };

            for (int i = 0; i < 8; i++)
            {
                networkInfo.Ldn.Nodes[i] = new NodeInfo()
                {
                    MacAddress = new byte[6],
                    UserName = new byte[0x21],
                    Unknown2 = new byte[0x10]
                };
            }

            HostedGame game = _tcpServer.CreateGame(id, networkInfo);

            game.SetOwner(this, request);
            game.Connect(this, myInfo);
        }

        private void HandleConnect(LdnHeader ldnPacket, ConnectRequest request)
        {
            ConnectNetworkData connectNetworkData = request.Data;
            NetworkInfo networkInfo = request.Info;

            string id = LdnHelper.ByteArrayToString(networkInfo.NetworkId.SessionId);

            HostedGame game = _tcpServer.FindGame(id);

            if (game != null && game.Players < 8)
            {
                NodeInfo myNode = new NodeInfo
                {
                    Ipv4Address = IpAddress,
                    MacAddress = MacAddress,
                    NodeId = 0, // Will be populated on insert.
                    IsConnected = 0x01,
                    UserName = connectNetworkData.UserConfig.UserName,
                    LocalCommunicationVersion = (ushort)connectNetworkData.LocalCommunicationVersion
                };

                game.Connect(this, myNode);
            }
        }
    }
}
