using FluentAssertions;
using Xunit;

namespace Ardopcall.Tests;

/// <summary>The notification vocabulary from docs/host-protocol.md, line by line.</summary>
public sealed class NotificationParsingTests
{
    [Fact]
    public void Connected_Carries_The_Call_And_The_Session_Bandwidth()
    {
        ArdopNotification.Parse("CONNECTED G8BPQ 500")
            .Should().BeOfType<ArdopConnected>()
            .Which.Should().BeEquivalentTo(new { Call = "G8BPQ", BandwidthHz = 500 });
    }

    [Fact]
    public void Disconnected_Is_Recognised()
        => ArdopNotification.Parse("DISCONNECTED").Should().BeOfType<ArdopDisconnected>();

    [Fact]
    public void Pending_And_CancelPending_Are_Distinct()
    {
        ArdopNotification.Parse("PENDING").Should().BeOfType<ArdopPending>();
        ArdopNotification.Parse("CANCELPENDING").Should().BeOfType<ArdopCancelPending>();
    }

    [Fact]
    public void Target_Carries_The_Called_Callsign()
        => ArdopNotification.Parse("TARGET M0LTE").Should().BeOfType<ArdopTarget>().Which.Call.Should().Be("M0LTE");

    [Fact]
    public void PingAck_Carries_Signal_To_Noise_And_Quality()
    {
        var ack = ArdopNotification.Parse("PINGACK 10 85").Should().BeOfType<ArdopPingAck>().Subject;

        ack.SnrDb.Should().Be(10);
        ack.Quality.Should().Be(85);
    }

    [Fact]
    public void PingAck_Accepts_A_Negative_Signal_To_Noise()
        => ArdopNotification.Parse("PINGACK -3 40").Should().BeOfType<ArdopPingAck>().Which.SnrDb.Should().Be(-3);

    [Fact]
    public void PingAck_With_Fields_The_Tnc_Never_Filled_In_Is_Still_A_PingAck()
    {
        // A TNC whose decoded frame had no values prints an empty field rather
        // than omitting the line, and that is worth surfacing, not discarding.
        var ack = ArdopNotification.Parse("PINGACK  ").Should().BeOfType<ArdopPingAck>().Subject;

        ack.SnrDb.Should().BeNull();
        ack.Quality.Should().BeNull();
    }

    [Fact]
    public void Ping_Echo_Is_Not_A_PingAck()
    {
        // The reply to "PING G8BPQ 2" is its own echo. Matching a reply on the
        // prefix "PING" would take a PINGACK for the echo and the other way
        // about.
        ArdopNotification.Parse("PING G8BPQ 2").Should().BeOfType<ArdopOtherLine>();
        ArdopNotification.KeywordOf("PINGACK 10 85").Should().Be("PINGACK");
        ArdopNotification.KeywordOf("PING G8BPQ 2").Should().Be("PING");
    }

    [Fact]
    public void Rejections_Are_Told_Apart()
    {
        ArdopNotification.Parse("REJECTEDBW G8BPQ").Should().BeOfType<ArdopRejectedBw>().Which.Call.Should().Be("G8BPQ");
        ArdopNotification.Parse("REJECTEDBUSY G8BPQ").Should().BeOfType<ArdopRejectedBusy>().Which.Call.Should().Be("G8BPQ");
    }

    [Fact]
    public void Ptt_Carries_Whether_The_Transmitter_Is_Keyed()
    {
        ArdopNotification.Parse("PTT TRUE").Should().BeOfType<ArdopPtt>().Which.Keyed.Should().BeTrue();
        ArdopNotification.Parse("PTT FALSE").Should().BeOfType<ArdopPtt>().Which.Keyed.Should().BeFalse();
    }

    [Fact]
    public void Busy_Is_Understood_Even_Though_Our_Tnc_Never_Sends_It()
    {
        ArdopNotification.Parse("BUSY TRUE").Should().BeOfType<ArdopBusy>().Which.Busy.Should().BeTrue();
        ArdopNotification.Parse("BUSY FALSE").Should().BeOfType<ArdopBusy>().Which.Busy.Should().BeFalse();
    }

    [Fact]
    public void Buffer_Carries_The_Queued_Byte_Count()
        => ArdopNotification.Parse("BUFFER 1234").Should().BeOfType<ArdopBuffer>().Which.Bytes.Should().Be(1234);

    [Fact]
    public void Status_Keeps_Its_Whole_Text()
        => ArdopNotification.Parse("STATUS ARQ Timeout from Protocol State: ISS")
            .Should().BeOfType<ArdopStatus>().Which.Text.Should().Be("ARQ Timeout from Protocol State: ISS");

    [Fact]
    public void NewState_Trims_The_Trailing_Space_Ardopcf_Sends()
    {
        // "NEWSTATE %s " is an ardopcf quirk that every host works around.
        var state = ArdopNotification.Parse("NEWSTATE DISC ").Should().BeOfType<ArdopNewState>().Subject;

        state.State.Should().Be("DISC");
        state.Raw.Should().Be("NEWSTATE DISC ");
    }

    [Fact]
    public void Fault_Keeps_Its_Reason()
        => ArdopNotification.Parse("FAULT MYCALL not set").Should().BeOfType<ArdopFault>().Which.Text.Should().Be("MYCALL not set");

    [Fact]
    public void A_Set_Acknowledgement_Is_Not_A_Notification_But_Is_Still_Kept()
    {
        var other = ArdopNotification.Parse("ARQBW now 500MAX").Should().BeOfType<ArdopOtherLine>().Subject;

        other.Raw.Should().Be("ARQBW now 500MAX");
    }

    [Fact]
    public void An_Unrecognised_Line_Is_Kept_Verbatim_Rather_Than_Dropped()
    {
        // An unknown line from one of the two implementations under test is a
        // finding, not noise.
        ArdopNotification.Parse("SOMETHINGNEW 42").Should().BeOfType<ArdopOtherLine>().Which.Raw.Should().Be("SOMETHINGNEW 42");
    }
}
