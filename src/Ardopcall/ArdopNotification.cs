using System.Globalization;

namespace Ardopcall;

/// <summary>
/// One line the TNC sent on the command socket, parsed into the small typed
/// vocabulary ardopcall reasons about.
/// </summary>
/// <remarks>
/// <para>There is no request identifier anywhere in this protocol and no framing
/// that separates a reply from an unsolicited notification, so the command
/// socket is an event stream and a line's text is the only thing that says what
/// it is. Everything that is not in the vocabulary below stays an
/// <see cref="ArdopOtherLine"/> with its raw text intact: query and set replies
/// (<c>MYCALL M0LTE</c>, <c>ARQBW now 500MAX</c>), version banners, and anything
/// a future or foreign TNC invents. Nothing is ever discarded, because an
/// unrecognised line from one of the two implementations under test is a finding
/// rather than noise.</para>
/// <para>Every case keeps <see cref="Raw"/>, so the transcript and the operator
/// always see the exact text rather than ardopcall's reading of it.</para>
/// </remarks>
internal abstract record ArdopNotification(string Raw)
{
    /// <summary>
    /// The leading keyword of a line, which is the only thing the protocol gives
    /// anyone to match a reply on. Matching on the whole keyword and not on a
    /// prefix matters: "PING G8BPQ 2" is the echo of a command and
    /// "PINGACK 10 85" is a far station answering, and a prefix match on "PING"
    /// cannot tell them apart.
    /// </summary>
    internal static string KeywordOf(string line)
    {
        int space = line.IndexOf(' ');
        return space < 0 ? line : line[..space];
    }

    /// <summary>Splits a line into its keyword and the remainder, and classifies it.</summary>
    internal static ArdopNotification Parse(string line)
    {
        string keyword = KeywordOf(line);
        int space = line.IndexOf(' ');
        string rest = space < 0 ? string.Empty : line[(space + 1)..];

        switch (keyword.ToUpperInvariant())
        {
            case "CONNECTED":
            {
                // CONNECTED <call> <session bandwidth in Hz>.
                string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    break;
                }

                return new ArdopConnected(line, parts[0], parts.Length > 1 ? ParseInt(parts[1]) : null);
            }

            case "DISCONNECTED":
                return new ArdopDisconnected(line);

            case "PENDING":
                return new ArdopPending(line);

            case "CANCELPENDING":
                return new ArdopCancelPending(line);

            case "TARGET":
                if (rest.Length == 0)
                {
                    break;
                }

                return new ArdopTarget(line, rest.Trim());

            case "PINGACK":
            {
                // PINGACK <snDb> <quality>. Both are kept nullable: the values
                // come straight off a decoded frame and a TNC that did not fill
                // them in prints an empty field rather than omitting the line,
                // which is itself worth seeing.
                string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return new ArdopPingAck(
                    line,
                    parts.Length > 0 ? ParseInt(parts[0]) : null,
                    parts.Length > 1 ? ParseInt(parts[1]) : null);
            }

            case "REJECTEDBW":
                return new ArdopRejectedBw(line, rest.Trim());

            case "REJECTEDBUSY":
                return new ArdopRejectedBusy(line, rest.Trim());

            case "PTT":
                if (TryParseBool(rest, out bool keyed))
                {
                    return new ArdopPtt(line, keyed);
                }

                break;

            case "BUSY":
                // Part of the protocol, but a TNC without a busy detector never
                // sends it. ardopcall notices the absence rather than assuming
                // the channel is clear; see ArdopHostClient.BusyEverReported.
                if (TryParseBool(rest, out bool busy))
                {
                    return new ArdopBusy(line, busy);
                }

                break;

            case "BUFFER":
                if (ParseInt(rest.Trim()) is { } queued)
                {
                    return new ArdopBuffer(line, queued);
                }

                break;

            case "STATUS":
                return new ArdopStatus(line, rest);

            case "NEWSTATE":
                // ardopcf's NEWSTATE carries a trailing space ("NEWSTATE %s ")
                // which hosts work around; trim it here so callers compare
                // against the state name and not against the quirk.
                return new ArdopNewState(line, rest.Trim());

            case "FAULT":
                return new ArdopFault(line, rest);

            default:
                break;
        }

        return new ArdopOtherLine(line);
    }

    private static int? ParseInt(string s)
        => int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? v : null;

    private static bool TryParseBool(string s, out bool value)
    {
        switch (s.Trim().ToUpperInvariant())
        {
            case "TRUE":
                value = true;
                return true;
            case "FALSE":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }
}

/// <summary>An ARQ session came up, with the bandwidth it settled on in Hz.</summary>
internal sealed record ArdopConnected(string Raw, string Call, int? BandwidthHz) : ArdopNotification(Raw);

/// <summary>The ARQ session went down, for any reason at all.</summary>
internal sealed record ArdopDisconnected(string Raw) : ArdopNotification(Raw);

/// <summary>An inbound connect request is being decoded.</summary>
internal sealed record ArdopPending(string Raw) : ArdopNotification(Raw);

/// <summary>The inbound connect request that was pending went away.</summary>
internal sealed record ArdopCancelPending(string Raw) : ArdopNotification(Raw);

/// <summary>The inbound call is addressed to this callsign.</summary>
internal sealed record ArdopTarget(string Raw, string Call) : ArdopNotification(Raw);

/// <summary>The far station answered a ping, with its reported S/N in dB and quality 0-100.</summary>
internal sealed record ArdopPingAck(string Raw, int? SnrDb, int? Quality) : ArdopNotification(Raw);

/// <summary>The far end refused the call on bandwidth.</summary>
internal sealed record ArdopRejectedBw(string Raw, string Call) : ArdopNotification(Raw);

/// <summary>The far end refused the call because it was busy.</summary>
internal sealed record ArdopRejectedBusy(string Raw, string Call) : ArdopNotification(Raw);

/// <summary>The transmitter keyed or unkeyed.</summary>
internal sealed record ArdopPtt(string Raw, bool Keyed) : ArdopNotification(Raw);

/// <summary>The channel-busy detector changed its mind. Never sent by a TNC that has none.</summary>
internal sealed record ArdopBusy(string Raw, bool Busy) : ArdopNotification(Raw);

/// <summary>Bytes now queued for transmission.</summary>
internal sealed record ArdopBuffer(string Raw, int Bytes) : ArdopNotification(Raw);

/// <summary>Human-readable commentary, always alongside the machine-readable line.</summary>
internal sealed record ArdopStatus(string Raw, string Text) : ArdopNotification(Raw);

/// <summary>A protocol state change.</summary>
internal sealed record ArdopNewState(string Raw, string State) : ArdopNotification(Raw);

/// <summary>The TNC refused a command.</summary>
internal sealed record ArdopFault(string Raw, string Text) : ArdopNotification(Raw);

/// <summary>Anything else the TNC said, kept verbatim.</summary>
internal sealed record ArdopOtherLine(string Raw) : ArdopNotification(Raw);
