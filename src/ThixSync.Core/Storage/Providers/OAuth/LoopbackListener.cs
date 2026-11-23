using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ThixSync.Core.Storage.Providers.OAuth;

/// <summary>
/// A one-request HTTP server on the loopback address that receives the browser's redirect after the sign-in (IPv4 and, on the same
/// port, IPv6 – the browser may resolve "localhost" to either). No http.sys, no URL reservation, no administrator rights. Answers the
/// browser with a short neutral page and hands the query (code, state or error) to the caller.
/// </summary>
internal sealed class LoopbackListener : IDisposable
{
    private readonly TcpListener _ipv4;
    private readonly TcpListener? _ipv6;

    /// <summary>Listens on <paramref name="port"/> (0: any free port).</summary>
    public LoopbackListener(int port)
    {
        _ipv4 = new TcpListener(IPAddress.Loopback, port);
        _ipv4.Start();
        Port = ((IPEndPoint)_ipv4.LocalEndpoint).Port;
        try
        {
            _ipv6 = new TcpListener(IPAddress.IPv6Loopback, Port);
            _ipv6.Start();
        }
        catch (SocketException)
        {
            _ipv6 = null; // no IPv6 on this computer (or the port is taken there): the browser then uses IPv4
        }
    }

    public int Port { get; }

    /// <summary>Waits for the request that carries "code" or "error" (a favicon or a probe of the browser is answered with 404).</summary>
    public async Task<Dictionary<string, string>> WaitForRedirectAsync(CancellationToken cancellationToken)
    {
        // One pending accept per listener; the one that answered is replaced, the other keeps waiting.
        var pending = new Dictionary<Task<TcpClient>, TcpListener> { [_ipv4.AcceptTcpClientAsync(cancellationToken).AsTask()] = _ipv4 };
        if (_ipv6 is not null)
        {
            pending[_ipv6.AcceptTcpClientAsync(cancellationToken).AsTask()] = _ipv6;
        }

        while (true)
        {
            var accepted = await Task.WhenAny(pending.Keys);
            var listener = pending[accepted];
            pending.Remove(accepted);
            pending[listener.AcceptTcpClientAsync(cancellationToken).AsTask()] = listener;
            using var client = await accepted;
            Dictionary<string, string>? query;
            try
            {
                query = await ReadRequestAsync(client, cancellationToken);
            }
            catch (IOException)
            {
                continue; // the browser closed a speculative connection
            }

            if (query is null)
            {
                continue;
            }

            await RespondAsync(client, query.ContainsKey("error"), cancellationToken);
            return query;
        }
    }

    public void Dispose()
    {
        _ipv4.Stop();
        _ipv6?.Stop();
    }

    private static async Task<Dictionary<string, string>?> ReadRequestAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        var requestLine = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0];
        var parts = requestLine.Split(' ');
        var query = parts.Length >= 2 && parts[0] == "GET" ? ParseQuery(parts[1]) : [];
        if (query.ContainsKey("code") || query.ContainsKey("error"))
        {
            return query;
        }

        var notFound = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(notFound, cancellationToken);
        return null;
    }

    private static Dictionary<string, string> ParseQuery(string target)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        var start = target.IndexOf('?');
        if (start < 0)
        {
            return query;
        }

        foreach (var pair in target[(start + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? "" : Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            query[name] = value;
        }

        return query;
    }

    private static async Task RespondAsync(TcpClient client, bool failed, CancellationToken cancellationToken)
    {
        var title = failed ? "Anmeldung nicht abgeschlossen" : "Anmeldung abgeschlossen";
        var text = failed
            ? "ThixSync zeigt dir, was nicht geklappt hat. Du kannst dieses Fenster schließen."
            : "Anmeldung abgeschlossen – du kannst dieses Fenster schließen.";
        var html = $$"""
            <!doctype html><html lang="de"><head><meta charset="utf-8"><title>ThixSync – {{title}}</title>
            <style>body{font-family:-apple-system,'Segoe UI',sans-serif;background:#F5F6F8;color:#111827;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
            div{background:#fff;border:1px solid #E5E7EB;border-radius:16px;padding:32px 40px;text-align:center;box-shadow:0 8px 30px rgba(0,0,0,.06)}
            h1{font-size:20px;margin:0 0 8px}p{color:#6B7280;margin:0}</style></head>
            <body><div><h1>{{title}}</h1><p>{{text}}</p></div></body></html>
            """;
        var bytes = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        var stream = client.GetStream();
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
