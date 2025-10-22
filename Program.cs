using BassPlayerSharp.Service;

public class Program
{   
    public static async Task Main(string[] args)
    {       
        var tcpService = new TcpService();
        await tcpService.StartAsync();
        Console.WriteLine("PipeService started");
    }
}