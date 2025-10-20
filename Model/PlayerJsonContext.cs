using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace BassPlayerSharp.Model
{
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(RequestMessage))] // 客户端请求消息
    [JsonSerializable(typeof(ResponseMessage))]  // 服务器响应消息
    public partial class PlayerJsonContext : JsonSerializerContext
    {
    }
}
