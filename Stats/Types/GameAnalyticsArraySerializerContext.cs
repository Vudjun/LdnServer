using System.Text.Json.Serialization;

namespace LanPlayServer.Stats.Types
{
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(GameAnalytics[]))]
    internal partial class GameAnalyticsArraySerializerContext : JsonSerializerContext
    {

    }
}