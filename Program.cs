using BassPlayerSharp.Service;
using System.Runtime;

public class Program
{   
    public static async Task Main(string[] args)
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        var tcpService = new TcpService();
        await tcpService.StartAsync();
    }
}