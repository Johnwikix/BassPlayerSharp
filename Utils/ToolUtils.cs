using BassPlayerSharp.Model;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BassPlayerSharp.Utils
{
    public class ToolUtils
    {
        // 将JSON字符串转回字典的方法
        public static Dictionary<string, double> ConvertToDictionary(string jsonString)
        {
            if (string.IsNullOrEmpty(jsonString))
            {
                return new Dictionary<string, double>
               {
                   {"32Hz", 0},
                   {"64Hz", 0},
                   {"125Hz", 0},
                   {"250Hz", 0},
                   {"500Hz", 0},
                   {"1kHz", 0},
                   {"2kHz", 0},
                   {"4kHz", 0},
                   {"8kHz", 0},
                   {"16kHz", 0}
               };
            }

            try
            {
                return (Dictionary<string, double>)
                    JsonSerializer.Deserialize(jsonString, AppJsonSerializerContext.Default.DictionaryStringDouble);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"反序列化时出错: {ex.Message}");
                return new Dictionary<string, double>
               {
                   {"32Hz", 0},
                   {"64Hz", 0},
                   {"125Hz", 0},
                   {"250Hz", 0},
                   {"500Hz", 0},
                   {"1kHz", 0},
                   {"2kHz", 0},
                   {"4kHz", 0},
                   {"8kHz", 0},
                   {"16kHz", 0}
               };
            }
        }
    }
}
