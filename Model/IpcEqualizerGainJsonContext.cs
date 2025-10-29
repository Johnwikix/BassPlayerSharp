using System.Text.Json.Serialization;

namespace BassPlayerSharp.Model
{
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(IpcEqualizerGain))]
    public partial class IpcEqualizerGainJsonContext : JsonSerializerContext
    {
    }
}
