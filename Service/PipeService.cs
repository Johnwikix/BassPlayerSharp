using BassPlayerSharp.Model;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace BassPlayerSharp.Service
{
    public class PipeService
    {
        private const string PipeName = "BassPlayerPipe";
        private readonly PlayBackService playBackService;

        public PipeService()
        {
            this.playBackService = new PlayBackService(this);
        }
        public void Start()
        {
            Console.WriteLine("PlayerBackService started.");
            using (var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1))
            {
                Console.WriteLine($"Waiting for connection on pipe: {PipeName}...");
                try
                {
                    server.WaitForConnection(); // 等待客户端连接
                    Console.WriteLine("Client connected to PlayerBackService.");
                    // 使用 StreamReader 和 StreamWriter 进行双向通信
                    using (var reader = new StreamReader(server))
                    using (var writer = new StreamWriter(server) { AutoFlush = true }) // 立即发送数据
                    {
                        CommunicationLoop(reader, writer);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Pipe communication error: {ex.Message}");
                }
                finally
                {
                    Console.WriteLine("Client disconnected. ");
                }
            }
        }

        private void CommunicationLoop(StreamReader reader, StreamWriter writer)
        {
            Console.WriteLine("Listening for commands...");
            string receivedJson;

            // 循环读取客户端发送的消息
            while ((receivedJson = reader.ReadLine()) != null)
            {
                Console.WriteLine($"Received JSON: {receivedJson}");
                ResponseMessage response;

                try
                {
                    // 1. 反序列化收到的 JSON 消息
                    var request = JsonSerializer.Deserialize(receivedJson, PlayerJsonContext.Default.RequestMessage);

                    if (request == null)
                    {
                        response = new ResponseMessage { Type = 0, Message = "Invalid request format." };
                    }
                    else
                    {
                        // 2. 处理命令并获取响应
                        response = ExecuteCommand(request);
                    }
                }
                catch (JsonException jEx)
                {
                    // JSON 解析错误
                    Console.WriteLine($"JSON deserialization error: {jEx.Message}");
                    response = new ResponseMessage { Type = 0, Message = $"JSON deserialization failed: {jEx.Message}" };
                }
                catch (Exception ex)
                {
                    // 其他处理错误
                    Console.WriteLine($"Command processing error: {ex.Message}");
                    response = new ResponseMessage { Type = 0, Message = $"Server error: {ex.Message}" };
                }

                // 3. 序列化响应消息并发送给客户端
                try
                {
                    string responseJson = JsonSerializer.Serialize(response, PlayerJsonContext.Default.ResponseMessage);
                    writer.WriteLine(responseJson); // 发送响应
                    Console.WriteLine($"Sent JSON response: {responseJson}");
                }
                catch (Exception writeEx)
                {
                    Console.WriteLine($"Error writing response: {writeEx.Message}");
                    // 写入失败通常意味着客户端已断开，退出循环
                    break;
                }
            }
        }

        private ResponseMessage ExecuteCommand(RequestMessage request)
        {
            Console.WriteLine($"Executing command: {request.Command} with data: {request.Data}");
            try
            {
                switch (request.Command)
                {
                    case "Play":
                        playBackService.PlayMusic(request.Data);
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = $"Started playing: {request.Data}",
                            Result = "Playback_Started"
                        };
                    case "PlayButton":
                        playBackService.PlayButton();
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = "Play button pressed.",
                            Result = "Playback_Started"
                        };
                    case "SetMusicUrl":
                        playBackService.MusicUrl = request.Data;
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = $"Music URL set to: {request.Data}",
                            Result = "MusicUrl_Set"
                        };
                    case "Volume":
                        playBackService.SetVolume(int.Parse(request.Data));
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = "Playback paused.",
                            Result = "Playback_Paused"
                        };                  
                    case "GetProgress":
                        var progress = playBackService.GetCurrentPosition();
                        return new ResponseMessage
                        {
                            Type = 10,
                            Message = "Current progress retrieved.",
                            Result = progress.ToString()
                        };
                    case "GetDuration":
                        var duration = playBackService.GetTotalPosition();
                        return new ResponseMessage
                        {
                            Type = 10,
                            Message = "Track duration retrieved.",
                            Result = duration.ToString()
                        };
                    case "ChangePosition":
                        playBackService.ChangeWaveChannelTime(TimeSpan.FromSeconds(int.Parse(request.Data)));
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = "Playback position changed.",
                            Result = "Position_Changed"
                        };
                    case "ChangeVolume":
                        playBackService.SetVolume(int.Parse(request.Data));
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = "Volume changed.",
                            Result = "Volume_Changed"
                        };
                    case "UpdateSettings":
                        playBackService.UpdateSettings(request.Data);
                        return new ResponseMessage
                        {
                            Type = 1,
                            Message = "Settings updated.",
                            Result = "Settings_Updated"
                        };
                    default:
                        return new ResponseMessage
                        {
                            Type = 0,
                            Message = $"Unknown command: {request.Command}",
                            Result = "Error_UnknownCommand"
                        };
                }
            }
            catch (Exception ex)
            {
                return new ResponseMessage
                {
                    Type = 0,
                    Message = $"Error during command execution: {ex.Message}",
                    Result = "Error_Execution"
                };
            }
        }
    }
}
