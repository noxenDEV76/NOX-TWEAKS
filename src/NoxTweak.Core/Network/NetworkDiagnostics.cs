using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace NoxTweak.Core.Network;

public sealed record PingResult(string Target, int Sent, int Received, double? MinMs, double? AvgMs, double? MaxMs, double? JitterMs, string? Error)
{
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
}

/// <summary>
/// Explicit, user-started ping tests. Three different things must never be confused:
/// latency to your router (local), to a public endpoint, and to a real game server.
/// </summary>
public static class NetworkDiagnostics
{
    private static readonly Regex HostRx = new(@"^[A-Za-z0-9]([A-Za-z0-9\-\.]{0,251}[A-Za-z0-9])?$", RegexOptions.Compiled);

    public static bool IsValidTarget(string host) => !string.IsNullOrWhiteSpace(host) && HostRx.IsMatch(host.Trim());

    public static async Task<PingResult> PingAsync(string host, int count, IProgress<int>? progress, CancellationToken ct)
    {
        host = host.Trim();
        if (!IsValidTarget(host)) return new PingResult(host, 0, 0, null, null, null, null, "Enter a host name or IP address.");
        count = Math.Clamp(count, 1, 50);

        var times = new List<double>();
        var sent = 0;
        try
        {
            using var ping = new Ping();
            for (var i = 0; i < count && !ct.IsCancellationRequested; i++)
            {
                sent++;
                try
                {
                    var r = await ping.SendPingAsync(host, 2000);
                    if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime);
                }
                catch (PingException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
                {
                    return new PingResult(host, sent, 0, null, null, null, null, "Host could not be resolved.");
                }
                progress?.Report(i + 1);
                try { await Task.Delay(250, ct); } catch (OperationCanceledException) { break; }
            }
        }
        catch (Exception ex)
        {
            return new PingResult(host, sent, times.Count, null, null, null, null, ex.Message);
        }

        if (times.Count == 0) return new PingResult(host, sent, 0, null, null, null, null, sent == 0 ? "Cancelled." : "No reply (the host may block ping).");
        double jitter = 0;
        if (times.Count > 1) jitter = Enumerable.Range(1, times.Count - 1).Average(i => Math.Abs(times[i] - times[i - 1]));
        return new PingResult(host, sent, times.Count, times.Min(), times.Average(), times.Max(), jitter, null);
    }
}
