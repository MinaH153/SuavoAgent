using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace SuavoAgent.Setup;

/// <summary>
/// Extracts SQL Server credentials from PioneerRx config.
/// Strategy chain: config XML host -> ConnectionStringServer TCP:12345 -> SQL Browser UDP:1434 -> manual.
/// </summary>
internal static class SqlCredentialDiscovery
{
    public sealed record SqlCredentials(
        string Server,
        string Database,
        string? User,
        string? Password)
    {
        public bool IsWindowsAuth => User == null;
    }

    private const int ConnectionStringServerPort = 12345;
    private const int SqlBrowserPort = 1434;
    private const string DefaultDatabase = "PioneerPharmacySystem";

    /// <summary>
    /// Full discovery pipeline. Returns null only if everything fails and user declines manual entry.
    /// </summary>
    public static SqlCredentials? Discover(string pioneerConfigPath)
    {
        // Step 1: Extract host from PioneerPharmacy.exe.config
        var host = ExtractHostFromConfig(pioneerConfigPath);
        if (host != null)
        {
            ConsoleUI.WriteOk($"PioneerRx host: {host}");
        }
        else
        {
            ConsoleUI.WriteWarn("Could not extract host from config - will try localhost");
            host = "localhost";
        }

        // Step 2: Try ConnectionStringServer on TCP port 12345
        var connStr = TryConnectionStringServer(host);
        if (connStr != null)
        {
            var parsed = ParseConnectionString(connStr);
            if (parsed != null)
            {
                ConsoleUI.WriteOk($"SQL Server: {parsed.Server}");
                ConsoleUI.WriteOk($"Database: {parsed.Database}");
                ConsoleUI.WriteOk($"Auth: {(parsed.IsWindowsAuth ? "Windows" : $"SQL ({parsed.User})")}");
                return parsed;
            }
        }

        // Step 3: Try SQL Browser for instance discovery
        var browserResult = TrySqlBrowser(host);
        if (browserResult != null)
        {
            ConsoleUI.WriteOk($"SQL Browser found: {browserResult}");
            return new SqlCredentials(browserResult, DefaultDatabase, null, null);
        }

        // Step 4: Manual fallback
        return PromptManual();
    }

    /// <summary>
    /// Reads PioneerPharmacy.exe.config XML to find the SQL server host.
    /// Checks newTechDataConfiguration element, then regex fallback.
    /// </summary>
    private static string? ExtractHostFromConfig(string configPath)
    {
        try
        {
            var configText = File.ReadAllText(configPath);
            var doc = new XmlDocument();
            doc.LoadXml(configText);

            // Try newTechDataConfiguration element
            var ntdc = doc.SelectSingleNode("//newTechDataConfiguration");
            if (ntdc != null)
            {
                var hostAttr = ntdc.Attributes?["host"]?.Value
                    ?? ntdc.Attributes?["server"]?.Value;
                if (!string.IsNullOrEmpty(hostAttr))
                    return hostAttr;

                // Check child element text
                var hostElem = ntdc.SelectSingleNode("host") ?? ntdc.SelectSingleNode("server");
                if (hostElem != null && !string.IsNullOrEmpty(hostElem.InnerText))
                    return hostElem.InnerText;
            }

            // Regex fallback on raw text
            var hostMatch = Regex.Match(configText, @"host\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
            if (hostMatch.Success) return hostMatch.Groups[1].Value;

            var serverMatch = Regex.Match(configText, @"server\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
            if (serverMatch.Success) return serverMatch.Groups[1].Value;

            // Try ConnectionStringServer-style config value
            var cssMatch = Regex.Match(configText, @"ConnectionStringServer[^""]*""([^""]+)""", RegexOptions.IgnoreCase);
            if (cssMatch.Success) return cssMatch.Groups[1].Value;
        }
        catch (Exception ex)
        {
            ConsoleUI.WriteInfo($"Config parse error: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Connects to the PioneerRx ConnectionStringServer on TCP port 12345.
    /// Protocol: send length-prefixed UTF-8 database name, receive connection string.
    /// </summary>
    private static string? TryConnectionStringServer(string host)
    {
        ConsoleUI.WriteInfo($"Trying ConnectionStringServer at {host}:{ConnectionStringServerPort}...");

        try
        {
            using var tcp = new TcpClient();
            tcp.Connect(host, ConnectionStringServerPort);

            using var stream = tcp.GetStream();
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 5000;

            // Send length-prefixed database name
            var reqBytes = Encoding.UTF8.GetBytes(DefaultDatabase);
            var lenBytes = BitConverter.GetBytes(reqBytes.Length);
            stream.Write(lenBytes, 0, 4);
            stream.Write(reqBytes, 0, reqBytes.Length);
            stream.Flush();

            // Read response
            var buf = new byte[4096];
            var read = stream.Read(buf, 0, buf.Length);
            if (read <= 0) return null;

            var response = Encoding.UTF8.GetString(buf, 0, read);

            // Skip length prefix if present, look for connection string markers
            if (response.Length > 4 && response[4..].Contains("Data Source", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleUI.WriteOk("Got connection string from ConnectionStringServer");
                return response[4..];
            }

            if (response.Contains("Data Source", StringComparison.OrdinalIgnoreCase) ||
                response.Contains("Server=", StringComparison.OrdinalIgnoreCase))
            {
                // Trim leading length-prefix bytes (non-printable)
                var trimmed = response.TrimStart('\0', '\x01', '\x02', '\x03', '\x04', '\x05');
                ConsoleUI.WriteOk("Got connection string from ConnectionStringServer");
                return trimmed;
            }
        }
        catch (Exception ex)
        {
            ConsoleUI.WriteInfo($"ConnectionStringServer failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Parses a SQL Server connection string into structured credentials.
    /// Manual parsing — no System.Data.SqlClient dependency needed.
    /// </summary>
    private static SqlCredentials? ParseConnectionString(string connStr)
    {
        try
        {
            var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var segment in connStr.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIdx = segment.IndexOf('=');
                if (eqIdx <= 0) continue;
                var key = segment[..eqIdx].Trim();
                var val = segment[(eqIdx + 1)..].Trim();
                parts[key] = val;
            }

            var server = GetValue(parts, "Data Source", "Server", "Address", "Addr");
            var database = GetValue(parts, "Initial Catalog", "Database") ?? DefaultDatabase;
            var integratedSecurity = GetValue(parts, "Integrated Security", "Trusted_Connection");
            string? user = null, password = null;

            var isWindows = integratedSecurity != null &&
                (integratedSecurity.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                 integratedSecurity.Equals("SSPI", StringComparison.OrdinalIgnoreCase) ||
                 integratedSecurity.Equals("yes", StringComparison.OrdinalIgnoreCase));

            if (!isWindows)
            {
                user = GetValue(parts, "User ID", "UID", "User");
                password = GetValue(parts, "Password", "PWD");
            }

            if (server == null)
            {
                ConsoleUI.WriteWarn("Connection string missing server address");
                return null;
            }

            return new SqlCredentials(server, database, user, password);
        }
        catch (Exception ex)
        {
            ConsoleUI.WriteWarn($"Connection string parse failed: {ex.Message}");
            return null;
        }
    }

    private static string? GetValue(Dictionary<string, string> parts, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (parts.TryGetValue(key, out var val))
                return val;
        }
        return null;
    }

    /// <summary>
    /// Sends a SQL Browser probe (UDP 1434) to discover SQL instances and their TCP ports.
    /// </summary>
    private static string? TrySqlBrowser(string host)
    {
        ConsoleUI.WriteInfo($"Trying SQL Browser on {host}:{SqlBrowserPort}...");

        try
        {
            var addresses = Dns.GetHostAddresses(host);
            var ipv4 = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 == null)
            {
                ConsoleUI.WriteInfo("Could not resolve host to IPv4");
                return null;
            }

            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 3000;

            var ep = new IPEndPoint(ipv4, SqlBrowserPort);
            udp.Send(new byte[] { 0x02 }, 1, ep);

            var recvEp = new IPEndPoint(IPAddress.Any, 0);
            var resp = udp.Receive(ref recvEp);

            if (resp.Length <= 3) return null;

            var respStr = Encoding.ASCII.GetString(resp, 3, resp.Length - 3);
            var parts = respStr.Split(';');

            string? instance = null, port = null;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "InstanceName") instance = parts[i + 1];
                if (parts[i] == "tcp" && Regex.IsMatch(parts[i + 1], @"^\d+$"))
                    port = parts[i + 1];
            }

            if (port != null)
            {
                var server = $"{ipv4},{port}";
                ConsoleUI.WriteInfo($"SQL Browser: instance={instance}, port={port}");
                return server;
            }
        }
        catch (Exception ex)
        {
            ConsoleUI.WriteInfo($"SQL Browser failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Interactive fallback for when auto-discovery fails.
    /// </summary>
    private static SqlCredentials? PromptManual()
    {
        ConsoleUI.WriteWarn("Auto-discovery failed. Manual entry required.");
        Console.WriteLine();

        Console.Write("  SQL Server (e.g. 192.168.1.78,49202): ");
        var server = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(server)) return null;

        Console.Write("  Use SQL Auth? (y/N): ");
        var useAuth = Console.ReadLine()?.Trim();

        string? user = null, password = null;
        if (useAuth?.Equals("y", StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.Write("  SQL Username: ");
            user = Console.ReadLine()?.Trim();
            Console.Write("  SQL Password: ");
            password = ReadPassword();
        }

        return new SqlCredentials(server, DefaultDatabase, user, password);
    }

    /// <summary>
    /// Reads a password from console with asterisk masking.
    /// </summary>
    private static string ReadPassword()
    {
        var sb = new StringBuilder();
        try
        {
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
                {
                    sb.Remove(sb.Length - 1, 1);
                    Console.Write("\b \b");
                }
                else if (key.KeyChar != '\0')
                {
                    sb.Append(key.KeyChar);
                    Console.Write('*');
                }
            }
            Console.WriteLine();
        }
        catch
        {
            // Non-interactive — read plaintext
            return Console.ReadLine() ?? "";
        }
        return sb.ToString();
    }
}
