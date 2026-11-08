using System.Net;
using SockTuner.Services.Collection;
using SockTuner.Models;

try
{
    var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
    if (args.Length % 2 != 0) throw new ArgumentException("Arguments must be name/value pairs.");
    for (var i = 0; i < args.Length; i += 2)
        if (args[i] is not ("--listen" or "--port" or "--key-file" or "--seconds" or "--test" or "--packet-bytes" or "--late-ms") || !arguments.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException("Unknown or duplicate option.");
    if (!arguments.TryGetValue("--key-file", out var keyFile))
        throw new ArgumentException("A key file is required. It must contain a shared 64-character random hexadecimal key.");
    var port = int.Parse(arguments.GetValueOrDefault("--port", "52345"));
    var key = File.ReadAllText(keyFile).Trim();
    if (arguments.TryGetValue("--test", out var host))
    {
        var options = new UdpQualityOptions(int.Parse(arguments.GetValueOrDefault("--seconds", "30")), 20,
            int.Parse(arguments.GetValueOrDefault("--packet-bytes", "128")), int.Parse(arguments.GetValueOrDefault("--late-ms", "100")));
        using var clientStop = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; clientStop.Cancel(); };
        var report = await new UdpQualityProbe().RunAsync(host, port, key, options, null, clientStop.Token);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    var address = IPAddress.Parse(arguments.GetValueOrDefault("--listen", "127.0.0.1"));
    var seconds = int.Parse(arguments.GetValueOrDefault("--seconds", "600"));
    if (port is < 1 or > 65535 || seconds is < 1 or > 3600 || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        throw new ArgumentException("Use IPv4, port 1–65535 and a lifetime of 1–3600 seconds.");
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    using var server = new UdpQualityResponder(new(address, port), key);
    Console.WriteLine($"SockTuner UDP responder on {server.Endpoint}; stops after {seconds} seconds or Ctrl+C. The key is never logged.");
    try { await server.RunAsync(stop.Token); }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine("Usage: SockTuner.UdpResponder --key-file PATH [--listen IPv4] [--port 52345] [--seconds 600]");
    Console.Error.WriteLine("Client: SockTuner.UdpResponder --key-file PATH --test HOST [--port 52345] [--seconds 30] [--packet-bytes 128] [--late-ms 100]");
    return 1;
}
return 0;
