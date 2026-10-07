using System.Net;
using System.Net.Sockets;
using System.Text;
using Recon.Build;
using Recon.Compare;

namespace Recon.Reporting;

/// <summary>What the viewer's server needs to answer with: the documents, and alignment on demand.</summary>
public sealed class ViewerContent
{
    public required ComparisonDocument Comparison { get; init; }

    public required ProgressReport Progress { get; init; }

    /// <summary>Both sides, when they could be loaded: without them, alignment cannot be computed.</summary>
    public ComparisonSide? Left { get; init; }

    public ComparisonSide? Right { get; init; }
}

/// <summary>
/// The smallest HTTP server that can serve the viewer, so the diff is browsable while a reconstruction
/// is being worked on: rebuild, reload the page, and the numbers are the new ones. It listens on one
/// address, answers four paths, and has no state beyond the documents it was given.
/// </summary>
public sealed class ViewerServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly ViewerContent _content;
    private readonly IBuildLog _log;
    private bool _disposed;

    public ViewerServer(IPEndPoint endpoint, ViewerContent content, IBuildLog log)
    {
        _content = content;
        _log = log;
        _listener = new TcpListener(endpoint);
        _listener.Start();
        LocalEndPoint = (IPEndPoint)_listener.LocalEndpoint;
    }

    public IPEndPoint LocalEndPoint { get; }

    public int Port => LocalEndPoint.Port;

    public string Url => $"http://localhost:{Port}/";

    /// <summary>Serves until cancellation. Each request is answered on its own connection.</summary>
    public void Run(CancellationToken token)
    {
        _log.Step($"serving the diff viewer on {Url} (Ctrl-C to stop)");
        while (!token.IsCancellationRequested)
        {
            if (!_listener.Pending())
            {
                if (token.WaitHandle.WaitOne(50))
                {
                    break;
                }

                continue;
            }

            using var client = _listener.AcceptTcpClient();
            client.NoDelay = true;
            try
            {
                Answer(client);
            }
            catch (IOException ex)
            {
                _log.Warn($"request failed: {ex.Message}");
            }
        }
    }

    private void Answer(TcpClient client)
    {
        using var stream = client.GetStream();
        var buffer = new byte[8192];
        int read = stream.Read(buffer, 0, buffer.Length);
        if (read <= 0)
        {
            return;
        }

        string request = Encoding.ASCII.GetString(buffer, 0, read);
        string[] lines = request.Split('\n');
        string[] parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string method = parts.Length > 0 ? parts[0] : "GET";
        string target = parts.Length > 1 ? parts[1] : "/";

        // The request body, if any, is not read: this server answers read-only queries.
        (int status, string type, string body) = method == "GET" ? Route(target) : (405, "text/plain", "only GET is supported");
        Respond(stream, status, type, body);
    }

    private (int Status, string Type, string Body) Route(string target)
    {
        string path = target.Split('?')[0].TrimStart('/');
        var query = ParseQuery(target);

        switch (path)
        {
            case "":
            case "index.html":
                return (200, "text/html; charset=utf-8", ReportSite.ServerHtml());

            case "api/comparison":
                return (200, "application/json", Reports.Serialize(_content.Comparison));

            case "api/progress":
                return (200, "application/json", Reports.Serialize(_content.Progress));

            case "api/aligned":
                return Aligned(query.GetValueOrDefault("id") ?? string.Empty);

            default:
                return (404, "text/plain", $"no such path: /{path}");
        }
    }

    private (int Status, string Type, string Body) Aligned(string leftId)
    {
        if (_content.Left is null || _content.Right is null)
        {
            // Without the binaries there is nothing to align; the page falls back to the recorded
            // differences and says so, rather than showing an empty listing.
            return (204, "application/json", "{}");
        }

        var pair = _content.Comparison.Functions.FirstOrDefault(f => f.Left is not null && f.Left.Id == leftId);
        if (pair?.Right is null)
        {
            return (404, "application/json", "{\"rows\":null}");
        }

        var rows = ComparisonBuilder.AlignedFor(_content.Left, _content.Right, pair.Left!.Id, pair.Right.Id);
        var text = new StringBuilder("{\"rows\":");
        text.Append(rows is null ? "null" : Reports.Serialize(rows));
        text.Append('}');
        return (200, "application/json", text.ToString());
    }

    private static Dictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int question = target.IndexOf('?');
        if (question < 0)
        {
            return result;
        }

        foreach (string pair in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] halves = pair.Split('=', 2);
            string key = Uri.UnescapeDataString(halves[0]);
            result[key] = halves.Length > 1 ? Uri.UnescapeDataString(halves[1]) : string.Empty;
        }

        return result;
    }

    private static void Respond(NetworkStream stream, int status, string type, string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        string reason = status switch
        {
            200 => "OK",
            204 => "No Content",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Error",
        };

        var header = new StringBuilder();
        header.Append($"HTTP/1.1 {status} {reason}\r\n");
        header.Append($"Content-Type: {type}\r\n");
        header.Append($"Content-Length: {payload.Length}\r\n");
        header.Append("Cache-Control: no-store\r\n");
        header.Append("Connection: close\r\n\r\n");

        byte[] head = Encoding.ASCII.GetBytes(header.ToString());
        stream.Write(head, 0, head.Length);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // Stopping a listener that has already gone is not an error worth reporting.
        }
    }
}
