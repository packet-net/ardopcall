using System.Net.Sockets;
using System.Text;

namespace Ardopcall;

/// <summary>
/// A live attachment to an ardopcf-compatible TNC: the command socket, the data
/// socket, and everything arriving on either of them.
/// </summary>
/// <remarks>
/// <para>This is the whole of ardopcall's dependency on ARDOP. It speaks the
/// ardopcf TCP host interface over <see cref="System.Net.Sockets"/> and nothing
/// else, so the same binary drives our managed TNC and a real ardopcf, and a
/// disagreement between them is a measurement rather than a guess.</para>
/// <para>The command socket is treated as an <b>event stream</b>, never as a
/// request/response channel. The protocol has no request identifier and no
/// framing that distinguishes a reply from an unsolicited notification, so a
/// caller that sent a command and then read "the next line" would eventually
/// read a PTT or a BUFFER notification and call it an answer. Instead a caller
/// registers a predicate with <see cref="Expect"/> <i>before</i> sending, and
/// the pump completes it when a matching line turns up whenever that is.</para>
/// </remarks>
internal sealed class ArdopHostClient : IAsyncDisposable
{
    /// <summary>ardopcf's default command port. pdn-soundmodem at GB7RDG uses 8200.</summary>
    internal const int DefaultCommandPort = 8515;

    /// <summary>
    /// The largest payload ardopcall puts in one host-to-TNC data block. The
    /// framing allows 65535, but a block is queued whole and the TNC reports its
    /// buffer in bytes, so smaller blocks give the operator a buffer figure that
    /// moves. 1 KiB is well inside every implementation's input buffer.
    /// </summary>
    internal const int MaxSendChunkBytes = 1024;

    private const int ReadBufferBytes = 8192;

    private readonly TcpClient commandSocket;
    private readonly TcpClient dataSocket;
    private readonly TranscriptLog? log;
    private readonly CancellationTokenSource cts = new();
    private readonly SemaphoreSlim commandWrite = new(1, 1);
    private readonly SemaphoreSlim dataWrite = new(1, 1);
    private readonly Lock waiterGate = new();
    private readonly List<LineWaiter> waiters = [];
    private readonly TaskCompletionSource closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task commandPump = Task.CompletedTask;
    private Task dataPump = Task.CompletedTask;
    private int shutdownFlag;

    private ArdopHostClient(string host, int commandPort, TcpClient commandSocket, TcpClient dataSocket, TranscriptLog? log)
    {
        Host = host;
        CommandPort = commandPort;
        this.commandSocket = commandSocket;
        this.dataSocket = dataSocket;
        this.log = log;
    }

    /// <summary>Every line the TNC sent, raw, with its CR already stripped.</summary>
    internal event EventHandler<string>? LineReceived;

    /// <summary>Every line the TNC sent, parsed.</summary>
    internal event EventHandler<ArdopNotification>? NotificationReceived;

    /// <summary>Every tagged data block the TNC sent.</summary>
    internal event EventHandler<ArdopDataBlock>? DataReceived;

    internal string Host { get; }

    internal int CommandPort { get; }

    /// <summary>The data socket is always the command port plus one. Not configurable, by the protocol.</summary>
    internal int DataPort => CommandPort + 1;

    /// <summary>Completes when either socket goes away, for any reason.</summary>
    internal Task Closed => closedTcs.Task;

    /// <summary>
    /// True once this TNC has said anything at all about channel-busy state.
    /// A TNC with no busy detector never sends BUSY, and ardopcall reports that
    /// absence rather than letting the operator read silence as "the channel is
    /// clear".
    /// </summary>
    internal bool BusyEverReported { get; private set; }

    /// <summary>
    /// Whether the last PTT notification said the transmitter was keyed.
    /// ardopcall waits for this to go false before it lets go of the sockets,
    /// because a burst the operator asked for should finish: losing the host
    /// aborts a FEC or ID transmission mid-frame, and turns an ARQ session into
    /// an orderly disconnect that keys up again to send DISC.
    /// </summary>
    internal bool TransmitterKeyed { get; private set; }

    /// <summary>
    /// How many times the TNC has reported the transmitter keying since this
    /// attachment opened. Asking "is it keyed now" is not enough on its own:
    /// PTT TRUE arrives after the command's own reply, so a caller that checked
    /// the flag the moment its command was acknowledged would find the
    /// transmitter idle and let go of the sockets just as the burst began.
    /// Counting keyups tells "the burst has not started yet" apart from "the
    /// burst has been and gone".
    /// </summary>
    internal int KeyupCount { get; private set; }

    /// <summary>
    /// Bytes the TNC last said it had queued for transmission. Zero until it
    /// says otherwise, and a TNC that never sends BUFFER leaves it there, which
    /// is why waiting on it is always bounded.
    /// </summary>
    internal int BufferedBytes { get; private set; }

    /// <summary>Opens both sockets and starts reading. The data socket is always command port + 1.</summary>
    internal static async Task<ArdopHostClient> ConnectAsync(string host, int commandPort, TranscriptLog? log, CancellationToken ct)
    {
        var command = new TcpClient();
        TcpClient? data = null;
        try
        {
            // NoDelay on both: every line and every block here is small and
            // latency-critical, and Nagle would coalesce a command with whatever
            // followed it and blur the timing the transcript is recording.
            command.NoDelay = true;
            await command.ConnectAsync(host, commandPort, ct).ConfigureAwait(false);

            data = new TcpClient { NoDelay = true };
            await data.ConnectAsync(host, commandPort + 1, ct).ConfigureAwait(false);
        }
        catch
        {
            command.Dispose();
            data?.Dispose();
            throw;
        }

        var client = new ArdopHostClient(host, commandPort, command, data, log);
        log?.Note($"attached to {host}:{commandPort} (data socket {commandPort + 1})");
        client.Start();
        return client;
    }

    private void Start()
    {
        commandPump = Task.Run(PumpCommandAsync, CancellationToken.None);
        dataPump = Task.Run(PumpDataAsync, CancellationToken.None);
    }

    /// <summary>
    /// Registers interest in a line before the command that provokes it is sent.
    /// Registering first is the point: the TNC can answer before
    /// <c>SendCommandAsync</c> has even returned, and a waiter created afterwards
    /// would miss it.
    /// </summary>
    internal LineWaiter Expect(Func<ArdopNotification, bool> predicate)
    {
        var waiter = new LineWaiter(this, predicate);
        lock (waiterGate)
        {
            waiters.Add(waiter);
        }

        // Losing the race with a socket that has already closed would hang the
        // caller until its timeout, so settle the waiter now instead.
        if (Volatile.Read(ref shutdownFlag) != 0)
        {
            waiter.Fail(new IOException("the TNC closed the command socket"));
        }

        return waiter;
    }

    /// <summary>
    /// Matches a line by its whole leading keyword, which is all the protocol
    /// gives us to match on. Whole keyword, not prefix: "PINGACK 10 85" starts
    /// with "PING" and is not a reply to a PING command.
    /// </summary>
    internal static Func<ArdopNotification, bool> Keyword(string keyword)
        => n => string.Equals(ArdopNotification.KeywordOf(n.Raw), keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>Sends one command line, CR-terminated, and records it in the transcript.</summary>
    internal async Task SendCommandAsync(string line, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\r");
        await commandWrite.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            log?.Command(LogDirection.ToTnc, line);
            await commandSocket.GetStream().WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            commandWrite.Release();
        }
    }

    /// <summary>
    /// Sends a payload on the data socket in the untagged host-to-TNC framing,
    /// split into blocks of at most <see cref="MaxSendChunkBytes"/>.
    /// </summary>
    internal async Task SendDataAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await dataWrite.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int offset = 0; offset < payload.Length; offset += MaxSendChunkBytes)
            {
                ReadOnlyMemory<byte> chunk = payload.Slice(offset, Math.Min(MaxSendChunkBytes, payload.Length - offset));
                log?.Data(LogDirection.ToTnc, string.Empty, chunk.Span);
                byte[] framed = ArdopFraming.FrameHostData(chunk.Span);
                await dataSocket.GetStream().WriteAsync(framed, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            dataWrite.Release();
        }
    }

    private async Task PumpCommandAsync()
    {
        var assembler = new CommandLineAssembler();
        var buffer = new byte[ReadBufferBytes];
        try
        {
            NetworkStream stream = commandSocket.GetStream();
            while (true)
            {
                int got = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                if (got == 0)
                {
                    break;
                }

                foreach (string line in assembler.Append(buffer.AsSpan(0, got)))
                {
                    Dispatch(line);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // A closed socket is how this loop normally ends.
        }
        catch (InvalidDataException ex)
        {
            log?.Note($"command socket: {ex.Message}");
            Console.Error.WriteLine($"ardopcall: command socket: {ex.Message}");
        }
        finally
        {
            Shutdown();
        }
    }

    private async Task PumpDataAsync()
    {
        var assembler = new DataBlockAssembler(tagged: true);
        var buffer = new byte[ReadBufferBytes];
        try
        {
            NetworkStream stream = dataSocket.GetStream();
            while (true)
            {
                int got = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                if (got == 0)
                {
                    break;
                }

                foreach (ArdopDataBlock block in assembler.Append(buffer.AsSpan(0, got)))
                {
                    log?.Data(LogDirection.FromTnc, block.Tag, block.Payload);
                    DataReceived?.Invoke(this, block);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }
        catch (InvalidDataException ex)
        {
            log?.Note($"data socket: {ex.Message}");
            Console.Error.WriteLine($"ardopcall: data socket: {ex.Message}");
        }
        finally
        {
            Shutdown();
        }
    }

    private void Dispatch(string line)
    {
        log?.Command(LogDirection.FromTnc, line);
        ArdopNotification notification = ArdopNotification.Parse(line);
        switch (notification)
        {
            case ArdopBusy:
                BusyEverReported = true;
                break;
            case ArdopPtt ptt:
                TransmitterKeyed = ptt.Keyed;
                if (ptt.Keyed)
                {
                    KeyupCount++;
                }

                break;
            case ArdopBuffer buffer:
                BufferedBytes = buffer.Bytes;
                break;
            default:
                break;
        }

        LineReceived?.Invoke(this, line);
        NotificationReceived?.Invoke(this, notification);

        LineWaiter[] matched;
        lock (waiterGate)
        {
            matched = [.. waiters.Where(w => w.Matches(notification))];
            foreach (LineWaiter waiter in matched)
            {
                waiters.Remove(waiter);
            }
        }

        foreach (LineWaiter waiter in matched)
        {
            waiter.Complete(notification);
        }
    }

    private void Shutdown()
    {
        if (Interlocked.Exchange(ref shutdownFlag, 1) != 0)
        {
            return;
        }

        LineWaiter[] orphaned;
        lock (waiterGate)
        {
            orphaned = [.. waiters];
            waiters.Clear();
        }

        // Anything still waiting can never be answered now, so fail it rather
        // than leave the caller sitting on a socket that has gone.
        var closedException = new IOException("the TNC closed the command socket");
        foreach (LineWaiter waiter in orphaned)
        {
            waiter.Fail(closedException);
        }

        closedTcs.TrySetResult();
    }

    internal void Remove(LineWaiter waiter)
    {
        lock (waiterGate)
        {
            waiters.Remove(waiter);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await cts.CancelAsync().ConfigureAwait(false);
        commandSocket.Close();
        dataSocket.Close();
        try
        {
            await Task.WhenAll(commandPump, dataPump).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The pumps already swallow the expected teardown exceptions; this
            // guards the unexpected ones so Dispose still finishes.
        }

        Shutdown();
        commandSocket.Dispose();
        dataSocket.Dispose();
        cts.Dispose();
        commandWrite.Dispose();
        dataWrite.Dispose();
    }
}

/// <summary>
/// A standing interest in one line from the TNC, registered before the command
/// that ought to provoke it goes out.
/// </summary>
internal sealed class LineWaiter : IDisposable
{
    private readonly ArdopHostClient owner;
    private readonly Func<ArdopNotification, bool> predicate;
    private readonly TaskCompletionSource<ArdopNotification> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal LineWaiter(ArdopHostClient owner, Func<ArdopNotification, bool> predicate)
    {
        this.owner = owner;
        this.predicate = predicate;
    }

    /// <summary>The matching line, once one arrives. Faults if the socket closes first.</summary>
    internal Task<ArdopNotification> Line => tcs.Task;

    internal bool Matches(ArdopNotification notification) => predicate(notification);

    internal void Complete(ArdopNotification notification) => tcs.TrySetResult(notification);

    internal void Fail(Exception exception) => tcs.TrySetException(exception);

    public void Dispose() => owner.Remove(this);
}
