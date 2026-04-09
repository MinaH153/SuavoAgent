using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Adapters.PioneerRx.Sql;

public readonly record struct SqlBrowserResult(string InstanceName, int TcpPort, string? Version);

public static class SqlDiscovery
{
    public static SqlBrowserResult? ParseBrowserResponse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return null;

        var parts = response.Split(';');
        string? instanceName = null;
        int? tcpPort = null;
        string? version = null;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i])
            {
                case "InstanceName" when i + 1 < parts.Length:
                    instanceName = parts[i + 1];
                    break;
                case "tcp" when i + 1 < parts.Length && int.TryParse(parts[i + 1], out var port):
                    tcpPort = port;
                    break;
                case "Version" when i + 1 < parts.Length:
                    version = parts[i + 1];
                    break;
            }
        }

        if (instanceName is null || tcpPort is null)
            return null;

        return new SqlBrowserResult(instanceName, tcpPort.Value, version);
    }

    public static async Task<SqlBrowserResult?> ProbeSqlBrowserAsync(
        string targetIp, int timeoutMs, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = timeoutMs;

            var endpoint = new IPEndPoint(IPAddress.Parse(targetIp), 1434);
            await udp.SendAsync(new byte[] { 0x02 }, 1, endpoint);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            var responseStr = Encoding.ASCII.GetString(result.Buffer, 3, result.Buffer.Length - 3);
            logger.LogDebug("SQL Browser response from {Ip}: {Response}", targetIp, responseStr);

            return ParseBrowserResponse(responseStr);
        }
        catch (Exception ex)
        {
            logger.LogDebug("SQL Browser probe failed for {Ip}: {Error}", targetIp, ex.Message);
            return null;
        }
    }

    public static async Task<(string Server, int Port)?> DiscoverSqlServerAsync(
        IReadOnlyList<string> candidateIps, int timeoutMs, ILogger logger, CancellationToken ct)
    {
        foreach (var ip in candidateIps)
        {
            if (ct.IsCancellationRequested) break;

            var result = await ProbeSqlBrowserAsync(ip, timeoutMs, logger, ct);
            if (result.HasValue)
            {
                logger.LogInformation("SQL Server found at {Ip}:{Port} (instance: {Instance})",
                    ip, result.Value.TcpPort, result.Value.InstanceName);
                return (ip, result.Value.TcpPort);
            }
        }

        return null;
    }
}
