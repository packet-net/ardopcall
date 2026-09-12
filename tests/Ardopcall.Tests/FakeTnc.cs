using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ardopcall.Tests;

/// <summary>
/// An in-process stand-in for a TNC that speaks the ardopcf host protocol over
/// real loopback sockets: a command socket, a data socket on the port after it,
/// CR-terminated lines one way and length-prefixed tagged blocks the other.
/// </summary>
/// <remarks>
/// Real sockets rather than an in-memory seam, because the framing bugs worth
/// catching here are the ones that only appear when a TCP stack splits or
/// coalesces writes. Everything a test waits for is a
/// <see cref="TaskCompletionSource"/> with a generous timeout: nothing in this
/// suite sleeps, and nothing decides anything by the clock.
/// </remarks>
internal sealed class FakeTnc : IAsyncDisposable
{
    /// <summary>Generous enough that a loaded CI box cannot fail it, short enough to fail a hang.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly TcpListener commandListener;
    private readonly TcpListener dataListener;
    private readonly CancellationTokenSource cts = new();
    private readonly Lock gate = new();
    private readonly List<string> receivedLines = [];
    private readonly List<byte[]> receivedBlocks = [];
    private readonly List<(Func<string, bool> Predicate, TaskCompletionSource<string> Completion)> lineWaiters = [];
    private readonly List<TaskCompletionSource<byte[]>> blockWaiters = [];
    // A station as a real one is found: in ARQ, listening, with a callsign and
    // a bandwidth already set. Tests that care about a particular starting
    // state change this before connecting.
    private readonly Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PROTOCOLMODE"] = "ARQ",
        ["LISTEN"] = "TRUE",
        ["MYCALL"] = "G8BPQ",
        ["GRIDSQUARE"] = "IO91",
        ["ARQBW"] = "2000MAX",
        ["ARQTIMEOUT"] = "120",
        ["FECMODE"] = "4FSK.500.100",
    };
    private readonly TaskCompletionSource commandReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource dataReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TcpClient? commandClient;
    private TcpClient? dataClient;
    private bool disposed;
    private Task commandAccept = Task.CompletedTask;
    private Task dataAccept = Task.CompletedTask;

    private FakeTnc(int commandPort, TcpListener commandListener, TcpListener dataListener)
    {
        CommandPort = commandPort;
        this.commandListener = commandListener;
        this.dataListener = dataListener;
    }

    /// <summary>The command port. The data port is always this plus one.</summary>
    internal int CommandPort { get; }

    /// <summary>Every command line the host has sent, in order.</summary>
    internal IReadOnlyList<string> ReceivedLines
    {
        get
        {
            lock (gate)
            {
                return [.. receivedLines];
            }
        }
    }

    /// <summary>Replaces the default script with a test's own handler.</summary>
    internal Func<FakeTnc, string, Task>? Script { get; set; }

    /// <summary>
    /// The station's settings, as the query form reports them. Change these
    /// before the host connects to test against a particular starting state; an
    /// empty value means the setting is unset, which is what GB7RDG's MYCALL
    /// looks like.
    /// </summary>
    internal Dictionary<string, string> Settings => settings;

    /// <summary>
    /// Binds a free command/data port pair on loopback. The pair has to be
    /// adjacent, so port 0 is no use here and a free pair is searched for
    /// instead.
    /// </summary>
    internal static FakeTnc Start()
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            int port = Random.Shared.Next(20000, 60000);
            TcpListener? command = null;
            TcpListener? data = null;
            try
            {
                command = new TcpListener(IPAddress.Loopback, port);
                command.Start();
                data = new TcpListener(IPAddress.Loopback, port + 1);
                data.Start();
                var tnc = new FakeTnc(port, command, data);
                tnc.Begin();
                return tnc;
            }
            catch (SocketException)
            {
                command?.Stop();
                data?.Stop();
            }
        }

        throw new InvalidOperationException("no free adjacent loopback port pair");
    }

    private void Begin()
    {
        commandAccept = Task.Run(ServeCommandAsync, CancellationToken.None);
        dataAccept = Task.Run(ServeDataAsync, CancellationToken.None);
    }

    /// <summary>Waits for a command line matching the predicate, whenever it arrives.</summary>
    internal Task<string> ExpectLineAsync(Func<string, bool> predicate)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            string? already = receivedLines.Find(l => predicate(l));
            if (already is not null)
            {
                completion.SetResult(already);
            }
            else
            {
                lineWaiters.Add((predicate, completion));
            }
        }

        return completion.Task;
    }

    /// <summary>Waits for the next data block the host sends on the data socket.</summary>
    internal Task<byte[]> ExpectDataAsync()
    {
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            blockWaiters.Add(completion);
        }

        return completion.Task;
    }

    /// <summary>Sends one CR-terminated command line to the host.</summary>
    internal async Task SendLineAsync(string line)
    {
        await commandReady.Task.WaitAsync(Timeout).ConfigureAwait(false);
        byte[] bytes = Encoding.ASCII.GetBytes(line + "\r");
        await commandClient!.GetStream().WriteAsync(bytes).ConfigureAwait(false);
    }

    /// <summary>Sends one tagged data block: [2-byte BE length including the tag][tag][payload].</summary>
    internal async Task SendDataAsync(string tag, byte[] payload)
    {
        await dataReady.Task.WaitAsync(Timeout).ConfigureAwait(false);
        var framed = new byte[payload.Length + 5];
        int length = payload.Length + 3;
        framed[0] = (byte)(length >> 8);
        framed[1] = (byte)length;
        Encoding.ASCII.GetBytes(tag, framed.AsSpan(2, 3));
        payload.CopyTo(framed, 5);
        await dataClient!.GetStream().WriteAsync(framed).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one tagged block split across several writes, so the host has to
    /// reassemble it out of whatever the TCP stack delivers.
    /// </summary>
    internal async Task SendDataInPiecesAsync(string tag, byte[] payload, int pieces)
    {
        await dataReady.Task.WaitAsync(Timeout).ConfigureAwait(false);
        var framed = new byte[payload.Length + 5];
        int length = payload.Length + 3;
        framed[0] = (byte)(length >> 8);
        framed[1] = (byte)length;
        Encoding.ASCII.GetBytes(tag, framed.AsSpan(2, 3));
        payload.CopyTo(framed, 5);

        int size = Math.Max(1, framed.Length / pieces);
        NetworkStream stream = dataClient!.GetStream();
        for (int offset = 0; offset < framed.Length; offset += size)
        {
            int take = Math.Min(size, framed.Length - offset);
            await stream.WriteAsync(framed.AsMemory(offset, take)).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
    }

    private async Task ServeCommandAsync()
    {
        try
        {
            commandClient = await commandListener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
            commandClient.NoDelay = true;
            commandReady.TrySetResult();

            var assembler = new CommandLineAssembler();
            var buffer = new byte[4096];
            NetworkStream stream = commandClient.GetStream();
            while (true)
            {
                int got = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                if (got == 0)
                {
                    return;
                }

                foreach (string line in assembler.Append(buffer.AsSpan(0, got)))
                {
                    Record(line);
                    Func<FakeTnc, string, Task> script = Script ?? DefaultReplyAsync;
                    await script(this, line).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task ServeDataAsync()
    {
        try
        {
            dataClient = await dataListener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
            dataClient.NoDelay = true;
            dataReady.TrySetResult();

            var assembler = new DataBlockAssembler(tagged: false);
            var buffer = new byte[4096];
            NetworkStream stream = dataClient.GetStream();
            while (true)
            {
                int got = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                if (got == 0)
                {
                    return;
                }

                foreach (ArdopDataBlock block in assembler.Append(buffer.AsSpan(0, got)))
                {
                    RecordBlock(block.Payload);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// What a TNC does with the commands ardopcall sends, close enough to
    /// M0LTE.Ardop's own replies to be worth testing against: a set command is
    /// acknowledged "NAME now VALUE", a bare command echoes its name, and
    /// ARQCALL and PING echo the whole line before anything happens on the air.
    /// </summary>
    internal static async Task DefaultReplyAsync(FakeTnc tnc, string line)
    {
        string keyword = ArdopNotification.KeywordOf(line).ToUpperInvariant();

        // A bare command name is the query form; a name with a space after it is
        // the set form, even when what follows the space is nothing. The two are
        // different commands and telling them apart is the whole of the restore
        // path's behaviour on an unset value.
        bool hasParameter = line.Length > keyword.Length;
        string rest = hasParameter ? line[(keyword.Length + 1)..] : string.Empty;

        switch (keyword)
        {
            case "PING":
                await tnc.SendLineAsync(line).ConfigureAwait(false);
                await tnc.SendLineAsync("PINGACK 10 85").ConfigureAwait(false);
                break;
            case "ARQCALL":
                await tnc.SendLineAsync(line).ConfigureAwait(false);
                await tnc.SendLineAsync("STATUS ARQ connection established").ConfigureAwait(false);
                await tnc.SendLineAsync($"CONNECTED {rest.Split(' ')[0]} 500").ConfigureAwait(false);
                break;
            case "DISCONNECT":
                await tnc.SendLineAsync("DISCONNECTED").ConfigureAwait(false);
                break;
            case "SENDID":
                await tnc.SendLineAsync("SENDID").ConfigureAwait(false);
                break;
            case "ABORT":
                await tnc.SendLineAsync("ABORT").ConfigureAwait(false);
                break;
            case "INITIALIZE" or "PURGEBUFFER":
                await tnc.SendLineAsync(keyword).ConfigureAwait(false);
                break;
            default:
                if (!hasParameter)
                {
                    // Query form, answered "<NAME> <value>". An unset value
                    // still gets its space, exactly as the library does.
                    await tnc.SendLineAsync($"{keyword} {tnc.settings.GetValueOrDefault(keyword, string.Empty)}").ConfigureAwait(false);
                }
                else if (rest.Length == 0 && keyword is "MYCALL" or "GRIDSQUARE")
                {
                    // A callsign or a locator cannot be set to nothing: the
                    // library's parse of an empty string fails, so the host
                    // protocol has no way to clear either of them.
                    await tnc.SendLineAsync($"FAULT Syntax Err: {keyword} : maximum length exceeded or unsupported format").ConfigureAwait(false);
                }
                else
                {
                    tnc.settings[keyword] = rest;
                    await tnc.SendLineAsync($"{keyword} now {rest}").ConfigureAwait(false);
                }

                break;
        }
    }

    private void Record(string line)
    {
        List<TaskCompletionSource<string>> matched = [];
        lock (gate)
        {
            receivedLines.Add(line);
            for (int i = lineWaiters.Count - 1; i >= 0; i--)
            {
                if (lineWaiters[i].Predicate(line))
                {
                    matched.Add(lineWaiters[i].Completion);
                    lineWaiters.RemoveAt(i);
                }
            }
        }

        foreach (TaskCompletionSource<string> completion in matched)
        {
            completion.TrySetResult(line);
        }
    }

    private void RecordBlock(byte[] payload)
    {
        List<TaskCompletionSource<byte[]>> matched = [];
        lock (gate)
        {
            receivedBlocks.Add(payload);
            matched.AddRange(blockWaiters);
            blockWaiters.Clear();
        }

        foreach (TaskCompletionSource<byte[]> completion in matched)
        {
            completion.TrySetResult(payload);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await cts.CancelAsync().ConfigureAwait(false);
        commandClient?.Close();
        dataClient?.Close();
        commandListener.Stop();
        dataListener.Stop();
        try
        {
            await Task.WhenAll(commandAccept, dataAccept).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        commandClient?.Dispose();
        dataClient?.Dispose();
        cts.Dispose();
    }
}
