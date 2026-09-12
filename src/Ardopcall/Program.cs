using System.Globalization;
using System.Reflection;

namespace Ardopcall;

/// <summary>The verb this run performs. One per run, chosen by subcommand.</summary>
internal enum ArdopSubcommand
{
    /// <summary>Attach in RXO and print everything. The default, and the only one that cannot transmit.</summary>
    Monitor,

    /// <summary>Wait for an inbound ARQ session and relay it. Transmits only to answer.</summary>
    Listen,

    /// <summary>Send a ping and report the PINGACK.</summary>
    Ping,

    /// <summary>Send one ID frame.</summary>
    Id,

    /// <summary>Send a connectionless FEC transmission.</summary>
    Fec,

    /// <summary>Dial an ARQ session and relay it.</summary>
    Connect,

    /// <summary>Sit between a host and a TNC and log every byte.</summary>
    Tee,

    /// <summary>
    /// Send one ABORT and nothing else. The thing to run from a second terminal
    /// when the first one is wedged and the radio is still keyed.
    /// </summary>
    Abort,
}

/// <summary>Everything the command line settled, once it parsed cleanly.</summary>
/// <remarks>
/// The nullable members are null when their flag was not given, and ardopcall
/// then does not send the corresponding command at all, so the TNC's own default
/// governs and the tool never silently restates it. That is why these are
/// <c>int?</c> and <c>string?</c> rather than fields with defaults.
/// </remarks>
internal sealed record ParsedArgs(
    ArdopSubcommand Command,
    string TncHost,
    int TncPort,
    string? MyCall,
    string? Target,
    string? FecText,
    string? FecMode,
    int? Bandwidth,
    bool Forced,
    string? Grid,
    int? ArqTimeoutSeconds,
    int? Attempts,
    string? LogPath,
    int? LocalPort,
    bool Initialize);

public static class Program
{
    // ARQBW takes one of four bandwidths, in Hz; anything else is a syntax
    // error at the TNC, so it is worth catching at the command line where the
    // user can see the list.
    internal static readonly int[] Bandwidths = [200, 500, 1000, 2000];

    // ARQTIMEOUT's accepted range (ardopcf and M0LTE.Ardop agree: > 29 and < 241).
    internal const int MinArqTimeoutSeconds = 30;
    internal const int MaxArqTimeoutSeconds = 240;

    // An attempt count must be positive; the protocol sets no ceiling, and the
    // TNC enforces whatever policy it has, so this only bounds the obvious
    // mistakes.
    internal const int MaxAttempts = 255;

    // ARDOP callsigns are a base call plus an optional SSID. This is a
    // deliberately loose superset: the TNC is the authority on what it will
    // accept, and rejecting something here that a real ardopcf would have taken
    // would make ardopcall the thing under test.
    internal const int MaxCallsignLength = 10;

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--version") || args.Contains("-V"))
        {
            PrintVersion();
            return 0;
        }

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        if (ParseArgs(args) is not { } parsed)
        {
            return 2;
        }

        using var appCts = new CancellationTokenSource();
        int interrupts = 0;
        Console.CancelKeyPress += (_, e) =>
        {
            if (Interlocked.Increment(ref interrupts) == 1)
            {
                // First Ctrl-C: keep the process alive long enough to send
                // ABORT. Letting it die here would drop the sockets, and that
                // is not a stop: the TNC answers a lost host with an orderly
                // ARQ disconnect, which keys the transmitter again.
                e.Cancel = true;
                appCts.Cancel();
                return;
            }

            // Second Ctrl-C: the abort is evidently not completing, so stop
            // waiting for it and let the runtime take the process down.
            Console.Error.WriteLine("ardopcall: interrupted again, exiting without waiting for the abort");
            e.Cancel = false;
        };

        try
        {
            return await ArdopCommands.RunAsync(parsed, appCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("ardopcall: interrupted").ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ardopcall: fatal: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Parses and validates the command line. Returns null after printing a
    /// usage error to stderr, and the caller exits 2. --help and --version are
    /// handled before this runs.
    /// </summary>
    internal static ParsedArgs? ParseArgs(string[] args)
    {
        string? tnc = null;
        string? myCall = null;
        string? bandwidthArg = null;
        bool forced = false;
        string? grid = null;
        string? timeoutArg = null;
        string? attemptsArg = null;
        string? logPath = null;
        string? fecMode = null;
        string? localArg = null;
        bool initialize = false;
        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-t" or "--tnc":
                    if (++i >= args.Length) return Fail("missing value for --tnc");
                    tnc = args[i];
                    break;
                case "-s" or "--mycall":
                    if (++i >= args.Length) return Fail("missing value for --mycall");
                    myCall = args[i];
                    break;
                case "--bw":
                    if (++i >= args.Length) return Fail("missing value for --bw");
                    bandwidthArg = args[i];
                    break;
                case "--forced":
                    forced = true;
                    break;
                case "--initialize":
                    initialize = true;
                    break;
                case "--grid":
                    if (++i >= args.Length) return Fail("missing value for --grid");
                    grid = args[i];
                    break;
                case "--timeout":
                    if (++i >= args.Length) return Fail("missing value for --timeout");
                    timeoutArg = args[i];
                    break;
                case "--attempts":
                    if (++i >= args.Length) return Fail("missing value for --attempts");
                    attemptsArg = args[i];
                    break;
                case "--log":
                    if (++i >= args.Length) return Fail("missing value for --log");
                    logPath = args[i];
                    break;
                case "--fecmode":
                    if (++i >= args.Length) return Fail("missing value for --fecmode");
                    fecMode = args[i];
                    break;
                case "--local":
                    if (++i >= args.Length) return Fail("missing value for --local");
                    localArg = args[i];
                    break;
                default:
                    if (args[i].StartsWith('-') && args[i].Length > 1) return Fail($"unknown option: {args[i]}");
                    positional.Add(args[i]);
                    break;
            }
        }

        // No subcommand at all means monitor, which is the one that cannot
        // transmit. Getting this backwards is how tools key transmitters by
        // accident.
        ArdopSubcommand command = ArdopSubcommand.Monitor;
        var operands = new List<string>(positional);
        if (operands.Count > 0)
        {
            if (TryParseSubcommand(operands[0], out ArdopSubcommand parsedCommand))
            {
                command = parsedCommand;
                operands.RemoveAt(0);
            }
            else
            {
                return Fail($"unknown subcommand: {operands[0]}");
            }
        }

        if (tnc is null) return Fail("--tnc is required (e.g. -t localhost:8515)");
        if (ParseHostPort(tnc) is not { } endpoint) return null;

        int? bandwidth = null;
        if (bandwidthArg is not null)
        {
            if (!TryParseCount(bandwidthArg, out int bw) || Array.IndexOf(Bandwidths, bw) < 0)
                return Fail($"invalid bandwidth: {bandwidthArg} (must be one of {string.Join(", ", Bandwidths)})");
            bandwidth = bw;
        }

        if (forced && bandwidth is null) return Fail("--forced needs --bw: it says how to apply a bandwidth, not which one");

        int? timeout = null;
        if (timeoutArg is not null)
        {
            if (!TryParseCount(timeoutArg, out int t) || t < MinArqTimeoutSeconds || t > MaxArqTimeoutSeconds)
                return Fail($"invalid timeout: {timeoutArg} (must be {MinArqTimeoutSeconds}..{MaxArqTimeoutSeconds} seconds)");
            timeout = t;
        }

        int? attempts = null;
        if (attemptsArg is not null)
        {
            if (!TryParseCount(attemptsArg, out int a) || a < 1 || a > MaxAttempts)
                return Fail($"invalid attempts: {attemptsArg} (must be 1..{MaxAttempts})");
            attempts = a;
        }

        int? localPort = null;
        if (localArg is not null)
        {
            if (!TryParseCount(localArg, out int p) || p is < 1 or > 65534)
                return Fail($"invalid local port: {localArg} (1..65534, since the data socket is this port plus one)");
            localPort = p;
        }

        string? call = null;
        if (myCall is not null)
        {
            call = myCall.ToUpperInvariant();
            if (!IsPlausibleCallsign(call)) return Fail($"invalid callsign: {myCall}");
        }

        // -s is mandatory for everything that can key a transmitter, and it has
        // no default and no fallback. A tool that guesses a callsign transmits
        // under someone else's licence.
        bool transmits = command is ArdopSubcommand.Listen or ArdopSubcommand.Ping
            or ArdopSubcommand.Id or ArdopSubcommand.Fec or ArdopSubcommand.Connect;
        if (transmits && call is null) return Fail($"--mycall is required for {Name(command)}: it transmits");

        if (command == ArdopSubcommand.Tee && call is not null)
            return Fail("tee does not take --mycall: it never sends a command of its own, it only relays the host's");

        // ARQBW is the one deliberate exception to "a flag left unset is not
        // sent". The TNC's ARQBW is whatever its default or the last host left
        // it at, and that is not reliably what its own configuration asks for:
        // a live pdn-soundmodem reports 2000MAX while its config says 500,
        // because it never applies the configured value to the TNC. Inheriting
        // that silently would put a session four times too wide on a
        // coordinated 500 Hz slot, so ardopcall refuses to transmit without
        // being told.
        if (transmits && bandwidth is null)
            return Fail($"--bw is required for {Name(command)}: the TNC's own ARQBW is whatever its default or the last host left it at (a live station reports 2000MAX while its config asks for 500), and ardopcall will not put a session of unknown width on the air");

        string? target = null;
        string? fecText = null;
        switch (command)
        {
            case ArdopSubcommand.Ping or ArdopSubcommand.Connect:
                if (operands.Count != 1) return Fail($"{Name(command)} takes exactly one callsign");
                target = operands[0].ToUpperInvariant();
                if (!IsPlausibleCallsign(target)) return Fail($"invalid destination callsign: {operands[0]}");
                break;
            case ArdopSubcommand.Fec:
                if (operands.Count != 1) return Fail("fec takes exactly one argument: the text to send, or - for stdin");
                fecText = operands[0];
                break;
            case ArdopSubcommand.Tee:
                if (operands.Count != 0) return Fail($"unexpected argument: {operands[0]}");
                if (localPort is null) return Fail("tee needs --local <port>: the port pair the host connects to");
                break;
            case ArdopSubcommand.Abort:
                // Deliberately tolerant: this is the command someone types in a
                // hurry, at a keyed transmitter, probably by editing the line
                // above it in the shell history. Anything it does not need is
                // ignored with a note rather than refused.
                if (operands.Count != 0) return Fail($"unexpected argument: {operands[0]}");
                break;
            default:
                if (operands.Count != 0) return Fail($"unexpected argument: {operands[0]}");
                break;
        }

        if (fecMode is not null && command != ArdopSubcommand.Fec) return Fail("--fecmode only applies to fec");
        if (localPort is not null && command != ArdopSubcommand.Tee) return Fail("--local only applies to tee");

        // INITIALIZE resets the TNC, and no restore can undo a reset, so the
        // subcommands that exist to observe or to stop a station cannot ask for
        // one at all.
        if (initialize && !transmits)
            return Fail($"--initialize is not available for {Name(command)}: resetting a TNC is not something a receive-only run should do to a station it does not own");

        return new ParsedArgs(
            command, endpoint.Host, endpoint.Port, call, target, fecText, fecMode,
            bandwidth, forced, grid, timeout, attempts, logPath, localPort, initialize);
    }

    private static bool TryParseSubcommand(string text, out ArdopSubcommand command)
    {
        switch (text.ToUpperInvariant())
        {
            case "MONITOR": command = ArdopSubcommand.Monitor; return true;
            case "LISTEN": command = ArdopSubcommand.Listen; return true;
            case "PING": command = ArdopSubcommand.Ping; return true;
            case "ID": command = ArdopSubcommand.Id; return true;
            case "FEC": command = ArdopSubcommand.Fec; return true;
            case "CONNECT": command = ArdopSubcommand.Connect; return true;
            case "TEE": command = ArdopSubcommand.Tee; return true;
            case "ABORT": command = ArdopSubcommand.Abort; return true;
            default: command = ArdopSubcommand.Monitor; return false;
        }
    }

    private static string Name(ArdopSubcommand command) => command.ToString().ToLowerInvariant();

    /// <summary>
    /// A loose check that catches typing mistakes without pretending to know
    /// every callsign form a TNC will take.
    /// </summary>
    internal static bool IsPlausibleCallsign(string call)
    {
        if (call.Length is 0 or > MaxCallsignLength) return false;
        foreach (char c in call)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
        }

        return true;
    }

    // A plain unsigned whole number: no sign, whitespace, separators or exponent.
    private static bool TryParseCount(string s, out int value)
        => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static (string Host, int Port)? ParseHostPort(string arg)
    {
        int lastColon = arg.LastIndexOf(':');
        if (lastColon <= 0)
        {
            Fail($"expected host:port, got: {arg}");
            return null;
        }

        string host = arg[..lastColon];
        if (!TryParseCount(arg[(lastColon + 1)..], out int port) || port is < 1 or > 65534)
        {
            // 65535 is excluded because the data socket is always the command
            // port plus one, and that one would not exist.
            Fail($"invalid port in: {arg}");
            return null;
        }

        return (host, port);
    }

    // Usage-error form: report to stderr, then hand back "no result".
    private static ParsedArgs? Fail(string message)
    {
        Console.Error.WriteLine($"ardopcall: {message}");
        return null;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine($"""
            Usage: ardopcall [monitor] -t <host:port> [options]
                   ardopcall listen -t <host:port> -s <mycall> [options]
                   ardopcall ping <call> -t <host:port> -s <mycall> [options]
                   ardopcall id -t <host:port> -s <mycall> [options]
                   ardopcall fec <text|-> -t <host:port> -s <mycall> [--fecmode <frame>]
                   ardopcall connect <call> -t <host:port> -s <mycall> [options]
                   ardopcall tee -t <host:port> --local <port> [--log <path>]
                   ardopcall abort -t <host:port>

            Drives any ardopcf-compatible ARDOP 1 TNC over its TCP host interface.
            The data socket is always the command port plus one.

            Subcommands (monitor is the default, and the only one that never transmits):
              monitor            Attach in PROTOCOLMODE RXO and print every line and
                                 data block. Receive only, by construction.
              listen             Wait for an inbound ARQ session, then relay stdin and
                                 stdout. Transmits only to answer a call.
              ping <call>        Send a ping and report the PINGACK: the far station's
                                 reported S/N in dB and its quality figure, 0-100.
              id                 Send one ID frame.
              fec <text|->       Send a connectionless FEC transmission of the given
                                 text, or of stdin when the argument is -.
              connect <call>     Dial an ARQ session, then relay stdin and stdout until
                                 either end drops.
              tee                Sit between a host (LinBPQ, Pat) and the TNC and relay
                                 both sockets byte for byte, logging everything.
              abort              Send one ABORT and exit. Stops the transmitter now.
                                 Needs no callsign and changes nothing else, so it can
                                 be run from a second terminal at a wedged station.

            Options:
              -t, --tnc <host:port>  TNC command socket (required). ardopcf listens on
                                     8515 by default; pdn-soundmodem at GB7RDG on 8200.
              -s, --mycall <call>    Local callsign. Required for every subcommand that
                                     transmits, with no default and no fallback.
              --bw <hz>              Session bandwidth: {string.Join(" | ", Bandwidths)}. Required for
                                     every subcommand that transmits, and read back from
                                     the TNC afterwards so you see what will go on air.
              --forced               Send the bandwidth as FORCED rather than MAX, so
                                     the far end must match it. Needs --bw.
              --grid <locator>       Maidenhead locator to report (GRIDSQUARE).
              --timeout <seconds>    ARQ session timeout, {MinArqTimeoutSeconds}..{MaxArqTimeoutSeconds}. Left unset, the
                                     TNC's own ARQTIMEOUT governs.
              --attempts <n>         Attempts for ping and connect. Unlike the other
                                     flags this cannot be left to the TNC: the count is
                                     part of the command, so ardopcall defaults to
                                     {ArdopCommands.DefaultPingAttempts} for a ping and {ArdopCommands.DefaultConnectAttempts} for a call.
              --fecmode <frame>      FEC waveform for fec, e.g. 4FSK.500.100. Passed
                                     through unchecked: the TNC decides what it has.
              --initialize           Reset the TNC (INITIALIZE) before starting. Off by
                                     default. Do not use on a station you do not own: a
                                     reset is the one thing ardopcall cannot put back.
              --local <port>         For tee: the local command port to listen on. The
                                     local data port is this plus one.
              --log <path>           Append a timestamped transcript of every command
                                     line in both directions and every data block.
              -V, --version          Show version
              -h, --help             Show this help

            Safety:
              ardopcall borrows a station, it does not reconfigure one. PROTOCOLMODE,
              LISTEN, MYCALL, GRIDSQUARE, ARQBW, ARQTIMEOUT and FECMODE are read before
              anything is changed and put back on the way out, and a restore that fails
              is reported and exits 6. Nothing is reset unless you pass --initialize.
              Ctrl-C sends ABORT before disconnecting; closing the socket alone does not
              stop an ARQ session transmitting. Dropping the command socket asks the TNC
              for an orderly disconnect, and it then keys up repeating DISC frames until
              the far end answers, so ABORT is the only real stop. A second Ctrl-C exits
              at once, without waiting for the abort.
              Every transmitting subcommand says what it is about to send, and under
              which callsign, before it sends it. ardopcall performs no channel-busy
              check of its own, and says so, because the TNC may not have a busy
              detector either.

            Exit codes:
              0 success   2 usage   3 cannot reach the TNC   4 no answer on the air
              5 the TNC refused a command   6 the TNC was not put back as it was found
              1 anything else
            """);
    }

    private static void PrintVersion()
    {
        Console.WriteLine($"ardopcall {AsmVersion(typeof(Program))}");
        Console.WriteLine();
        Console.WriteLine("Speaks the ardopcf TCP host interface directly, over nothing but");
        Console.WriteLine("System.Net.Sockets. No ARDOP library is linked in, so the same binary");
        Console.WriteLine("drives a real ardopcf and any ardopcf-compatible TNC.");
    }

    // Read the assembly's informational version (the NuGet package version),
    // trimming any +commit-hash build-metadata suffix SourceLink appends.
    private static string AsmVersion(Type t)
    {
        Assembly asm = t.Assembly;
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            int plus = info.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? info[..plus] : info;
        }

        return asm.GetName().Version?.ToString() ?? "unknown";
    }
}
