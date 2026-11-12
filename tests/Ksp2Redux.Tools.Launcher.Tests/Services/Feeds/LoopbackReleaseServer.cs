using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Feeds;

/// <summary>
/// Owned TLS socket fixture for production HTTP handlers, with no DNS or external connection fallback.
/// </summary>
internal class LoopbackReleaseServer : IAsyncDisposable
{
    internal const string TEST_TOKEN = "owned-loopback-test-token";
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly X509Certificate2 _certificate;
    private readonly Process _tls;
    private readonly DateTime _tlsStarted;
    private readonly string _tlsExecutable;
    private readonly int _tlsPort;
    private readonly string _root;
    private readonly Task _accept;
    internal readonly ConcurrentQueue<Request> Requests = [];
    internal Func<Request, Stream, CancellationToken, Task> Respond = (_, stream, ct) => ReplyAsync(stream, [], 404, ct);

    /// <summary>
    /// Nonsecret request observations retained for transport and coordinator assertions.
    /// </summary>
    /// <param name="Host">The logical request authority.</param>
    /// <param name="Path">The requested path and query.</param>
    /// <param name="Authorized">True only for the explicit fixture credential.</param>
    /// <param name="Range">The optional resume range.</param>
    internal record Request(string Host, string Path, bool Authorized, string? Range);

    /// <summary>
    /// Start an owned loopback backend and ephemeral-key TLS child without changing host trust.
    /// </summary>
    internal LoopbackReleaseServer()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=owned-loopback", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        foreach (string name in new[] { "api.github.com", "github.com", "objects.fixture.test", "publisher.fixture.test" })
        {
            names.AddDnsName(name);
        }
        request.CertificateExtensions.Add(names.Build());
        _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        _listener.Start();
        _accept = AcceptAsync();
        string tools = Path.GetDirectoryName(typeof(LoopbackReleaseServer).Assembly.Location)!;
        while (!File.Exists(Path.Combine(tools, "scripts", "launcher-tls-loopback.py")))
        {
            tools = Path.GetDirectoryName(tools) ?? throw new IOException("The maintained TLS fixture helper was not found.");
        }
        string root = Path.Combine(Path.GetDirectoryName(tools)!, "Library", "Omnipatching", "launcher-preparation", $"tls-{Guid.NewGuid():N}");
        _root = root;
        Directory.CreateDirectory(root);
        string certificate = Path.Combine(root, "test-certificate.pem");
        string privateKey = Path.Combine(root, "test-key.pem");
        File.WriteAllText(certificate, _certificate.ExportCertificatePem());
        File.WriteAllText(privateKey, key.ExportPkcs8PrivateKeyPem());
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python.exe" : "python3")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-u", Path.Combine(tools, "scripts", "launcher-tls-loopback.py"),
            "--certificate", certificate, "--key", privateKey, "--backend-port", ((IPEndPoint)_listener.LocalEndpoint).Port.ToString() })
        {
            start.ArgumentList.Add(argument);
        }
        _tls = Process.Start(start) ?? throw new IOException("The owned TLS fixture child did not start.");
        _tlsStarted = _tls.StartTime.ToUniversalTime();
        try
        {
            string ready = _tls.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult()
                ?? throw new IOException("The owned TLS fixture did not report its port.");
            _tls.Refresh();
            _tlsExecutable = _tls.MainModule?.FileName ?? throw new IOException("The owned TLS child image was not observable after readiness.");
            using var document = JsonDocument.Parse(ready);
            _tlsPort = document.RootElement.GetProperty("port").GetInt32();
            File.WriteAllText(Path.Combine(root, "owner.json"), JsonSerializer.Serialize(new
                { ProcessId = _tls.Id, StartedUtc = _tlsStarted, Executable = _tlsExecutable,
                    Helper = start.ArgumentList[1], ExplicitTestKey = true }));
        }
        catch
        {
            _tls.StandardInput.Close();
            _stop.Cancel();
            _listener.Stop();
            throw;
        }
    }

    /// <summary>
    /// Create a real HTTPS handler restricted to this listener and this ephemeral certificate.
    /// </summary>
    /// <returns>The handler requiring no installed certificate or host configuration.</returns>
    internal SocketsHttpHandler Handler() => new()
    {
        UseProxy = false,
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null &&
                certificate.GetCertHashString(HashAlgorithmName.SHA256) == _certificate.GetCertHashString(HashAlgorithmName.SHA256)
        },
        ConnectCallback = async (context, ct) =>
        {
            if (context.DnsEndPoint.Port != 443 || context.DnsEndPoint.Host is not
                ("api.github.com" or "github.com" or "objects.fixture.test" or "publisher.fixture.test"))
                throw new InvalidOperationException("The owned transport fixture forbids external connections.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, _tlsPort), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Add(ServeAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        await using (NetworkStream tls = client.GetStream())
        {
            try
            {
                using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
                string first = await reader.ReadLineAsync(_stop.Token) ?? throw new IOException("Missing request line.");
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int length = first.Length;
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                {
                    length += line.Length;
                    if (length > 65536)
                        throw new IOException("Fixture headers exceeded their bound.");
                    int separator = line.IndexOf(':');
                    headers.Add(line[..separator], line[(separator + 1)..].Trim());
                }
                var observed = new Request(headers["Host"], first.Split(' ')[1],
                    headers.GetValueOrDefault("Authorization") == $"Bearer {TEST_TOKEN}", headers.GetValueOrDefault("Range"));
                Requests.Enqueue(observed);
                await Respond(observed, tls, _stop.Token);
            }
            catch (IOException) { }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
    }

    /// <summary>
    /// Send an HTTP response on the real TLS connection.
    /// </summary>
    /// <param name="stream">The accepted TLS stream.</param>
    /// <param name="body">The response payload.</param>
    /// <param name="status">The HTTP status.</param>
    /// <param name="ct">The fixture lifetime token.</param>
    /// <param name="headers">The additional response headers.</param>
    /// <param name="declaredLength">The optional declared length for interruption cases.</param>
    /// <returns>The response task.</returns>
    internal static async Task ReplyAsync(Stream stream, byte[] body, int status, CancellationToken ct,
        string headers = "", int? declaredLength = null)
    {
        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Length: {declaredLength ?? body.Length}\r\nConnection: close\r\n{headers}\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_connections);
        _tls.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _tls.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!_tls.HasExited && _tls.StartTime.ToUniversalTime() == _tlsStarted && _tls.MainModule!.FileName == _tlsExecutable)
                _tls.Kill();
            await _tls.WaitForExitAsync();
        }
        File.WriteAllText(Path.Combine(_root, "exit.json"), JsonSerializer.Serialize(new
            { ProcessId = _tls.Id, StartedUtc = _tlsStarted, Executable = _tlsExecutable, _tls.ExitCode }));
        _tls.Dispose();
        _certificate.Dispose();
        _stop.Dispose();
    }
}
