using BassPlayerSharp.Service;

public class Program
{
    private static PipeService pipeService { get; set; } = new PipeService();
    public static void Main()
    {
        Console.WriteLine("PipeService started");
        pipeService.Start();
    }
}