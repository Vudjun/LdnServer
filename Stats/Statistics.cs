using LanPlayServer.Stats.Types;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Threading.Tasks;

namespace LanPlayServer.Stats
{
    public static class Statistics
    {
        private static readonly ConcurrentDictionary<string, GameAnalytics> Games = new();
        private static readonly LdnAnalytics LdnAnalytics = new();

        public static event Action<GameAnalytics, bool> GameAnalyticsChanged;
        public static event Action<LdnAnalytics> LdnAnalyticsChanged;

        public static void AddGameAnalytics(HostedGame game)
        {
            GameAnalytics analytics = GameAnalytics.FromGame(game);

            if (Games.TryAdd(analytics.Id, analytics))
            {
                game.PropertyChanged += UpdateGameAnalytics;
                GameAnalyticsChanged?.Invoke(analytics, true);

                Task.Run(() => UpdateLdnAnalytics(Games.Values.ToImmutableList()));
            }
        }

        private static void UpdateGameAnalytics(object sender, PropertyChangedEventArgs e)
        {
            if (sender is not HostedGame game || !Games.TryGetValue(game.Id, out GameAnalytics value))
            {
                return;
            }

            value.Update(game);

            Task.Run(() => UpdateLdnAnalytics(Games.Values.ToImmutableList()));
        }

        public static void RemoveGameAnalytics(HostedGame game)
        {
            if (Games.Remove(game.Id, out GameAnalytics analytics))
            {
                game.PropertyChanged -= UpdateGameAnalytics;
                GameAnalyticsChanged?.Invoke(analytics, false);

                Task.Run(() => UpdateLdnAnalytics(Games.Values.ToImmutableList()));
            }
            else
            {
                GameAnalyticsChanged?.Invoke(new GameAnalytics { Id = game.Id }, false);
            }
        }

        private static void UpdateLdnAnalytics(ImmutableList<GameAnalytics> games)
        {
            int totalGameCount     = 0;
            int totalPlayerCount   = 0;
            int privateGameCount   = 0;
            int privatePlayerCount = 0;
            int masterProxyCount   = 0;
            int inProgressCount    = 0;

            foreach (GameAnalytics game in games)
            {
                if (game.PlayerCount == 0)
                {
                    continue;
                }

                if (game.Mode != "P2P")
                {
                    masterProxyCount++;
                }

                totalGameCount++;

                if (!game.IsPublic)
                {
                    privateGameCount++;
                    privatePlayerCount += game.PlayerCount;
                }

                if (game.Status != "Joinable")
                {
                    inProgressCount++;
                }

                totalPlayerCount += game.PlayerCount;
            }

            LdnAnalytics.TotalGameCount = totalGameCount;
            LdnAnalytics.PrivateGameCount = privateGameCount;
            LdnAnalytics.PublicGameCount = totalGameCount - privateGameCount;
            LdnAnalytics.InProgressCount = inProgressCount;
            LdnAnalytics.MasterProxyCount = masterProxyCount;
            LdnAnalytics.TotalPlayerCount = totalPlayerCount;
            LdnAnalytics.PrivatePlayerCount = privatePlayerCount;
            LdnAnalytics.PublicPlayerCount = totalPlayerCount - privatePlayerCount;

            LdnAnalyticsChanged?.Invoke(LdnAnalytics);
        }
    }
}
