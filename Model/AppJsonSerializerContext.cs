using System.Text.Json.Serialization;

namespace BassPlayerSharp.Model
{
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(Dictionary<string, double>))]
    public partial class AppJsonSerializerContext : JsonSerializerContext
    {
    }
}
