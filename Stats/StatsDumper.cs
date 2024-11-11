using LanPlayServer.Stats.Types;
using LanPlayServer.Utils;
using NRedisStack;
using NRedisStack.RedisStackCommands;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace LanPlayServer.Stats
{
    internal static class StatsDumper
    {
        private static ConnectionMultiplexer _redisConnection;
        private static IDatabase _db;

        public static void Start(EndPoint redisEndpoint)
        {
            _redisConnection = ConnectionMultiplexer.Connect(new ConfigurationOptions()
            {
                ClientName = "LdnServer",
                EndPoints =
                [
                    redisEndpoint,
                ],
            });

            _db = _redisConnection.GetDatabase();

            Console.WriteLine("Creating empty json objects for redis...");

            EnsureDBKeysExist(_db.JSON(), true);

            //Statistics.GameAnalyticsChanged += OnGameAnalyticsChanged;
            //Statistics.LdnAnalyticsChanged += OnLdnAnalyticsPropertyChanged;
        }

        private static void EnsureDBKeysExist(IJsonCommands json, bool overwrite = false)
        {
            json.Set("ldn", "$", new LdnAnalytics().ToJson(), overwrite ? When.Always : When.NotExists);
            json.Set("games", "$", new {}, overwrite ? When.Always : When.NotExists);
        }

        public static void Stop()
        {
            //Statistics.GameAnalyticsChanged -= OnGameAnalyticsChanged;
            //Statistics.LdnAnalyticsChanged -= OnLdnAnalyticsPropertyChanged;

            _redisConnection.Close();
            _redisConnection.Dispose();
        }

        private static void OnLdnAnalyticsPropertyChanged(LdnAnalytics analytics)
        {
            return;
            JsonCommands json = _db.JSON();
            string analyticsJson = analytics.ToJson();

            EnsureDBKeysExist(json);
            json.Set("ldn", "$", analyticsJson);
        }

        private static void OnGameAnalyticsChanged(GameAnalytics analytics, bool created)
        {
            return;
            JsonCommands json = _db.JSON();

            EnsureDBKeysExist(json);

            if (created)
            {
                Console.WriteLine("Add game analytics for " + analytics.Id);
                string analyticsJson = analytics.ToJson();
                json.Set("games", $"$.{analytics.Id}", analyticsJson);
                analytics.PropertyChanged += OnGameAnalyticsPropertyChanged;
            }
            else
            {
                Console.WriteLine("Actually removing game analytics for " + analytics.Id);
                analytics.PropertyChanged -= OnGameAnalyticsPropertyChanged;
                DeleteGameById(analytics.Id);
            }
        }

        private static void DeleteGameById(string id)
        {
            JsonCommands json = _db.JSON();
            json.Del("games", $"$.{id}");
        }

        private static void OnGameAnalyticsPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            return;
            GameAnalytics analytics = sender as GameAnalytics;
            JsonCommands json = _db.JSON();

            if (analytics == null)
            {
                return;
            }

            EnsureDBKeysExist(json);

            if (analytics.PlayerCount == 0)
            {
                //DeleteGameById(analytics.Id);
                return;
            }
            Console.WriteLine("update for " + analytics.Id);

            string analyticsJson = analytics.ToJson();

            // This could be optimized to only update the changed property.
            json.Set("games", $"$.{analytics.Id}", analyticsJson);
        }


        public static async Task DumpAll(IDictionary<string, HostedGame> games) {
            if (_db == null)
            {
                return;
            }
            var ldnAnalytics = new LdnAnalytics();

            int totalGameCount     = 0;
            int totalPlayerCount   = 0;
            int privateGameCount   = 0;
            int privatePlayerCount = 0;
            int masterProxyCount   = 0;
            int inProgressCount    = 0;

            var json = _db.JSON();
            var gamesList = new List<GameAnalytics>(games.Count);
            foreach (var hostedGame in games) {
                var game = new GameAnalytics();
                game.Update(hostedGame.Value);

                if (game.Id == null)
                {
                    continue;
                }

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
                gamesList.Add(game);
            }
            var opts = new JsonSerializerOptions()
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            };

            ldnAnalytics.TotalGameCount = totalGameCount;
            ldnAnalytics.PrivateGameCount = privateGameCount;
            ldnAnalytics.PublicGameCount = totalGameCount - privateGameCount;
            ldnAnalytics.InProgressCount = inProgressCount;
            ldnAnalytics.MasterProxyCount = masterProxyCount;
            ldnAnalytics.TotalPlayerCount = totalPlayerCount;
            ldnAnalytics.PrivatePlayerCount = privatePlayerCount;
            ldnAnalytics.PublicPlayerCount = totalPlayerCount - privatePlayerCount;

            var ldnJson = ldnAnalytics.ToJson();

            var gamesJson = GameAnalytics.ToJson(gamesList.ToArray());

            await json.SetAsync("games", "$", gamesJson);
            await json.SetAsync("ldn", "$", ldnJson);
        }
    }
}
