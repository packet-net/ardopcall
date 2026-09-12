using System.Net;
using System.Net.Sockets;

namespace Ardopcall;

/// <summary>
/// Sits between a host (LinBPQ, Pat) and a real TNC, passing every byte through
/// untouched and writing down what it saw.
/// </summary>
/// <remarks>
/// <para>This is a wire tap, not a proxy with opinions. Bytes are copied in both
/// directions exactly as they arrive: nothing is reframed, rebuffered into
/// different block boundaries, re-encoded, held back or retried. The transcript
/// is produced by a <b>separate</b> observer fed a copy of the same bytes, and if
/// that observer ever fails to make sense of the stream it falls back to logging
/// raw hex and the relay carries on regardless. A tee that could corrupt the
/// session it is observing would be worse than no tee at all.</para>
/// <para>The two sockets are handled independently, exactly as the TNC handles
/// them, each with its own accept loop: a host that drops and reconnects (LinBPQ
/// does) gets served again without restarting ardopcall.</para>
/// </remarks>
internal static class TeeProxy
{
    private const int BufferBytes = 8192;

    internal static async Task<int> RunAsync(ParsedArgs args, TranscriptLog? log, CancellationToken ct)
    {
        int localPort = args.LocalPort!.Value;

        // Bound to every interface, like the TNC this is standing in for: the
        // host being observed is often on another box, and pointing it at a
        // loopback-only tee would just look like a dead TNC.
        var commandListener = new TcpListener(IPAddress.Any, localPort);
        var dataListener = new TcpListener(IPAddress.Any, localPort + 1);
        try
        {
            commandListener.Start();
            dataListener.Start();
        }
        catch (SocketException ex)
        {
            commandListener.Stop();
            dataListener.Stop();
            Console.Error.WriteLine($"ardopcall: cannot listen on {localPort} and {localPort + 1}: {ex.Message}");
            return 3;
        }

        await Console.Error.WriteLineAsync(
            $"ardopcall: tee listening on {localPort} and {localPort + 1}, forwarding to {args.TncHost}:{args.TncPort} and {args.TncPort + 1}").ConfigureAwait(false);
        await Console.Error.WriteLineAsync("ardopcall: point the host at this port pair; press Ctrl-C to stop").ConfigureAwait(false);
        log?.Note($"tee {localPort}/{localPort + 1} -> {args.TncHost}:{args.TncPort}/{args.TncPort + 1}");

        try
        {
            await Task.WhenAll(
                ServeAsync(commandListener, args.TncHost, args.TncPort, commandChannel: true, log, ct),
                ServeAsync(dataListener, args.TncHost, args.TncPort + 1, commandChannel: false, log, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("ardopcall: tee stopped").ConfigureAwait(false);
        }
        finally
        {
            commandListener.Stop();
            dataListener.Stop();
        }

        return 0;
    }

    private static async Task ServeAsync(TcpListener listener, string upstreamHost, int upstreamPort, bool commandChannel, TranscriptLog? log, CancellationToken ct)
    {
        string channel = commandChannel ? "command" : "data";
        while (!ct.IsCancellationRequested)
        {
            TcpClient host = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            host.NoDelay = true;

            TcpClient upstream;
            try
            {
                upstream = new TcpClient { NoDelay = true };
                await upstream.ConnectAsync(upstreamHost, upstreamPort, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                Console.Error.WriteLine($"ardopcall: {channel} socket: host connected but the TNC would not: {ex.Message}");
                log?.Note($"{channel}: upstream connect failed: {ex.Message}");
                host.Close();
                continue;
            }

            Console.Error.WriteLine($"ardopcall: {channel} socket: host connected, relaying to {upstreamHost}:{upstreamPort}");
            log?.Note($"{channel}: host connected");

            try
            {
                await RelayPairAsync(host, upstream, commandChannel, log, ct).ConfigureAwait(false);
            }
            finally
            {
                host.Close();
                upstream.Close();
                host.Dispose();
                upstream.Dispose();
                Console.Error.WriteLine($"ardopcall: {channel} socket: session ended");
                log?.Note($"{channel}: session ended");
            }
        }
    }

    private static async Task RelayPairAsync(TcpClient host, TcpClient upstream, bool commandChannel, TranscriptLog? log, CancellationToken ct)
    {
        using var pairCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task toTnc = CopyAsync(host.GetStream(), upstream.GetStream(), new TeeObserver(log, LogDirection.ToTnc, commandChannel), pairCts.Token);
        Task fromTnc = CopyAsync(upstream.GetStream(), host.GetStream(), new TeeObserver(log, LogDirection.FromTnc, commandChannel), pairCts.Token);

        // One end closing takes the pair down, the way the TNC's own sockets
        // behave: a half-open tee would leave the host waiting on a TNC that has
        // already gone.
        await Task.WhenAny(toTnc, fromTnc).ConfigureAwait(false);
        await pairCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(toTnc, fromTnc).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    private static async Task CopyAsync(NetworkStream from, NetworkStream to, TeeObserver observer, CancellationToken ct)
    {
        var buffer = new byte[BufferBytes];
        try
        {
            while (true)
            {
                int got = await from.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (got == 0)
                {
                    return;
                }

                // Forward first, observe second: the observation must never be
                // able to delay or alter what the two ends see.
                await to.WriteAsync(buffer.AsMemory(0, got), ct).ConfigureAwait(false);
                observer.Observe(buffer.AsSpan(0, got));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Makes a readable transcript out of a copy of the bytes, and gets out of
    /// the way if it cannot.
    /// </summary>
    private sealed class TeeObserver
    {
        private readonly TranscriptLog? log;
        private readonly LogDirection direction;
        private readonly string channel;
        private readonly CommandLineAssembler? lines;
        private readonly DataBlockAssembler? blocks;
        private bool broken;

        internal TeeObserver(TranscriptLog? log, LogDirection direction, bool commandChannel)
        {
            this.log = log;
            this.direction = direction;
            channel = commandChannel ? "CMD " : "DATA";
            if (commandChannel)
            {
                lines = new CommandLineAssembler();
            }
            else
            {
                // The data socket is framed differently in each direction: the
                // host sends untagged blocks, the TNC sends tagged ones.
                blocks = new DataBlockAssembler(tagged: direction == LogDirection.FromTnc);
            }
        }

        internal void Observe(ReadOnlySpan<byte> bytes)
        {
            if (log is null)
            {
                return;
            }

            if (broken)
            {
                log.Raw(direction, channel, bytes);
                return;
            }

            try
            {
                if (lines is not null)
                {
                    foreach (string line in lines.Append(bytes))
                    {
                        log.Command(direction, line);
                    }
                }
                else
                {
                    foreach (ArdopDataBlock block in blocks!.Append(bytes))
                    {
                        log.Data(direction, block.Tag, block.Payload);
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                broken = true;
                log.Note($"{channel}: {ex.Message}: logging raw bytes from here on");
                log.Raw(direction, channel, bytes);
            }
        }
    }
}
