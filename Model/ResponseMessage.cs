using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace BassPlayerSharp.Model
{
    public class ResponseMessage
    {
        // 0 表示失败，1 表示成功，5播放状态，11表示播放结束，20当前时间，21总时间，22播放位置调整,100音量写回，1000退出
        [JsonPropertyName("type")]
        public int Type { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } // 响应/错误信息

        [JsonPropertyName("result")]
        public string Result { get; set; } // 操作结果数据
    }
}
