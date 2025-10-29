using System.Text.Json.Serialization;

namespace BassPlayerSharp.Model
{
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(IpcSetting))]
    public partial class IpcSettingJsonContext : JsonSerializerContext
    {
    }
}
