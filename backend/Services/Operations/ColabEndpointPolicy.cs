using System.Net;
using System.Net.Sockets;

namespace ClinicalRag.Api.Services;

/// <summary>Credentials go only to a validated origin; redirects and private-address DNS rebinding are refused.</summary>
public sealed class ColabEndpointPolicy(IConfiguration configuration)
{
    private bool AllowLoopback => configuration.GetValue("Colab:AllowInsecureLoopback", false);

    public Uri Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || uri.AbsolutePath != "/" || uri.HostNameType == UriHostNameType.Unknown)
            throw new ArgumentException("Enter an HTTPS base URL without a path, credentials, query or fragment.");
        var loopback = uri.IsLoopback;
        if (loopback && AllowLoopback && uri.Scheme is "http" or "https") return uri;
        if (uri.Scheme != "https" || !uri.IsDefaultPort || loopback
            || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Contains('.') && uri.HostNameType == UriHostNameType.Dns
            || IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) && !IsPublic(address))
            throw new ArgumentException("Use a public HTTPS Colab tunnel URL. Local endpoints require the explicit development-only loopback option.");
        var hosts = configuration.GetSection("Colab:AllowedHosts").Get<string[]>() ?? [];
        if (hosts.Length > 0 && !hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("This Colab hostname is not in the configured allowlist.");
        return uri;
    }

    public HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 16,
        ConnectCallback = async (context, token) =>
        {
            var host = context.DnsEndPoint.Host;
            var allowLocal = AllowLoopback && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(host, out var literal) && IPAddress.IsLoopback(literal));
            var addresses = await Dns.GetHostAddressesAsync(host, token);
            var safe = addresses.Where(address => IsPublic(address) || allowLocal && IPAddress.IsLoopback(address)).ToArray();
            if (safe.Length == 0 || safe.Length != addresses.Length) throw new HttpRequestException("Colab resolved to a refused network address.");
            foreach (var address in safe)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("Colab connection failed.");
        }
    };

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
                && !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 192 && bytes[1] == 0) && !(bytes[0] == 198 && bytes[1] is 18 or 19)
                && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                && !(bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99);
        // Global unicast only; refuse transition/translation, documentation and IETF special-purpose ranges.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xe0) == 0x20
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 2)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            && !(bytes[0] == 0x20 && bytes[1] == 0x02)
            && !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
    }
}
