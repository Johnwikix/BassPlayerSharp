using BassPlayerSharp.Service;

public class Program
{
    private static PlayerBackService PlayerBackService { get; set; } = new PlayerBackService();
    public static void Main()
    {
        Console.WriteLine("PlayerBackService started");
        PlayerBackService.Start();
    }
}