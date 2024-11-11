using LanPlayServer.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LanPlayServer.Stats.Types
{
    public class GameAnalytics: INotifyPropertyChanged
    {
        private static readonly GameAnalyticsSerializerContext SerializerContext = new(JsonHelper.GetDefaultSerializerOptions(false));
        private static readonly GameAnalyticsArraySerializerContext ArraySerializerContext = new(JsonHelper.GetDefaultSerializerOptions(false));

        public event PropertyChangedEventHandler PropertyChanged;

        private string _id;
        private bool _isPublic;
        private int _playerCount;
        private int _maxPlayerCount;
        private string _gameName;
        private string _titleId;
        private string _titleVersion;
        private string _mode;
        private string _status;
        private int _sceneId;
        private List<string> _players;

        public string Id
        {
            get => _id;
            set
            {
                if (_id != value)
                {
                    _id = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public bool IsPublic
        {
            get => _isPublic;
            set
            {
                if (_isPublic != value)
                {
                    _isPublic = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public int PlayerCount
        {
            get => _playerCount;
            set
            {
                if (_playerCount != value)
                {
                    _playerCount = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public int MaxPlayerCount
        {
            get => _maxPlayerCount;
            set
            {
                if (_maxPlayerCount != value)
                {
                    _maxPlayerCount = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string GameName
        {
            get => _gameName;
            set
            {
                if (_gameName != value)
                {
                    _gameName = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string TitleId
        {
            get => _titleId;
            set
            {
                if (_titleId != value)
                {
                    _titleId = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string TitleVersion
        {
            get => _titleVersion;
            set
            {
                if (_titleVersion != value)
                {
                    _titleVersion = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string Mode
        {
            get => _mode;
            set
            {
                if (_mode != value)
                {
                    _mode = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public int SceneId
        {
            get => _sceneId;
            set
            {
                if (_sceneId != value)
                {
                    _sceneId = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public List<string> Players
        {
            get => _players;
            set
            {
                if (!ReferenceEquals(_players, value))
                {
                    _players = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private static void FromGame(GameAnalytics instance, HostedGame game)
        {
            if (game.Closing)
            {
                return;
            }
            ulong appId = (ulong)game.Info.NetworkId.IntentId.LocalCommunicationId;
            string gameName = GameList.GetGameById(appId)?.Name ?? "Unknown";
            var players = new List<string>();

            foreach (var player in game.Info.Ldn.Nodes.AsSpan()[..game.Info.Ldn.NodeCount])
            {
                string name = StringUtils.ReadUtf8String(player.UserName.AsSpan());

                // Sanitize user input
                name = name.CleanInput(32);

                // Would like to add more player information here, but that needs a bit more work.

                players.Add(name);
            }

            instance.Id = game.Id;
            instance.IsPublic = string.IsNullOrWhiteSpace(game.Passphrase);
            instance.PlayerCount = game.Info.Ldn.NodeCount;
            instance.MaxPlayerCount = game.Info.Ldn.NodeCountMax;
            // We can mostly trust these names, but just in case remove <>
            instance.GameName = gameName.Trim().Replace("<", "_").Replace(">", "_");
            instance.TitleId = appId.ToString("X16");
            instance.TitleVersion = game.GameVersion.CleanInput(16, @"\.");
            instance.Mode = game.IsP2P ? "P2P" : "Master Server Proxy";
            instance.Status = game.Info.Ldn.StationAcceptPolicy == 1 ? "Not Joinable" : "Joinable";
            instance.SceneId = game.Info.NetworkId.IntentId.SceneId;
            instance.Players = players;
        }

        public void Update(HostedGame game)
        {
            FromGame(this, game);
        }

        public string ToJson()
        {
            return JsonHelper.Serialize(this, SerializerContext.GameAnalytics);
        }

        public static string ToJson(GameAnalytics[] instances)
        {
            return JsonHelper.Serialize(instances, ArraySerializerContext.GameAnalyticsArray);
        }

        public static GameAnalytics FromGame(HostedGame game)
        {
            GameAnalytics analytics = new();
            FromGame(analytics, game);

            return analytics;
        }

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            //PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
