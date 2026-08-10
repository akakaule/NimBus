using System.Net;
using System.Net.Sockets;

namespace NimBus.ServiceBusEmulator.Hosting;

internal enum FrontendProtocol
{
    Incomplete,
    Invalid,
    Amqp,
    Http,
}

internal static class ProtocolClassifier
{
    private static readonly byte[][] HttpPrefixes =
    [
        "GET "u8.ToArray(),
        "PUT "u8.ToArray(),
        "POST "u8.ToArray(),
        "DELETE "u8.ToArray(),
        "HEAD "u8.ToArray(),
        "OPTIONS "u8.ToArray(),
        "PATCH "u8.ToArray(),
    ];

    public static FrontendProtocol Classify(ReadOnlySpan<byte> prefix)
    {
        ReadOnlySpan<byte> amqpMagic = "AMQP"u8;
        if (prefix.Length <= amqpMagic.Length && amqpMagic[..prefix.Length].SequenceEqual(prefix))
        {
            return FrontendProtocol.Incomplete;
        }

        if (prefix.Length >= 4 && prefix[..4].SequenceEqual(amqpMagic))
        {
            if (prefix.Length < 8)
            {
                return FrontendProtocol.Incomplete;
            }

            return (prefix[4] is 0 or 3) && prefix[5] == 1 && prefix[6] == 0 && prefix[7] == 0
                ? FrontendProtocol.Amqp
                : FrontendProtocol.Invalid;
        }

        var couldBeHttp = false;
        foreach (var candidate in HttpPrefixes)
        {
            if (prefix.Length >= candidate.Length && prefix[..candidate.Length].SequenceEqual(candidate))
            {
                return FrontendProtocol.Http;
            }

            if (prefix.Length < candidate.Length && candidate.AsSpan(0, prefix.Length).SequenceEqual(prefix))
            {
                couldBeHttp = true;
            }
        }

        return couldBeHttp ? FrontendProtocol.Incomplete : FrontendProtocol.Invalid;
    }
}

internal sealed class TcpMultiplexer : IAsyncDisposable
{
    private const int MaxPrefixLength = 8;
    private readonly TcpListener _listener;
    private readonly IPEndPoint _amqpEndpoint;
    private readonly IPEndPoint _httpEndpoint;
    private readonly SemaphoreSlim _connectionSlots;
    private readonly TimeSpan _classificationTimeout;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;

    public TcpMultiplexer(
        int publicPort,
        IPEndPoint amqpEndpoint,
        IPEndPoint httpEndpoint,
        int maxConnections = 256,
        TimeSpan? classificationTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(publicPort);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 1);
        _listener = new TcpListener(IPAddress.Loopback, publicPort);
        _amqpEndpoint = EnsureLoopback(amqpEndpoint);
        _httpEndpoint = EnsureLoopback(httpEndpoint);
        _connectionSlots = new SemaphoreSlim(maxConnections, maxConnections);
        _classificationTimeout = classificationTimeout ?? TimeSpan.FromSeconds(5);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _connectionSlots.Dispose();
        _shutdown.Dispose();
    }

    private static IPEndPoint EnsureLoopback(IPEndPoint endpoint) =>
        IPAddress.IsLoopback(endpoint.Address)
            ? endpoint
            : throw new ArgumentException("Emulator backends must be loopback endpoints.", nameof(endpoint));

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!_connectionSlots.Wait(0, CancellationToken.None))
            {
                client.Dispose();
                continue;
            }

            _ = ProxyAsync(client, cancellationToken);
        }
    }

    private async Task ProxyAsync(TcpClient inbound, CancellationToken shutdownToken)
    {
        try
        {
            using (inbound)
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken))
            {
                deadline.CancelAfter(_classificationTimeout);
                var inboundStream = inbound.GetStream();
                var prefix = new byte[MaxPrefixLength];
                var length = 0;
                FrontendProtocol protocol;
                do
                {
                    var count = await inboundStream.ReadAsync(prefix.AsMemory(length, 1), deadline.Token).ConfigureAwait(false);
                    if (count == 0)
                    {
                        return;
                    }

                    length += count;
                    protocol = ProtocolClassifier.Classify(prefix.AsSpan(0, length));
                    NimBus.ServiceBusEmulator.Protocol.EmulatorDiagnostics.Write(
                        "TCP prefix",
                        Convert.ToHexString(prefix.AsSpan(0, length)));
                }
                while (protocol == FrontendProtocol.Incomplete && length < prefix.Length);

                if (protocol is FrontendProtocol.Invalid or FrontendProtocol.Incomplete)
                {
                    return;
                }

                using var backend = new TcpClient(AddressFamily.InterNetwork);
                var target = protocol == FrontendProtocol.Amqp ? _amqpEndpoint : _httpEndpoint;
                await backend.ConnectAsync(target, deadline.Token).ConfigureAwait(false);
                var backendStream = backend.GetStream();
                await backendStream.WriteAsync(prefix.AsMemory(0, length), deadline.Token).ConfigureAwait(false);
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);

                var upstream = inboundStream.CopyToAsync(backendStream, shutdownToken);
                var downstream = backendStream.CopyToAsync(inboundStream, shutdownToken);
                var first = await Task.WhenAny(upstream, downstream).ConfigureAwait(false);
                if (first == upstream)
                {
                    backend.Client.Shutdown(SocketShutdown.Send);
                    await downstream.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
                else
                {
                    inbound.Client.Shutdown(SocketShutdown.Send);
                    await upstream.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            // A disconnected, timed-out, or malformed client is isolated to its connection.
        }
        finally
        {
            _connectionSlots.Release();
        }
    }
}
