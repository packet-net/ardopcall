using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Ardopcall;

/// <summary>The subcommands, one method each, sharing one setup sequence.</summary>
/// <remarks>
/// <para>The rule ardopcall inherits from axcall: a flag left unset is never sent
/// to the TNC, so the TNC's own default governs and ardopcall never silently
/// restates it. That is why the setup sequence below is full of
/// <c>if (... is not null)</c> and not full of defaults.</para>
/// <para>The two exceptions are deliberate and both are about safety rather than
/// tuning. PROTOCOLMODE is always set, because "whatever mode the last host left
/// it in" is not a state anyone should transmit from. LISTEN is always set,
/// because leaving it where it was could have the TNC answer an inbound call,
/// and therefore transmit, during a run the operator started for some other
/// reason entirely.</para>
/// </remarks>
internal static class ArdopCommands
{
    /// <summary>
    /// Attempts for an ARQ call when <c>--attempts</c> is not given. Unlike the
    /// tuning flags this cannot be left out: ARQCALL takes the count as a
    /// mandatory parameter, so ardopcall has to name one. Five is ardopcf's own
    /// ConReq repeat default.
    /// </summary>
    internal const int DefaultConnectAttempts = 5;

    /// <summary>
    /// Attempts for a ping when <c>--attempts</c> is not given, mandatory for the
    /// same reason. Lower than a call because a ping is a probe: two frames is
    /// enough to learn whether a path exists, and a probe that keeps keying the
    /// transmitter on a shared channel is the thing this tool is trying not to
    /// be.
    /// </summary>
    internal const int DefaultPingAttempts = 2;

    // The TNC answers every command in the setup sequence, so this only has to
    // cover a slow link to the TNC, not a slow radio path.
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);

    // A ping is one frame out and one frame back; 15 s per attempt is generous
    // for every ARDOP frame type at every bandwidth.
    private static readonly TimeSpan PingWaitPerAttempt = TimeSpan.FromSeconds(15);

    // A ceiling on the ARQ dial, for the case where the TNC never says anything
    // at all. The TNC gives up on its own long before this and says so.
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(120);

    // A FEC transmission is as long as the payload makes it.
    private static readonly TimeSpan FecCompletionWait = TimeSpan.FromMinutes(10);

    // How long to wait for the transmitter to unkey before giving up on a clean
    // exit and dropping the sockets.
    private static readonly TimeSpan UnkeyWait = TimeSpan.FromSeconds(30);

    // How long to wait for a burst to start after the command that should have
    // started it. Only reached on a TNC that does not report PTT at all, since
    // ours and ardopcf both do.
    private static readonly TimeSpan KeyupWait = TimeSpan.FromSeconds(10);

    // How long the interrupt path waits for the TNC to acknowledge an ABORT.
    // Short on purpose: the operator has already asked for this to stop.
    private static readonly TimeSpan AbortAckWait = TimeSpan.FromSeconds(2);

    /// <summary>Attaches to the TNC, runs the chosen subcommand, and returns the exit code.</summary>
    internal static async Task<int> RunAsync(ParsedArgs args, CancellationToken ct)
    {
        TranscriptLog? log = null;
        try
        {
            if (args.LogPath is not null)
            {
                log = TranscriptLog.Open(args.LogPath);
                await Console.Error.WriteLineAsync($"ardopcall: transcript: {args.LogPath}").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail($"cannot open transcript {args.LogPath}: {ex.Message}", 2);
        }

        try
        {
            if (args.Command == ArdopSubcommand.Tee)
            {
                return await TeeProxy.RunAsync(args, log, ct).ConfigureAwait(false);
            }

            ArdopHostClient client;
            try
            {
                client = await ArdopHostClient.ConnectAsync(args.TncHost, args.TncPort, log, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                return Fail($"cannot attach to {args.TncHost}:{args.TncPort}: {ex.Message}", 3);
            }

            await using (client.ConfigureAwait(false))
            {
                await Console.Error.WriteLineAsync(
                    $"ardopcall: attached to {args.TncHost}:{args.TncPort} (data socket {args.TncPort + 1})").ConfigureAwait(false);
                AttachOperatorView(client, dataToStdout: args.Command == ArdopSubcommand.Monitor);

                // abort changes nothing, so it neither reads the station's
                // settings nor owes it a restore: it sends one command and goes.
                TncStateGuard? guard = args.Command == ArdopSubcommand.Abort
                    ? null
                    : await TncStateGuard.CaptureAsync(client, log, ct).ConfigureAwait(false);

                int code;
                try
                {
                    code = args.Command switch
                    {
                        ArdopSubcommand.Monitor => await RunMonitorAsync(client, args, guard!, ct).ConfigureAwait(false),
                        ArdopSubcommand.Listen => await RunListenAsync(client, args, guard!, log, ct).ConfigureAwait(false),
                        ArdopSubcommand.Ping => await RunPingAsync(client, args, guard!, log, ct).ConfigureAwait(false),
                        ArdopSubcommand.Id => await RunIdAsync(client, args, guard!, log, ct).ConfigureAwait(false),
                        ArdopSubcommand.Fec => await RunFecAsync(client, args, guard!, log, ct).ConfigureAwait(false),
                        ArdopSubcommand.Connect => await RunConnectAsync(client, args, guard!, log, ct).ConfigureAwait(false),
                        ArdopSubcommand.Abort => await RunAbortAsync(client, args, ct).ConfigureAwait(false),
                        _ => Fail($"unimplemented subcommand: {args.Command}", 1),
                    };
                }
                catch (OperationCanceledException)
                {
                    // Ctrl-C, with the radio possibly keyed. ABORT has to go out
                    // before the sockets close, because closing them is not a
                    // stop: the TNC reads a lost host as a request for an
                    // orderly ARQ disconnect and keys up repeating DISC frames.
                    // The settings go back afterwards, below.
                    await TryAbortAsync(client).ConfigureAwait(false);
                    code = 0;
                }
                catch (IOException ex)
                {
                    code = Fail($"the TNC dropped the connection: {ex.Message}", 3);
                }

                // Always, on every path: put the station back the way it was
                // found. A run that changed someone else's TNC and said nothing
                // is worse than a run that failed.
                if (guard is not null && !await guard.RestoreAsync(client).ConfigureAwait(false))
                {
                    return Fail("the TNC has not been put back as it was found; see the warnings above", 6);
                }

                return code;
            }
        }
        finally
        {
            log?.Dispose();
        }
    }

    /// <summary>
    /// stderr is the operator's view of the link: every line the TNC says, as it
    /// says it. Nothing is filtered, because on a first transmission the line
    /// ardopcall did not think was interesting is exactly the one that explains
    /// what happened.
    /// </summary>
    private static void AttachOperatorView(ArdopHostClient client, bool dataToStdout)
    {
        client.LineReceived += (_, line) => Console.Error.WriteLine($"  < {line}");
        client.DataReceived += (_, block) =>
        {
            Console.Error.WriteLine($"  < [{block.Tag}] {block.Payload.Length} bytes");

            // ERR is a frame that failed to decode: its bytes are noise by
            // definition, so they stay out of the peer-data stream on stdout.
            if (dataToStdout && block.Tag != ArdopDataBlock.Err)
            {
                Console.Out.Write(SessionRelay.RenderReceivedText(block.Payload));
                Console.Out.Flush();
            }
        };
    }

    private static async Task<int> RunMonitorAsync(ArdopHostClient client, ParsedArgs args, TncStateGuard guard, CancellationToken ct)
    {
        // RXO is receive-only by construction: the TNC refuses ARQCALL and PING
        // from it, so the default subcommand cannot key the transmitter even if
        // ardopcall is wrong about something.
        if (!await SetupAsync(client, args, guard, "RXO", listen: false, ct).ConfigureAwait(false))
        {
            return 5;
        }

        await Console.Error.WriteLineAsync("ardopcall: monitoring in PROTOCOLMODE RXO, receive only, nothing will be transmitted").ConfigureAwait(false);
        await Console.Error.WriteLineAsync("ardopcall: press Ctrl-C to stop").ConfigureAwait(false);

        await client.Closed.WaitAsync(ct).ConfigureAwait(false);
        await Console.Error.WriteLineAsync("ardopcall: the TNC closed the connection").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunListenAsync(ArdopHostClient client, ParsedArgs args, TncStateGuard guard, TranscriptLog? log, CancellationToken ct)
    {
        if (!await SetupAsync(client, args, guard, "ARQ", listen: true, ct).ConfigureAwait(false))
        {
            return 5;
        }

        Announce(client, log, $"nothing yet, but this station will answer an inbound ARQ call as {args.MyCall}, and answering transmits");
        await Console.Error.WriteLineAsync("ardopcall: listening, press Ctrl-C to stop").ConfigureAwait(false);

        using LineWaiter connected = client.Expect(n => n is ArdopConnected);
        ArdopNotification notification;
        try
        {
            notification = await connected.Line.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return Fail("the TNC closed the connection while listening", 3);
        }

        var session = (ArdopConnected)notification;
        await Console.Error.WriteLineAsync($"ardopcall: connected to {session.Call}{Bandwidth(session)}").ConfigureAwait(false);
        return await new SessionRelay(client).RelayAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> RunPingAsync(ArdopHostClient client, ParsedArgs args, TncStateGuard guard, TranscriptLog? log, CancellationToken ct)
    {
        if (!await SetupAsync(client, args, guard, "ARQ", listen: false, ct).ConfigureAwait(false))
        {
            return 5;
        }

        int attempts = args.Attempts ?? DefaultPingAttempts;
        Announce(client, log, $"a PING to {args.Target}, up to {attempts} attempts, as {args.MyCall}");

        using LineWaiter outcome = client.Expect(n => n is ArdopPingAck or ArdopFault);
        int keyups = client.KeyupCount;
        await client.SendCommandAsync($"PING {args.Target} {attempts}", ct).ConfigureAwait(false);

        TimeSpan wait = (PingWaitPerAttempt * attempts) + ReplyTimeout;
        ArdopNotification? answer = await AwaitAsync(outcome, wait, ct).ConfigureAwait(false);
        await SettleAsync(client, keyups, expectBurst: false, ct).ConfigureAwait(false);

        switch (answer)
        {
            case ArdopPingAck ack:
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"PINGACK from {args.Target}: S/N {Show(ack.SnrDb)} dB, quality {Show(ack.Quality)}"));
                return 0;
            case ArdopFault fault:
                return Fail($"the TNC refused the ping: {fault.Text}", 5);
            default:
                return Fail($"no PINGACK from {args.Target} after {attempts} attempts", 4);
        }
    }

    private static async Task<int> RunIdAsync(ArdopHostClient client, ParsedArgs args, TncStateGuard guard, TranscriptLog? log, CancellationToken ct)
    {
        if (!await SetupAsync(client, args, guard, "ARQ", listen: false, ct).ConfigureAwait(false))
        {
            return 5;
        }

        Announce(client, log, $"one ID frame as {args.MyCall}");

        using LineWaiter outcome = client.Expect(n => n is ArdopFault || ArdopHostClient.Keyword("SENDID")(n));
        int keyups = client.KeyupCount;
        await client.SendCommandAsync("SENDID", ct).ConfigureAwait(false);

        ArdopNotification? reply = await AwaitAsync(outcome, ReplyTimeout, ct).ConfigureAwait(false);
        if (reply is ArdopFault fault)
        {
            return Fail($"the TNC refused SENDID: {fault.Text}", 5);
        }

        // Hold the sockets until the burst is over. The TNC acknowledges SENDID
        // before it keys, so letting go on the acknowledgement would truncate
        // the ID mid-transmission.
        await SettleAsync(client, keyups, expectBurst: true, ct).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunFecAsync(ArdopHostClient client, ParsedArgs args, TncStateGuard guard, TranscriptLog? log, CancellationToken ct)
    {
        if (!await SetupAsync(client, args, guard, "FEC", listen: false, ct).ConfigureAwait(false))
        {
            return 5;
        }

        if (args.FecMode is not null && !await SendSetupAsync(client, $"FECMODE {args.FecMode}", guard, ct).ConfigureAwait(false))
        {
            return 5;
        }

        byte[] payload = await ReadFecPayloadAsync(args.FecText!, ct).ConfigureAwait(false);
        if (payload.Length == 0)
        {
            return Fail("nothing to send: the FEC payload is empty", 2);
        }

        Announce(client, log, $"a connectionless FEC transmission of {payload.Length} bytes as {args.MyCall}");

        // The payload has to be queued before FECSEND TRUE: a TNC with an empty
        // FEC buffer faults the command rather than starting a transmission.
        using LineWaiter queued = client.Expect(n => n is ArdopBuffer { Bytes: > 0 });
        await client.SendDataAsync(payload, ct).ConfigureAwait(false);
        if (await AwaitAsync(queued, ReplyTimeout, ct).ConfigureAwait(false) is null)
        {
            await Console.Error.WriteLineAsync("ardopcall: the TNC never reported the data queued; sending FECSEND anyway").ConfigureAwait(false);
        }

        using LineWaiter finished = client.Expect(n => n is ArdopNewState { State: "DISC" });
        int keyups = client.KeyupCount;
        if (!await SendSetupAsync(client, "FECSEND TRUE", guard: null, ct).ConfigureAwait(false))
        {
            return 5;
        }

        await Console.Error.WriteLineAsync("ardopcall: transmitting, waiting for the TNC to come back to DISC; Ctrl-C aborts").ConfigureAwait(false);
        if (await AwaitAsync(finished, FecCompletionWait, ct).ConfigureAwait(false) is null)
        {
            await Console.Error.WriteLineAsync("ardopcall: the TNC never came back to DISC; stopping the transmission").ConfigureAwait(false);
            await client.SendCommandAsync("FECSEND FALSE", ct).ConfigureAwait(false);
        }

        await SettleAsync(client, keyups, expectBurst: true, ct).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunConnectAsync(ArdopHostClient client, ParsedArgs args, TncStateGuard guard, TranscriptLog? log, CancellationToken ct)
    {
        if (!await SetupAsync(client, args, guard, "ARQ", listen: false, ct).ConfigureAwait(false))
        {
            return 5;
        }

        int attempts = args.Attempts ?? DefaultConnectAttempts;
        Announce(client, log, $"an ARQ call to {args.Target}, up to {attempts} attempts, as {args.MyCall}{BandwidthRequest(args)}");

        using LineWaiter outcome = client.Expect(
            n => n is ArdopConnected or ArdopRejectedBw or ArdopRejectedBusy or ArdopDisconnected or ArdopFault);
        int keyups = client.KeyupCount;
        await client.SendCommandAsync($"ARQCALL {args.Target} {attempts}", ct).ConfigureAwait(false);

        ArdopNotification? answer = await AwaitAsync(outcome, ConnectWait, ct).ConfigureAwait(false);
        switch (answer)
        {
            case ArdopConnected session:
                await Console.Error.WriteLineAsync($"ardopcall: connected to {session.Call}{Bandwidth(session)}").ConfigureAwait(false);
                return await new SessionRelay(client).RelayAsync(ct).ConfigureAwait(false);
            case ArdopRejectedBw rejected:
                await SettleAsync(client, keyups, expectBurst: false, ct).ConfigureAwait(false);
                return Fail($"{rejected.Call} refused the call on bandwidth", 4);
            case ArdopRejectedBusy rejected:
                await SettleAsync(client, keyups, expectBurst: false, ct).ConfigureAwait(false);
                return Fail($"{rejected.Call} refused the call, busy", 4);
            case ArdopFault fault:
                return Fail($"the TNC refused the call: {fault.Text}", 5);
            case ArdopDisconnected:
                await SettleAsync(client, keyups, expectBurst: false, ct).ConfigureAwait(false);
                return Fail($"{args.Target} did not answer", 4);
            default:
                await SettleAsync(client, keyups, expectBurst: false, ct).ConfigureAwait(false);
                return Fail($"the TNC said nothing about the call to {args.Target}", 4);
        }
    }

    /// <summary>
    /// The setup sequence, in the order the host protocol documents it. Returns
    /// false when the TNC faulted something, which is always worth stopping for:
    /// carrying on after a rejected MYCALL or PROTOCOLMODE would transmit under
    /// conditions nobody asked for.
    /// </summary>
    private static async Task<bool> SetupAsync(ArdopHostClient client, ParsedArgs args, TncStateGuard guard, string protocolMode, bool listen, CancellationToken ct)
    {
        // INITIALIZE resets the TNC, and a reset cannot be undone by putting
        // settings back, so it is off unless the operator asks for it. A monitor
        // run has no business resetting a station it does not own, and the
        // command line refuses the flag there at all.
        if (args.Initialize && !await SendSetupAsync(client, "INITIALIZE", guard, ct).ConfigureAwait(false))
        {
            return false;
        }

        if (!await SendSetupAsync(client, $"PROTOCOLMODE {protocolMode}", guard, ct).ConfigureAwait(false))
        {
            return false;
        }

        if (args.MyCall is not null && !await SendSetupAsync(client, $"MYCALL {args.MyCall}", guard, ct).ConfigureAwait(false))
        {
            return false;
        }

        if (args.Grid is not null && !await SendSetupAsync(client, $"GRIDSQUARE {args.Grid}", guard, ct).ConfigureAwait(false))
        {
            return false;
        }

        if (args.Bandwidth is { } bw)
        {
            string qualifier = args.Forced ? "FORCED" : "MAX";
            if (!await SendSetupAsync(client, $"ARQBW {bw}{qualifier}", guard, ct).ConfigureAwait(false))
            {
                return false;
            }

            await ReportArqBwAsync(client, ct).ConfigureAwait(false);
        }

        if (args.ArqTimeoutSeconds is { } timeout &&
            !await SendSetupAsync(client, $"ARQTIMEOUT {timeout}", guard, ct).ConfigureAwait(false))
        {
            return false;
        }

        return await SendSetupAsync(client, listen ? "LISTEN TRUE" : "LISTEN FALSE", guard, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads ARQBW back with a bare query and prints what the TNC says.
    /// </summary>
    /// <remarks>
    /// The acknowledgement of a set command says what the TNC was told, not what
    /// it has. What goes on the air is the second of those, and on a coordinated
    /// slot the difference is the whole point, so ardopcall asks and shows the
    /// answer rather than reporting back the number the operator typed.
    /// </remarks>
    private static async Task ReportArqBwAsync(ArdopHostClient client, CancellationToken ct)
    {
        using LineWaiter readback = client.Expect(n => n is ArdopFault || ArdopHostClient.Keyword("ARQBW")(n));
        await client.SendCommandAsync("ARQBW", ct).ConfigureAwait(false);
        ArdopNotification? actual = await AwaitAsync(readback, ReplyTimeout, ct).ConfigureAwait(false);
        await Console.Error.WriteLineAsync(actual is null
            ? "ardopcall: the TNC did not answer a bare ARQBW, so the bandwidth that will go on air is unverified"
            : $"ardopcall: the TNC reports {actual.Raw}").ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one ABORT and nothing else: no INITIALIZE, no PROTOCOLMODE, no
    /// MYCALL. Anything else this did would be another thing that could fail
    /// before the transmitter stopped.
    /// </summary>
    private static async Task<int> RunAbortAsync(ArdopHostClient client, ParsedArgs args, CancellationToken ct)
    {
        if (args.MyCall is not null)
        {
            await Console.Error.WriteLineAsync("ardopcall: ignoring --mycall: abort sends one command and changes nothing else").ConfigureAwait(false);
        }

        using LineWaiter reply = client.Expect(n => n is ArdopFault || ArdopHostClient.Keyword("ABORT")(n));
        await client.SendCommandAsync("ABORT", ct).ConfigureAwait(false);

        ArdopNotification? answer = await AwaitAsync(reply, ReplyTimeout, ct).ConfigureAwait(false);
        switch (answer)
        {
            case ArdopFault fault:
                return Fail($"the TNC refused ABORT: {fault.Text}", 5);
            case null:
                return Fail("no acknowledgement of ABORT from the TNC", 5);
            default:
                await Console.Error.WriteLineAsync("ardopcall: the TNC acknowledged ABORT").ConfigureAwait(false);
                return 0;
        }
    }

    /// <summary>
    /// Sends one setup command and waits for the TNC to answer it. A FAULT stops
    /// the run; silence does not, because a foreign TNC that answers fewer
    /// commands than ours is exactly the kind of difference ardopcall exists to
    /// show rather than to enforce.
    /// </summary>
    private static async Task<bool> SendSetupAsync(ArdopHostClient client, string line, TncStateGuard? guard, CancellationToken ct)
    {
        string keyword = ArdopNotification.KeywordOf(line);
        using LineWaiter waiter = client.Expect(n => n is ArdopFault || ArdopHostClient.Keyword(keyword)(n));
        await client.SendCommandAsync(line, ct).ConfigureAwait(false);

        ArdopNotification? reply = await AwaitAsync(waiter, ReplyTimeout, ct).ConfigureAwait(false);
        switch (reply)
        {
            case ArdopFault fault:
                await Console.Error.WriteLineAsync($"ardopcall: the TNC refused \"{line}\": {fault.Text}").ConfigureAwait(false);
                return false;
            case null:
                // Unacknowledged is not refused: a foreign TNC that answers
                // fewer commands than ours is a difference to show, not to
                // enforce. Count it as changed, since it may well have been.
                await Console.Error.WriteLineAsync($"ardopcall: no acknowledgement of \"{line}\" from this TNC, carrying on").ConfigureAwait(false);
                guard?.NoteChanged(keyword);
                return true;
            default:
                guard?.NoteChanged(keyword);
                return true;
        }
    }

    /// <summary>
    /// Waits for a registered line, returning null if it does not turn up in
    /// time. A faulted waiter (the socket closed) is left to the caller.
    /// </summary>
    private static async Task<ArdopNotification?> AwaitAsync(LineWaiter waiter, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await waiter.Line.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Waits for the transmitter to unkey, if the TNC has said it is keyed.
    /// Returns at once otherwise, including on a TNC that never reports PTT at
    /// all.
    /// </summary>
    private static async Task SettleAsync(ArdopHostClient client, int keyupsBefore, bool expectBurst, CancellationToken ct)
    {
        // Register both waiters before testing anything. The other order loses a
        // race it cannot win: a PTT arriving in between would leave this waiting
        // for a notification that had already been and gone.
        using LineWaiter unkeyed = client.Expect(n => n is ArdopPtt { Keyed: false });
        using LineWaiter keyed = client.Expect(n => n is ArdopPtt { Keyed: true });

        if (!client.TransmitterKeyed)
        {
            if (!expectBurst || client.KeyupCount > keyupsBefore)
            {
                // Nothing is keyed and nothing is owed. Either the burst has
                // been and gone, or the caller already has its answer: a PINGACK
                // can only arrive while receiving, so the exchange that produced
                // it is over.
                return;
            }

            // Nothing has keyed yet, and something should. A command's reply
            // comes back before the transmission starts, so this is the normal
            // case for SENDID and for FECSEND, and returning here would drop the
            // sockets mid-burst.
            if (await AwaitAsync(keyed, KeyupWait, ct).ConfigureAwait(false) is null)
            {
                // Either it never keyed, or this TNC does not report PTT. Both
                // are out of ardopcall's hands.
                return;
            }
        }

        if (await AwaitAsync(unkeyed, UnkeyWait, ct).ConfigureAwait(false) is null)
        {
            await Console.Error.WriteLineAsync("ardopcall: the transmitter is still keyed; sending ABORT rather than just closing, which would not stop it").ConfigureAwait(false);
            await TryAbortAsync(client).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The interrupt path: ABORT, then a short wait for the TNC to acknowledge
    /// it, then let the caller close the sockets.
    /// </summary>
    /// <remarks>
    /// The wait is short and best-effort, and it is not a retry loop: a second
    /// Ctrl-C takes the process down without it. What it must not do is skip the
    /// ABORT, because dropping the sockets on their own does not stop an ARQ
    /// session; the TNC treats a lost host as a request for an orderly
    /// disconnect and transmits repeating DISC frames.
    /// </remarks>
    private static async Task TryAbortAsync(ArdopHostClient client)
    {
        await Console.Error.WriteLineAsync("ardopcall: aborting transmit").ConfigureAwait(false);
        try
        {
            using LineWaiter reply = client.Expect(ArdopHostClient.Keyword("ABORT"));
            using var abortCts = new CancellationTokenSource(AbortAckWait);
            await client.SendCommandAsync("ABORT", abortCts.Token).ConfigureAwait(false);
            if (await AwaitAsync(reply, AbortAckWait, CancellationToken.None).ConfigureAwait(false) is null)
            {
                await Console.Error.WriteLineAsync("ardopcall: the TNC did not acknowledge the ABORT").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            Console.Error.WriteLine($"ardopcall: could not send ABORT: {ex.Message}");
        }
    }

    private static async Task<byte[]> ReadFecPayloadAsync(string text, CancellationToken ct)
    {
        if (text != "-")
        {
            // Sent exactly as given, with no terminator invented: a FEC
            // transmission is a broadcast of bytes, not a line of text.
            return Encoding.UTF8.GetBytes(text);
        }

        await using Stream stdin = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        await stdin.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>
    /// Says what is about to go out on the air, under which callsign, before it
    /// goes out. This is the whole safety story of the tool, so it is plain text
    /// on stderr and in the transcript, and it is never conditional.
    /// </summary>
    private static void Announce(ArdopHostClient client, TranscriptLog? log, string what)
    {
        Console.Error.WriteLine($"ardopcall: about to transmit: {what}");
        if (!client.BusyEverReported)
        {
            Console.Error.WriteLine("ardopcall: this TNC has reported no channel-busy state, so nothing here checked whether the channel was in use");
        }

        log?.Note($"about to transmit: {what}");
    }

    private static string Bandwidth(ArdopConnected session)
        => session.BandwidthHz is { } hz ? string.Create(CultureInfo.InvariantCulture, $" at {hz} Hz") : string.Empty;

    private static string BandwidthRequest(ParsedArgs args)
        => args.Bandwidth is { } bw
            ? string.Create(CultureInfo.InvariantCulture, $", bandwidth {bw}{(args.Forced ? "FORCED" : "MAX")}")
            : string.Empty;

    private static string Show(int? value)
        => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "not reported";

    private static int Fail(string message, int code)
    {
        Console.Error.WriteLine($"ardopcall: {message}");
        return code;
    }
}
