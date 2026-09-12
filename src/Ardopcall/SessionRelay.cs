using System.Text;

namespace Ardopcall;

/// <summary>
/// Pumps an operator's terminal through a connected ARQ session: stdin becomes
/// the data ardopcall queues for transmission, and the peer's data becomes
/// stdout.
/// </summary>
/// <remarks>
/// <para>Line-oriented in, byte-faithful out. stdin is read a line at a time and
/// each line is sent with a single CR terminator, because that is what every
/// packet-radio correspondent on the far end of an ARQ session expects and it is
/// what axcall does over AX.25. Received payloads are written as they arrive,
/// with CR and CRLF translated to LF so a received line renders as a line break
/// instead of overwriting the previous one on the terminal.</para>
/// <para>ardopcall does not frame, chunk on boundaries, or interpret the session
/// data in any other way: there is no B2F, no compression and no Winlink here,
/// deliberately.</para>
/// </remarks>
internal sealed class SessionRelay(ArdopHostClient client, TextReader? input = null, TextWriter? output = null)
{
    // How long to let a transmit buffer drain after end of input before giving
    // up and disconnecting anyway. Long, because an ARQ link at 200 Hz in poor
    // conditions is slow, and this is data the operator typed.
    private static readonly TimeSpan DrainWait = TimeSpan.FromMinutes(10);

    private readonly TextReader input = input ?? Console.In;
    private readonly TextWriter output = output ?? Console.Out;

    /// <summary>
    /// Runs until the session drops or stdin ends, and returns the process exit
    /// code. stdin ending asks for an orderly DISCONNECT; a dropped session
    /// stops the stdin reader.
    /// </summary>
    internal async Task<int> RelayAsync(CancellationToken ct)
    {
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnData(object? sender, ArdopDataBlock block)
        {
            // ERR blocks are a failed FEC frame, not session data, and IDF is a
            // decoded ID: neither belongs in the peer's byte stream on stdout.
            if (block.Tag is ArdopDataBlock.Err or ArdopDataBlock.Idf)
            {
                return;
            }

            output.Write(RenderReceivedText(block.Payload));
            output.Flush();
        }

        void OnNotification(object? sender, ArdopNotification notification)
        {
            if (notification is ArdopDisconnected)
            {
                disconnected.TrySetResult();
            }
        }

        client.DataReceived += OnData;
        client.NotificationReceived += OnNotification;
        try
        {
            using var stdinCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task stdinTask = Task.Run(() => ReadStdinAsync(stdinCts.Token), CancellationToken.None);

            // Either end may finish first: the operator closing stdin, or the
            // link dropping under them.
            Task finished = await Task.WhenAny(stdinTask, disconnected.Task, client.Closed).ConfigureAwait(false);

            if (finished == stdinTask && !ct.IsCancellationRequested)
            {
                await DrainAsync(disconnected.Task, ct).ConfigureAwait(false);
                await Console.Error.WriteLineAsync("ardopcall: end of input, disconnecting").ConfigureAwait(false);
                await client.SendCommandAsync("DISCONNECT", CancellationToken.None).ConfigureAwait(false);
                using var teardown = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try
                {
                    await disconnected.Task.WaitAsync(teardown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await Console.Error.WriteLineAsync("ardopcall: no DISCONNECTED from the TNC, giving up on the orderly teardown").ConfigureAwait(false);
                }
            }
            else
            {
                await stdinCts.CancelAsync().ConfigureAwait(false);
            }

            // An interrupt has to leave as an interrupt, not as a tidy return:
            // the caller's handler is what sends ABORT, and an ARQ session that
            // is only closed rather than aborted goes on transmitting DISC
            // frames.
            ct.ThrowIfCancellationRequested();
            return 0;
        }
        finally
        {
            client.DataReceived -= OnData;
            client.NotificationReceived -= OnNotification;
        }
    }

    /// <summary>
    /// Waits for the TNC to report its transmit buffer empty before the
    /// disconnect goes in.
    /// </summary>
    /// <remarks>
    /// Found by running the thing: reaching end of input and asking to
    /// disconnect in the same breath tears the session down with the last line
    /// still queued, and on a slow ARQ link that is most of what was typed. The
    /// wait is bounded and best-effort: a TNC that never sends BUFFER, or one
    /// whose buffer never empties, must not leave the operator stuck in a
    /// session they have already left.
    /// </remarks>
    private async Task DrainAsync(Task disconnected, CancellationToken ct)
    {
        // Register before reading the count, so a BUFFER 0 arriving in between
        // is not missed.
        using LineWaiter drained = client.Expect(n => n is ArdopBuffer { Bytes: 0 });
        int queued = client.BufferedBytes;
        if (queued <= 0)
        {
            return;
        }

        await Console.Error.WriteLineAsync($"ardopcall: waiting for {queued} queued bytes to go out").ConfigureAwait(false);
        try
        {
            // The session dropping under us is just as good an answer as the
            // buffer emptying: either way there is nothing left to send.
            await Task.WhenAny(drained.Line, disconnected).WaitAsync(DrainWait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await Console.Error.WriteLineAsync("ardopcall: the transmit buffer has not emptied; disconnecting anyway").ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The socket went while we waited; the disconnect below will fail
            // the same way and be reported there.
        }
    }

    private async Task ReadStdinAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (line is null)
            {
                return;
            }

            await client.SendDataAsync(Encoding.UTF8.GetBytes(line + "\r"), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Decodes a received payload as text for the terminal. Packet data is
    /// CR-terminated; CR and CRLF become LF so received lines render as real
    /// line breaks rather than carriage returns that overwrite the current line.
    /// A lone LF, and text with no terminator at all, pass through untouched: no
    /// newline is invented.
    /// </summary>
    internal static string RenderReceivedText(ReadOnlySpan<byte> payload)
        => Encoding.UTF8.GetString(payload)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
}
