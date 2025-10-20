using BassPlayerSharp.Model;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace BassPlayerSharp.Service
{
    public class PlayerBackService
    {
        private const string PipeName = "BassPlayerPipe";

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
                        response = new ResponseMessage { Success = false, Message = "Invalid request format." };
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
                    response = new ResponseMessage { Success = false, Message = $"JSON deserialization failed: {jEx.Message}" };
                }
                catch (Exception ex)
                {
                    // 其他处理错误
                    Console.WriteLine($"Command processing error: {ex.Message}");
                    response = new ResponseMessage { Success = false, Message = $"Server error: {ex.Message}" };
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

            // 根据 request.Command 执行具体逻辑
            try
            {
                switch (request.Command?.ToLower())
                {
                    case "play":
                        // 实际播放逻辑
                        // ...
                        return new ResponseMessage
                        {
                            Success = true,
                            Message = $"Started playing: {request.Data}",
                            Result = "Playback_Started"
                        };
                    case "pause":
                        // 实际暂停逻辑
                        // ...
                        return new ResponseMessage
                        {
                            Success = true,
                            Message = "Playback paused.",
                            Result = "Playback_Paused"
                        };
                    // 添加更多命令处理...
                    default:
                        return new ResponseMessage
                        {
                            Success = false,
                            Message = $"Unknown command: {request.Command}",
                            Result = "Error_UnknownCommand"
                        };
                }
            }
            catch (Exception ex)
            {
                return new ResponseMessage
                {
                    Success = false,
                    Message = $"Error during command execution: {ex.Message}",
                    Result = "Error_Execution"
                };
            }
        }
    }
    //public class PlayerBackService
    //{
    //    public void Start()
    //    {
    //        // 创建命名管道服务器
    //        Console.WriteLine("PlayerBackService started and waiting for commands...");
    //        var server = new NamedPipeServerStream("BassPlayerPipe", PipeDirection.In, 1);
    //        server.WaitForConnection(); // 等待 WinUI 进程连接
    //        Console.WriteLine("Client connected to PlayerBackService.");
    //        using (var reader = new StreamReader(server))
    //        {
    //            string command;      
    //            Console.WriteLine("Listening for commands...");
    //            while ((command = reader.ReadLine()) != null)
    //            {
    //                // 解析命令并执行播放操作
    //                ExecuteCommand(command);
    //            }
    //        }           
    //    }

    //    private void ExecuteCommand(string command)
    //    {
    //        // 在这里实现具体的播放逻辑
    //        Console.WriteLine($"Received command: {command}");
    //    }
    //}
}
