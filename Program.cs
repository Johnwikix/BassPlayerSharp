using BassPlayerSharp.Service;
using System.Runtime;

public class Program
{
    public static async Task Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, _) => { };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { e.SetObserved(); } catch { }
        };
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        try
        {
            var tcpService = new MmpIpcService();
            await tcpService.StartAsync();
        }
        catch { }
    }
}