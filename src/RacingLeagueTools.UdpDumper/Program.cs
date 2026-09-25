using System;

using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RacingLeagueTools.UdpDumper.Services;
using Serilog;

namespace RacingLeagueTools.UdpDumper;

class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            var (port, outputDir) = ParseArguments(args);
            
            var service = new UdpDumperService();
            await service.StartAsync(port, 0, 0, outputDir, "");
            
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
            return 1;
        }
    }
    
    private static (int port, string outputDir) ParseArguments(string[] args)
    {
        int port = 0;
        string outputDir = "";
        
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLower())
            {
                case "--port":
                case "-p":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedPort))
                    {
                        port = parsedPort;
                        i++; // Skip next argument as it's the value
                    }
                    break;
                    
                case "--output":
                case "-o":
                    if (i + 1 < args.Length)
                    {
                        outputDir = args[i + 1];
                        i++; // Skip next argument as it's the value
                    }
                    break;
                    
                case "--help":
                case "-h":
                    ShowHelp();
                    Environment.Exit(0);
                    break;
            }
        }
        
        return (port, outputDir);
    }
    
    private static void ShowHelp()
    {
        Console.WriteLine();
        Console.WriteLine("Racing League Tools UDP Dumper - Records UDP packets for racing games telemetry");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  UdpDumper [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -p, --port <port>      UDP port to listen on");
        Console.WriteLine("  -o, --output <path>    Output directory for dump files");
        Console.WriteLine("  -h, --help             Show this help message");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  UdpDumper --port 20777 --output \"C:\\Dumps\"");
        Console.WriteLine("  UdpDumper -p 20777 -o \"./dumps\"");
        Console.WriteLine();
    }
}
