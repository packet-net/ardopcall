using System.Text;

namespace Ardopcall;

/// <summary>
/// One block on the ARDOP data socket: the three-character tag the TNC put in
/// front of the payload, or an empty tag for a block going the other way, since
/// host-to-TNC blocks carry no tag at all (docs/host-protocol.md, "Data socket
/// framing").
/// </summary>
internal sealed record ArdopDataBlock(string Tag, byte[] Payload)
{
    /// <summary>Data from a connected ARQ session.</summary>
    internal const string Arq = "ARQ";

    /// <summary>Data from a connectionless FEC transmission.</summary>
    internal const string Fec = "FEC";

    /// <summary>A FEC frame that failed to decode.</summary>
    internal const string Err = "ERR";

    /// <summary>A decoded ID frame.</summary>
    internal const string Idf = "IDF";
}

/// <summary>
/// Reassembles CR-terminated command lines out of whatever the TCP stack hands
/// over.
/// </summary>
/// <remarks>
/// <para>The command socket carries <c>&lt;line&gt;&lt;CR&gt;</c> in both
/// directions, with no line feed, and the TNC makes no promise at all about how
/// those lines land in TCP segments: several may arrive in one read and one may
/// be split across two. So this accumulates bytes and only yields a line when it
/// sees the CR. Empty lines are discarded rather than faulted, which is what the
/// TNC itself does with them.</para>
/// <para>A line feed is <b>not</b> treated as a terminator and is not stripped.
/// The protocol says CR and only CR, and this tool exists to show what a TNC
/// actually sent: silently absorbing a stray LF would hide exactly the kind of
/// disagreement between two implementations that ardopcall is here to find. It
/// shows up in the transcript as an escaped <c>\x0A</c> instead.</para>
/// </remarks>
internal sealed class CommandLineAssembler
{
    /// <summary>
    /// Refuse to buffer more than this without seeing a CR. Nothing in the
    /// protocol comes close (the longest real lines are version banners), so a
    /// stream this long without a terminator means the far end is not speaking
    /// the command protocol at all, and saying so beats growing until the
    /// process dies.
    /// </summary>
    internal const int MaxLineBytes = 65536;

    private readonly List<byte> pending = [];

    /// <summary>Feeds one TCP chunk in and gets back the complete lines it finished.</summary>
    internal List<string> Append(ReadOnlySpan<byte> chunk)
    {
        var lines = new List<string>();
        foreach (byte b in chunk)
        {
            if (b == 0x0D)
            {
                if (pending.Count > 0)
                {
                    lines.Add(Encoding.UTF8.GetString(pending.ToArray()));
                    pending.Clear();
                }

                continue;
            }

            pending.Add(b);
            if (pending.Count > MaxLineBytes)
            {
                throw new InvalidDataException(
                    $"command line exceeded {MaxLineBytes} bytes with no carriage return: the peer is not speaking the ARDOP host protocol");
            }
        }

        return lines;
    }
}

/// <summary>
/// Reassembles length-prefixed blocks on the data socket, in whichever of the
/// two framings applies to the direction being read.
/// </summary>
/// <remarks>
/// TNC to host is <c>[2-byte BE length][3-char tag][payload]</c> and the length
/// counts the tag, so it is <c>payload.Length + 3</c>. Host to TNC is
/// <c>[2-byte BE length][payload]</c> with no tag at all. The two are not
/// symmetrical and nothing in the stream says which you are looking at, so the
/// direction is fixed when the assembler is constructed rather than sniffed.
/// </remarks>
internal sealed class DataBlockAssembler(bool tagged)
{
    private readonly List<byte> pending = [];

    /// <summary>True when this assembler expects the TNC-to-host tagged framing.</summary>
    internal bool Tagged { get; } = tagged;

    /// <summary>Feeds one TCP chunk in and gets back the complete blocks it finished.</summary>
    internal List<ArdopDataBlock> Append(ReadOnlySpan<byte> chunk)
    {
        pending.AddRange(chunk);
        var blocks = new List<ArdopDataBlock>();
        while (pending.Count >= 2)
        {
            // Big-endian, so a payload of 300 bytes is 0x01 0x2C and the high
            // byte carries real information: reading these little-endian is the
            // classic way to lose every block over 255 bytes.
            int length = (pending[0] << 8) | pending[1];
            int overhead = Tagged ? 3 : 0;
            if (length < overhead)
            {
                throw new InvalidDataException(
                    $"data block length {length} is too short to hold the {overhead}-character tag");
            }

            if (pending.Count < length + 2)
            {
                break;
            }

            string tag = Tagged ? Encoding.ASCII.GetString(pending.GetRange(2, 3).ToArray()) : string.Empty;
            byte[] payload = [.. pending.GetRange(2 + overhead, length - overhead)];
            pending.RemoveRange(0, length + 2);
            blocks.Add(new ArdopDataBlock(tag, payload));
        }

        return blocks;
    }
}

/// <summary>The one thing ardopcall writes on the data socket.</summary>
internal static class ArdopFraming
{
    /// <summary>
    /// The largest payload the host-to-TNC framing can describe, since the
    /// length prefix is two bytes.
    /// </summary>
    internal const int MaxHostBlockBytes = 65535;

    /// <summary>
    /// Wraps a payload in the host-to-TNC framing:
    /// <c>[2-byte BE length][payload]</c>, no tag. The length counts the payload
    /// only.
    /// </summary>
    internal static byte[] FrameHostData(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxHostBlockBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload), $"a host data block is at most {MaxHostBlockBytes} bytes");
        }

        var framed = new byte[payload.Length + 2];
        framed[0] = (byte)(payload.Length >> 8);
        framed[1] = (byte)payload.Length;
        payload.CopyTo(framed.AsSpan(2));
        return framed;
    }
}
