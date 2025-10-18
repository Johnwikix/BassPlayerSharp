using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;

namespace BassPlayerSharp.Service
{
    public class PlayerBackService
    {
        public void Start()
        {
            // 创建命名管道服务器
            Console.WriteLine("PlayerBackService started and waiting for commands...");
            var server = new NamedPipeServerStream("BassPlayerPipe", PipeDirection.In, 1);
            server.WaitForConnection(); // 等待 WinUI 进程连接
            Console.WriteLine("Client connected to PlayerBackService.");
            using (var reader = new StreamReader(server))
            {
                string command;      
                Console.WriteLine("Listening for commands...");
                while ((command = reader.ReadLine()) != null)
                {
                    // 解析命令并执行播放操作
                    ExecuteCommand(command);
                }
            }           
        }

        private void ExecuteCommand(string command)
        {
            // 在这里实现具体的播放逻辑
            Console.WriteLine($"Received command: {command}");
        }
    }
}
