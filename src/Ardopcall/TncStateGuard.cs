namespace Ardopcall;

/// <summary>
/// Remembers how the TNC was configured when ardopcall attached, and puts it
/// back that way when ardopcall leaves.
/// </summary>
/// <remarks>
/// <para>ardopcall borrows a station, it does not reconfigure one. Every setting
/// it sends persists after the process exits, and the TNC it is pointed at is
/// usually not its own: a node's TNC sits in PROTOCOLMODE ARQ with LISTEN TRUE
/// so the node can answer calls, and a run of <c>monitor</c> that left it in RXO
/// would silently stop that node answering anything, with nobody told.</para>
/// <para>So the values ardopcall is capable of changing are read back before
/// anything is changed, and only the ones it actually changed are restored. A
/// restore that fails is reported loudly and makes the run exit non-zero, because
/// a station left in a state its operator did not choose is the worst outcome
/// available here, and the operator finding out about it is the whole point.</para>
/// <para>MYCALL is the sharpest of these. Setting a callsign on a TNC with LISTEN
/// TRUE makes that station answer inbound calls addressed to it, from anyone, and
/// it stays that way. Note that the host protocol has no defined way to clear a
/// callsign: ardopcall sends the empty form and reports honestly when the TNC
/// refuses it, rather than pretending the station was left as it was found.</para>
/// </remarks>
internal sealed class TncStateGuard
{
    /// <summary>
    /// The settings ardopcall can change, in the order they are restored.
    /// PROTOCOLMODE and LISTEN come last on purpose: they are the pair that
    /// decides whether the station answers a call, so they go back only once the
    /// callsign has been dealt with.
    /// </summary>
    internal static readonly string[] Watched =
        ["MYCALL", "GRIDSQUARE", "ARQBW", "ARQTIMEOUT", "FECMODE", "PROTOCOLMODE", "LISTEN"];

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, string?> found = [];
    private readonly HashSet<string> changed = [];
    private readonly TranscriptLog? log;

    private TncStateGuard(TranscriptLog? log) => this.log = log;

    /// <summary>
    /// Reads the current value of everything ardopcall might change. Queries
    /// only: a bare command name is the query form in this protocol, so nothing
    /// here alters anything.
    /// </summary>
    internal static async Task<TncStateGuard> CaptureAsync(ArdopHostClient client, TranscriptLog? log, CancellationToken ct)
    {
        var guard = new TncStateGuard(log);
        foreach (string keyword in Watched)
        {
            guard.found[keyword] = await QueryAsync(client, keyword, ct).ConfigureAwait(false);
        }

        log?.Note($"state as found: {guard.Describe()}");
        return guard;
    }

    /// <summary>The value the TNC reported at attach, or null if it did not answer.</summary>
    internal string? Original(string keyword) => found.GetValueOrDefault(keyword);

    /// <summary>Records that ardopcall has changed this setting, so it owes a restore.</summary>
    internal void NoteChanged(string keyword)
    {
        if (Array.IndexOf(Watched, keyword) >= 0)
        {
            changed.Add(keyword);
        }
    }

    /// <summary>
    /// Puts back everything ardopcall changed. Returns false if any of it could
    /// not be put back, having said so on stderr.
    /// </summary>
    /// <remarks>
    /// Runs on every exit path, including the interrupt: it is called after the
    /// ABORT, so the transmitter stops first and the settings go back second.
    /// The cancellation token is deliberately not passed on. This work happens
    /// because the run is ending, and a cancelled token would cancel the very
    /// thing that puts the station right.
    /// </remarks>
    internal async Task<bool> RestoreAsync(ArdopHostClient client)
    {
        if (changed.Count == 0)
        {
            return true;
        }

        bool ok = true;
        var restored = new List<string>();
        foreach (string keyword in Watched)
        {
            if (!changed.Contains(keyword))
            {
                continue;
            }

            string? original = found[keyword];
            if (original is null)
            {
                ok = false;
                Console.Error.WriteLine(
                    $"ardopcall: WARNING: this TNC never reported its {keyword}, so ardopcall cannot put it back; the station is left with whatever ardopcall set");
                log?.Note($"restore failed: {keyword} was never reported");
                continue;
            }

            // An empty value means the setting was unset when we arrived. The
            // protocol's set form with an empty parameter is the only thing
            // there is to try, and a TNC is entitled to refuse it.
            string line = original.Length == 0 ? $"{keyword} " : $"{keyword} {original}";
            ArdopNotification? reply;
            try
            {
                reply = await SendAsync(client, line, keyword).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                ok = false;
                Console.Error.WriteLine(
                    $"ardopcall: WARNING: the connection to the TNC has gone, so {keyword} could not be put back; the station is left with whatever ardopcall set");
                log?.Note($"restore failed: {keyword}: {ex.Message}");
                continue;
            }

            switch (reply)
            {
                case null:
                    ok = false;
                    Console.Error.WriteLine($"ardopcall: WARNING: the TNC did not acknowledge \"{line}\", so its {keyword} may not be back as it was");
                    log?.Note($"restore unacknowledged: {line}");
                    break;
                case ArdopFault fault:
                    ok = false;
                    Console.Error.WriteLine($"ardopcall: WARNING: the TNC refused \"{line}\": {fault.Text}");
                    Console.Error.WriteLine(
                        original.Length == 0
                            ? $"ardopcall: WARNING: {keyword} was empty when ardopcall attached and the host protocol has no way to clear it; clear it at the TNC yourself"
                            : $"ardopcall: WARNING: {keyword} is not back at {original}; put it back at the TNC yourself");
                    log?.Note($"restore refused: {line}: {fault.Text}");
                    break;
                default:
                    restored.Add(original.Length == 0 ? $"cleared {keyword} (was empty)" : $"{keyword} {original}");
                    break;
            }
        }

        if (restored.Count > 0)
        {
            Console.Error.WriteLine($"ardopcall: restored {string.Join(", ", restored)}");
            log?.Note($"restored {string.Join(", ", restored)}");
        }

        return ok;
    }

    private string Describe()
        => string.Join(", ", Watched.Select(k => found.GetValueOrDefault(k) is { } v
            ? (v.Length == 0 ? $"{k} (empty)" : $"{k} {v}")
            : $"{k} (not reported)"));

    // The query form: a bare command name, answered as "<NAME> <value>". An
    // answer with nothing after the name means the setting is unset, which is
    // different from the TNC not answering at all, so the two are kept apart:
    // empty string against null.
    private static async Task<string?> QueryAsync(ArdopHostClient client, string keyword, CancellationToken ct)
    {
        ArdopNotification? reply;
        try
        {
            reply = await SendAsync(client, keyword, keyword, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            return null;
        }

        if (reply is null or ArdopFault)
        {
            return null;
        }

        string raw = reply.Raw;
        return raw.Length > keyword.Length ? raw[(keyword.Length + 1)..].Trim() : string.Empty;
    }

    private static async Task<ArdopNotification?> SendAsync(ArdopHostClient client, string line, string keyword, CancellationToken ct = default)
    {
        using LineWaiter waiter = client.Expect(n => n is ArdopFault || ArdopHostClient.Keyword(keyword)(n));
        await client.SendCommandAsync(line, ct).ConfigureAwait(false);
        try
        {
            return await waiter.Line.WaitAsync(ReplyTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
