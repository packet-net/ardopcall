using System.Globalization;
using System.Text;

namespace Ardopcall;

/// <summary>Which way a logged item travelled.</summary>
internal enum LogDirection
{
    /// <summary>ardopcall (or, under tee, the host) sent it to the TNC.</summary>
    ToTnc,

    /// <summary>The TNC sent it.</summary>
    FromTnc,
}

/// <summary>
/// The <c>--log</c> transcript: every command line in both directions and every
/// framed data block, timestamped.
/// </summary>
/// <remarks>
/// <para>This file is evidence, so it is written the way evidence has to be
/// written. It is opened for append and never truncated, so a second run cannot
/// erase the first. Every line is flushed as it is written, so a transcript
/// survives the process being killed, which is the normal way to stop a
/// transmission. Timestamps are UTC to the millisecond, because the point of the
/// file is to line an ardopcall run up against a separate audio recording or
/// another station's log.</para>
/// <para>Everything written is plain ASCII with no line wrapping. Bytes outside
/// printable ASCII are escaped as <c>\xNN</c> rather than transliterated, so
/// nothing in the file is a guess about what a byte meant, and the file reads
/// correctly through journalctl, less and a C-locale pager.</para>
/// </remarks>
internal sealed class TranscriptLog : IDisposable
{
    private readonly StreamWriter writer;
    private readonly Lock gate = new();

    private TranscriptLog(StreamWriter writer) => this.writer = writer;

    /// <summary>Opens (or reopens) a transcript for append.</summary>
    internal static TranscriptLog Open(string path)
    {
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
        var log = new TranscriptLog(writer);
        log.Note("ardopcall transcript opened, times are UTC");
        return log;
    }

    /// <summary>Logs one command-socket line, already stripped of its CR.</summary>
    internal void Command(LogDirection direction, string line)
        => Write($"{Arrow(direction)} CMD  {Escape(line)}");

    /// <summary>
    /// Logs one data-socket block: its tag, its length, and a hex dump of the
    /// payload. An empty tag means the untagged host-to-TNC framing.
    /// </summary>
    internal void Data(LogDirection direction, string tag, ReadOnlySpan<byte> payload)
    {
        string label = tag.Length == 0 ? "(untagged)" : tag;
        lock (gate)
        {
            WriteLocked($"{Arrow(direction)} DATA {label} {payload.Length} bytes");
            for (int offset = 0; offset < payload.Length; offset += 16)
            {
                int take = Math.Min(16, payload.Length - offset);
                WriteLocked($"{Arrow(direction)} DATA {HexDumpLine(offset, payload.Slice(offset, take))}");
            }
        }
    }

    /// <summary>
    /// Logs a raw chunk of bytes exactly as it crossed a socket, used by tee,
    /// where reframing the stream to make it prettier would defeat the purpose.
    /// </summary>
    internal void Raw(LogDirection direction, string channel, ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            WriteLocked($"{Arrow(direction)} {channel} {bytes.Length} bytes raw");
            for (int offset = 0; offset < bytes.Length; offset += 16)
            {
                int take = Math.Min(16, bytes.Length - offset);
                WriteLocked($"{Arrow(direction)} {channel} {HexDumpLine(offset, bytes.Slice(offset, take))}");
            }
        }
    }

    /// <summary>Logs something ardopcall itself decided or observed, not something on the wire.</summary>
    internal void Note(string text) => Write($"# {Escape(text)}");

    public void Dispose()
    {
        lock (gate)
        {
            writer.Dispose();
        }
    }

    private void Write(string body)
    {
        lock (gate)
        {
            WriteLocked(body);
        }
    }

    private void WriteLocked(string body)
        => writer.WriteLine($"{DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)}Z {body}");

    private static string Arrow(LogDirection direction) => direction == LogDirection.ToTnc ? ">" : "<";

    /// <summary>
    /// Renders a line as printable ASCII, escaping everything else. A CR that
    /// somehow survived into the middle of a line, a stray LF, or a UTF-8 byte
    /// all show up as what they are rather than moving the cursor.
    /// </summary>
    internal static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is >= ' ' and <= '~')
            {
                sb.Append(c);
            }
            else if (c <= 0xFF)
            {
                sb.Append(CultureInfo.InvariantCulture, $"\\x{(int)c:X2}");
            }
            else
            {
                sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
            }
        }

        return sb.ToString();
    }

    /// <summary>One canonical hex-dump line: offset, up to 16 bytes, then the printable gutter.</summary>
    internal static string HexDumpLine(int offset, ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{offset:x4}  ");
        for (int i = 0; i < 16; i++)
        {
            sb.Append(i < bytes.Length
                ? string.Create(CultureInfo.InvariantCulture, $"{bytes[i]:x2} ")
                : "   ");
            if (i == 7)
            {
                sb.Append(' ');
            }
        }

        sb.Append('|');
        foreach (byte b in bytes)
        {
            sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
        }

        sb.Append('|');
        return sb.ToString();
    }
}
