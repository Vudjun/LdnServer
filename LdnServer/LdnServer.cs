using LanPlayServer.Stats;
using LanPlayServer.Stats.Types;
using NetCoreServer;
using Ryujinx.HLE.HOS.Services.Ldn.Types;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LanPlayServer
{
    public class LdnServer : TcpServer
    {
        public const int InactivityPingFrequency = 10000;

        private readonly ConcurrentDictionary<string, HostedGame> _hostedGames = new();
        public MacAddressMemory MacAddresses { get; } = new();
        public bool UseProxy => true;

        private readonly CancellationTokenSource _cancel = new();

        public LdnServer(IPAddress address, int port) : base(address, port)
        {
            OptionNoDelay = true;

            Task.Run(BackgroundPingTask);
            Task.Run(BackgroundDumpTask);
        }

        public HostedGame CreateGame(string id, NetworkInfo info, AddressList dhcpConfig, string oldOwnerID)
        {
            id = id.ToLower();
            HostedGame game = new(id, info, dhcpConfig);
            bool idTaken = false;

            _hostedGames.AddOrUpdate(id, game, (id, oldGame) =>
            {
                if (oldGame.OwnerId == oldOwnerID)
                {
                    oldGame.Close();

                    Statistics.RemoveGameAnalytics(oldGame);

                    return game;
                }
                else
                {
                    game.Close();
                    idTaken = true;

                    Console.WriteLine($"id Taken: {id}");
                    return oldGame;
                }
            });

            if (idTaken)
            {
                return null;
            }

            Statistics.AddGameAnalytics(game);

            return game;
        }

        public HostedGame FindGame(string id)
        {
            id = id.ToLower();

            _hostedGames.TryGetValue(id, out HostedGame result);

            return result;
        }

        public HostedGame[] All()
        {
            return _hostedGames.Values.ToArray();
        }

        public int Scan(ref NetworkInfo[] info, ScanFilter filter, string passphrase, HostedGame exclude)
        {
            KeyValuePair<string, HostedGame>[] all = _hostedGames.ToArray();

            int results = 0;

            int gameCount = all.Length;
            int playerCount = 0;

            for (int i = 0; i < all.Length; i++)
            {
                HostedGame game = all[i].Value;

                game.TestReadLock();

                int nPlayers = game.Players;
                playerCount += nPlayers;

                if (game.Passphrase != passphrase || game == exclude)
                {
                    continue;
                }

                NetworkInfo scanInfo = game.Info;

                if (scanInfo.Ldn.StationAcceptPolicy == 1)
                {
                    // Optimization: don't tell anyone about unjoinable networks.

                    continue;
                }

                if (filter.Flag.HasFlag(ScanFilterFlag.LocalCommunicationId))
                {
                    if (scanInfo.NetworkId.IntentId.LocalCommunicationId != filter.NetworkId.IntentId.LocalCommunicationId)
                    {
                        continue;
                    }
                }

                if (filter.Flag.HasFlag(ScanFilterFlag.SceneId))
                {
                    if (scanInfo.NetworkId.IntentId.SceneId != filter.NetworkId.IntentId.SceneId)
                    {
                        continue;
                    }
                }

                if (filter.Flag.HasFlag(ScanFilterFlag.SessionId))
                {
                    if (!scanInfo.NetworkId.SessionId.AsSpan().SequenceEqual(filter.NetworkId.SessionId.AsSpan()))
                    {
                        continue;
                    }
                }

                if (filter.Flag.HasFlag(ScanFilterFlag.Ssid))
                {
                    Span<byte> gameSsid = scanInfo.Common.Ssid.Name.AsSpan()[..scanInfo.Common.Ssid.Length];
                    Span<byte> scanSsid = filter.Ssid.Name.AsSpan()[..filter.Ssid.Length];
                    if (!gameSsid.SequenceEqual(scanSsid))
                    {
                        continue;
                    }
                }

                if (filter.Flag.HasFlag(ScanFilterFlag.NetworkType))
                {
                    if (scanInfo.Common.NetworkType != (byte)filter.NetworkType)
                    {
                        continue;
                    }
                }

                if (nPlayers == 0)
                {
                    continue;
                }

                // Mac address filter not implemented, since they are currently random.

                if (results >= info.Length)
                {
                    Array.Resize(ref info, info.Length + 1);
                }

                info[results++] = scanInfo;
            }

            return results;
        }

        public void CloseGame(string id)
        {
            _hostedGames.Remove(id.ToLower(), out HostedGame removed);
            removed?.Close();

            if (removed != null)
            {
                Console.WriteLine($"Removing from analytics: {id}");
                Statistics.RemoveGameAnalytics(removed);
            }
        }

        protected override TcpSession CreateSession()
        {
            return new LdnSession(this);
        }

        protected override void OnError(SocketError error)
        {
            Console.WriteLine($"LDN TCP server caught an error with code {error}");
        }

        public override bool Stop()
        {
            _cancel.Cancel();

            return base.Stop();
        }

        private async Task BackgroundPingTask()
        {
            while (!IsDisposed)
            {
                foreach (KeyValuePair<Guid, TcpSession> session in Sessions)
                {
                    (session.Value as LdnSession).Ping();
                }

                try
                {
                    await Task.Delay(InactivityPingFrequency, _cancel.Token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }

        private async Task BackgroundDumpTask()
        {
            while (!IsDisposed)
            {
                await Task.Delay(5000, _cancel.Token);
                try
                {
                    await StatsDumper.DumpAll(_hostedGames);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }
        }
    }
}