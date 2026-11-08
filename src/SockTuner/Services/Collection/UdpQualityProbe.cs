using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using SockTuner.Models;

namespace SockTuner.Services.Collection;

public sealed class UdpQualityProbe
{
    private readonly TimeSpan _settle;
    private readonly TimeSpan _controlTimeout;
    public UdpQualityProbe() : this(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500)) { }
    internal UdpQualityProbe(TimeSpan settle, TimeSpan controlTimeout) => (_settle, _controlTimeout) = (settle, controlTimeout);

    public async Task<UdpQualityReport> RunAsync(string host, int port, string keyText, UdpQualityOptions options,
        IProgress<UdpQualityProgress>? progress, CancellationToken token)
    {
        options.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var key = UdpQualityProtocol.ParseKey(keyText);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, token);
            var address = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new IOException("The responder needs a reachable IPv4 address.");
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(address, port);
            var local = ((IPEndPoint)udp.Client.LocalEndPoint!).Address;
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(local)));
            var session = Guid.NewGuid();
            var hello = UdpQualityProtocol.Create(UdpQualityMessage.Hello, session, key, options.PacketCount, options.PacketsPerSecond);
            var ready = false;
            for (var retry = 0; retry < 4 && !ready; retry++)
            {
                await udp.SendAsync(hello, token);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(_controlTimeout);
                try
                {
                    while (!ready)
                    {
                        var packet = await udp.ReceiveAsync(wait.Token);
                        ready = UdpQualityProtocol.TryRead(packet.Buffer, key, out var type, out var id, out var count, out _)
                            && type == UdpQualityMessage.Ready && id == session && count == options.PacketCount;
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
            if (!ready) throw new IOException("No authenticated responder reply. Check the address, port, key and firewall; no loss verdict is available.");

            var started = DateTimeOffset.Now;
            var clock = Stopwatch.StartNew();
            var sentAt = Enumerable.Repeat(-1L, options.PacketCount).ToArray();
            var received = new bool[options.PacketCount];
            var rtts = new List<double>(options.PacketCount);
            var summary = new TaskCompletionSource<bool[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var (late, duplicates, reordered, highest, delays) = (0, 0, 0, -1, 0);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var receiver = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var packet = await udp.ReceiveAsync(stop.Token);
                        if (!UdpQualityProtocol.TryRead(packet.Buffer, key, out var type, out var id, out var seq, out var value)
                            || id != session) continue;
                        if (type == UdpQualityMessage.Summary && seq == options.PacketCount)
                        {
                            var bitmap = UdpQualityProtocol.ReadBitmap(packet.Buffer, seq, value);
                            if (bitmap is not null) summary.TrySetResult(bitmap);
                            continue;
                        }
                        if (type != UdpQualityMessage.Echo || seq < 0 || seq >= received.Length
                            || packet.Buffer.Length != options.PacketBytes) continue;
                        var timestamp = Interlocked.Read(ref sentAt[seq]);
                        if (timestamp < 0) continue;
                        if (received[seq]) { duplicates++; continue; }
                        received[seq] = true;
                        var rtt = (clock.ElapsedTicks - timestamp) * 1000d / Stopwatch.Frequency;
                        rtts.Add(rtt);
                        if (rtt > options.LateThresholdMs) late++;
                        if (seq < highest) reordered++;
                        highest = Math.Max(highest, seq);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception error) { summary.TrySetException(error); }
            }, CancellationToken.None);
            try
            {
                var interval = 1d / options.PacketsPerSecond;
                for (var i = 0; i < options.PacketCount; i++)
                {
                    var wait = TimeSpan.FromSeconds(i * interval) - clock.Elapsed;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
                    if (clock.Elapsed.TotalSeconds - i * interval > interval) delays++;
                    var probe = UdpQualityProtocol.Create(UdpQualityMessage.Probe, session, key, i, size: options.PacketBytes);
                    Interlocked.Exchange(ref sentAt[i], clock.ElapsedTicks);
                    await udp.SendAsync(probe, token);
                    progress?.Report(new(i + 1, options.PacketCount));
                }
                var sendElapsed = clock.Elapsed.TotalSeconds;
                await Task.Delay(_settle, token);
                var request = UdpQualityProtocol.Create(UdpQualityMessage.SummaryRequest, session, key, size: 1024);
                for (var retry = 0; retry < 5 && !summary.Task.IsCompleted; retry++)
                {
                    await udp.SendAsync(request, token);
                    await Task.WhenAny(summary.Task, Task.Delay(_controlTimeout, token));
                    token.ThrowIfCancellationRequested();
                }
                if (!summary.Task.IsCompleted) throw new IOException("The responder receipt summary is missing. Directional loss is inconclusive; repeat the test.");
                var seen = await summary.Task;
                await stop.CancelAsync();
                await receiver;
                token.ThrowIfCancellationRequested();
                if (received.Where((yes, i) => yes && !seen[i]).Any())
                    throw new IOException("The responder summary conflicts with the echoes. No loss verdict is available.");
                return new(started, new IPEndPoint(address, port).ToString(), options,
                    local.ToString(), adapter?.Id ?? string.Empty, adapter?.Speed ?? 0,
                    adapter?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    options.PacketCount, seen.Count(x => x), received.Count(x => x), late, duplicates,
                    reordered, delays, sendElapsed, rtts.ToArray());
            }
            finally { await stop.CancelAsync(); await receiver; }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
