using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Ardopcall.Tests;

/// <summary>
/// The transcript is evidence, so these are tests about the properties evidence
/// has to have: append-only, flushed, timestamped, and readable in a terminal
/// that knows nothing but ASCII.
/// </summary>
public sealed class TranscriptLogTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"ardopcall-{Path.GetRandomFileName()}.log");

    [Fact]
    public void Command_Lines_Carry_A_Direction_Marker_And_A_Utc_Timestamp()
    {
        using (TranscriptLog log = TranscriptLog.Open(path))
        {
            log.Command(LogDirection.ToTnc, "MYCALL M0LTE");
            log.Command(LogDirection.FromTnc, "MYCALL now M0LTE");
        }

        string[] lines = File.ReadAllLines(path);

        lines.Should().Contain(l => l.EndsWith("> CMD  MYCALL M0LTE", StringComparison.Ordinal));
        lines.Should().Contain(l => l.EndsWith("< CMD  MYCALL now M0LTE", StringComparison.Ordinal));
        foreach (string line in lines)
        {
            Regex.IsMatch(line, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z ").Should().BeTrue(line);
        }
    }

    [Fact]
    public void Data_Blocks_Are_Logged_With_Their_Tag_Length_And_A_Hex_Dump()
    {
        using (TranscriptLog log = TranscriptLog.Open(path))
        {
            log.Data(LogDirection.FromTnc, "ARQ", "hello"u8);
        }

        string text = File.ReadAllText(path);

        text.Should().Contain("< DATA ARQ 5 bytes");
        text.Should().Contain("68 65 6c 6c 6f");
        text.Should().Contain("|hello|");
    }

    [Fact]
    public void An_Untagged_Block_Is_Labelled_As_One()
    {
        using (TranscriptLog log = TranscriptLog.Open(path))
        {
            log.Data(LogDirection.ToTnc, string.Empty, "hi\r"u8);
        }

        File.ReadAllText(path).Should().Contain("> DATA (untagged) 3 bytes");
    }

    [Fact]
    public void Everything_Written_Is_Plain_Ascii()
    {
        using (TranscriptLog log = TranscriptLog.Open(path))
        {
            log.Command(LogDirection.FromTnc, "STATUS café with a stray\nline feed");
            log.Data(LogDirection.FromTnc, "ERR", [0x00, 0xFF, 0x80]);
        }

        byte[] bytes = File.ReadAllBytes(path);

        bytes.Should().OnlyContain(b => b < 0x80);
        string text = Encoding.ASCII.GetString(bytes);
        text.Should().Contain(@"caf\xE9");
        text.Should().Contain(@"\x0A");
    }

    [Fact]
    public void Reopening_Appends_Rather_Than_Truncating()
    {
        // A second run must not erase the evidence from the first.
        using (TranscriptLog first = TranscriptLog.Open(path))
        {
            first.Command(LogDirection.ToTnc, "FIRST RUN");
        }

        using (TranscriptLog second = TranscriptLog.Open(path))
        {
            second.Command(LogDirection.ToTnc, "SECOND RUN");
        }

        string text = File.ReadAllText(path);

        text.Should().Contain("FIRST RUN");
        text.Should().Contain("SECOND RUN");
    }

    [Fact]
    public void Lines_Are_Flushed_As_They_Are_Written()
    {
        // The normal way to stop a transmission is to kill the process, so what
        // is on disk at any moment has to be what has happened so far.
        using TranscriptLog log = TranscriptLog.Open(path);
        log.Command(LogDirection.ToTnc, "SENDID");

        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var text = new StreamReader(reader);

        text.ReadToEnd().Should().Contain("SENDID");
    }

    [Fact]
    public void A_Hex_Dump_Line_Has_The_Offset_The_Bytes_And_The_Printable_Gutter()
    {
        string line = TranscriptLog.HexDumpLine(16, "AB\x01"u8);

        line.Should().StartWith("0010  41 42 01 ");
        line.Should().EndWith("|AB.|");
    }

    public void Dispose() => File.Delete(path);
}
