using System.Text;
using FluentAssertions;
using Xunit;

namespace Ardopcall.Tests;

/// <summary>
/// The wire format, tested where it actually goes wrong: at the seams between
/// TCP segments, and on the high byte of a length prefix.
/// </summary>
public sealed class FramingTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    [Fact]
    public void Complete_Line_In_One_Segment_Is_Returned()
    {
        var assembler = new CommandLineAssembler();
        assembler.Append(Ascii("MYCALL now M0LTE\r")).Should().Equal("MYCALL now M0LTE");
    }

    [Fact]
    public void Line_Split_Across_Segments_Is_Reassembled()
    {
        var assembler = new CommandLineAssembler();

        assembler.Append(Ascii("CONNEC")).Should().BeEmpty();
        assembler.Append(Ascii("TED G8BPQ ")).Should().BeEmpty();
        assembler.Append(Ascii("500\r")).Should().Equal("CONNECTED G8BPQ 500");
    }

    [Fact]
    public void Line_Split_Immediately_Before_Its_Carriage_Return_Is_Reassembled()
    {
        var assembler = new CommandLineAssembler();

        assembler.Append(Ascii("PTT TRUE")).Should().BeEmpty();
        assembler.Append(Ascii("\r")).Should().Equal("PTT TRUE");
    }

    [Fact]
    public void Several_Lines_In_One_Segment_Are_All_Returned_In_Order()
    {
        var assembler = new CommandLineAssembler();

        assembler.Append(Ascii("PTT TRUE\rBUFFER 12\rPTT FALSE\r"))
            .Should().Equal("PTT TRUE", "BUFFER 12", "PTT FALSE");
    }

    [Fact]
    public void Trailing_Partial_Line_Is_Held_Until_Its_Terminator_Arrives()
    {
        var assembler = new CommandLineAssembler();

        assembler.Append(Ascii("PENDING\rTARGET M0L")).Should().Equal("PENDING");
        assembler.Append(Ascii("TE\r")).Should().Equal("TARGET M0LTE");
    }

    [Fact]
    public void Empty_Lines_Are_Discarded_Not_Faulted()
    {
        var assembler = new CommandLineAssembler();

        assembler.Append(Ascii("\r\r\rDISCONNECTED\r\r")).Should().Equal("DISCONNECTED");
    }

    [Fact]
    public void A_Line_Feed_Is_Content_Not_A_Terminator()
    {
        // The protocol says CR and only CR. Absorbing a stray LF would hide the
        // difference between two implementations, which is the one thing this
        // tool must not do.
        var assembler = new CommandLineAssembler();

        assembler.Append(Ascii("STATUS one\ntwo\r")).Should().Equal("STATUS one\ntwo");
    }

    [Fact]
    public void An_Unterminated_Flood_Is_Rejected_Rather_Than_Buffered_Forever()
    {
        var assembler = new CommandLineAssembler();
        var flood = new byte[CommandLineAssembler.MaxLineBytes + 1];
        Array.Fill(flood, (byte)'X');

        Action append = () => assembler.Append(flood);

        append.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Host_To_Tnc_Framing_Is_Length_Then_Payload_With_No_Tag()
    {
        byte[] framed = ArdopFraming.FrameHostData(Ascii("hello"));

        framed.Should().Equal(0x00, 0x05, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o');
    }

    [Fact]
    public void Host_To_Tnc_Framing_Uses_The_High_Byte_Of_The_Length()
    {
        // 300 bytes is 0x012C: read little-endian this would frame as 44 bytes
        // and every long block would be lost.
        var payload = new byte[300];
        Array.Fill(payload, (byte)'Z');

        byte[] framed = ArdopFraming.FrameHostData(payload);

        framed.Length.Should().Be(302);
        framed[0].Should().Be(0x01);
        framed[1].Should().Be(0x2C);
    }

    [Fact]
    public void Tnc_To_Host_Framing_Counts_The_Tag_In_The_Length()
    {
        var assembler = new DataBlockAssembler(tagged: true);

        // length 8 = 3 tag characters + 5 payload bytes.
        List<ArdopDataBlock> blocks = assembler.Append([0x00, 0x08, (byte)'A', (byte)'R', (byte)'Q', (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o']);

        blocks.Should().ContainSingle();
        blocks[0].Tag.Should().Be("ARQ");
        Encoding.ASCII.GetString(blocks[0].Payload).Should().Be("hello");
    }

    [Fact]
    public void Tnc_To_Host_Block_Split_Across_Segments_Is_Reassembled()
    {
        var assembler = new DataBlockAssembler(tagged: true);

        assembler.Append([0x00]).Should().BeEmpty();
        assembler.Append([0x08, (byte)'F', (byte)'E', (byte)'C']).Should().BeEmpty();
        assembler.Append(Ascii("hel")).Should().BeEmpty();

        List<ArdopDataBlock> blocks = assembler.Append(Ascii("lo"));

        blocks.Should().ContainSingle();
        blocks[0].Tag.Should().Be("FEC");
        Encoding.ASCII.GetString(blocks[0].Payload).Should().Be("hello");
    }

    [Fact]
    public void Several_Tnc_To_Host_Blocks_In_One_Segment_Are_All_Returned()
    {
        var assembler = new DataBlockAssembler(tagged: true);
        var segment = new List<byte>();
        segment.AddRange([0x00, 0x05, (byte)'A', (byte)'R', (byte)'Q', (byte)'h', (byte)'i']);
        segment.AddRange([0x00, 0x04, (byte)'I', (byte)'D', (byte)'F', (byte)'!']);

        List<ArdopDataBlock> blocks = assembler.Append(segment.ToArray());

        blocks.Should().HaveCount(2);
        blocks[0].Tag.Should().Be("ARQ");
        blocks[1].Tag.Should().Be("IDF");
        Encoding.ASCII.GetString(blocks[1].Payload).Should().Be("!");
    }

    [Fact]
    public void Tnc_To_Host_Framing_Uses_The_High_Byte_Of_The_Length()
    {
        var payload = new byte[300];
        Array.Fill(payload, (byte)'Z');
        var framed = new List<byte> { 0x01, 0x2F, (byte)'A', (byte)'R', (byte)'Q' };  // 300 + 3 = 0x012F
        framed.AddRange(payload);

        var assembler = new DataBlockAssembler(tagged: true);
        List<ArdopDataBlock> blocks = assembler.Append(framed.ToArray());

        blocks.Should().ContainSingle();
        blocks[0].Payload.Should().HaveCount(300);
    }

    [Fact]
    public void A_Tagged_Block_Too_Short_To_Hold_A_Tag_Is_Rejected()
    {
        var assembler = new DataBlockAssembler(tagged: true);

        Action append = () => assembler.Append([0x00, 0x02, (byte)'A', (byte)'R']);

        append.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void An_Empty_Tagged_Block_Is_Allowed()
    {
        var assembler = new DataBlockAssembler(tagged: true);

        List<ArdopDataBlock> blocks = assembler.Append([0x00, 0x03, (byte)'E', (byte)'R', (byte)'R']);

        blocks.Should().ContainSingle();
        blocks[0].Tag.Should().Be("ERR");
        blocks[0].Payload.Should().BeEmpty();
    }

    [Fact]
    public void Untagged_Blocks_Round_Trip_Through_The_Host_Framing()
    {
        // What ardopcall writes is what a TNC reads: this is the pair of
        // functions the data path depends on.
        var assembler = new DataBlockAssembler(tagged: false);
        var stream = new List<byte>();
        stream.AddRange(ArdopFraming.FrameHostData(Ascii("first\r")));
        stream.AddRange(ArdopFraming.FrameHostData(Ascii("second\r")));

        List<ArdopDataBlock> blocks = assembler.Append(stream.ToArray());

        blocks.Should().HaveCount(2);
        blocks[0].Tag.Should().BeEmpty();
        Encoding.ASCII.GetString(blocks[0].Payload).Should().Be("first\r");
        Encoding.ASCII.GetString(blocks[1].Payload).Should().Be("second\r");
    }
}
