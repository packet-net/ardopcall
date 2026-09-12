using FluentAssertions;
using Xunit;

namespace Ardopcall.Tests;

public sealed class ArgumentParsingTests
{
    [Fact]
    public async Task No_Args_Returns_Exit_Code_2()
        => (await Program.Main([])).Should().Be(2);

    [Fact]
    public async Task Help_Flag_Returns_Exit_Code_0()
        => (await Program.Main(["--help"])).Should().Be(0);

    [Fact]
    public async Task Short_Help_Flag_Returns_Exit_Code_0()
        => (await Program.Main(["-h"])).Should().Be(0);

    [Fact]
    public async Task Version_Flag_Returns_Exit_Code_0()
        => (await Program.Main(["--version"])).Should().Be(0);

    [Fact]
    public async Task Short_Version_Flag_Returns_Exit_Code_0()
        => (await Program.Main(["-V"])).Should().Be(0);

    [Fact]
    public void No_Subcommand_Means_Monitor()
    {
        ParsedArgs? parsed = Program.ParseArgs(["-t", "localhost:8515"]);

        parsed.Should().NotBeNull();
        parsed!.Command.Should().Be(ArdopSubcommand.Monitor);
    }

    [Fact]
    public void Monitor_Needs_Neither_Callsign_Nor_Bandwidth()
    {
        // It cannot transmit, so it asks for nothing that only a transmitting
        // run needs.
        ParsedArgs? parsed = Program.ParseArgs(["monitor", "-t", "localhost:8515"]);

        parsed.Should().NotBeNull();
        parsed!.MyCall.Should().BeNull();
        parsed.Bandwidth.Should().BeNull();
    }

    [Fact]
    public void Unset_Flags_Stay_Null_So_The_Tnc_Default_Governs()
    {
        ParsedArgs? parsed = Program.ParseArgs(["-t", "localhost:8515"]);

        parsed.Should().NotBeNull();
        parsed!.Grid.Should().BeNull();
        parsed.ArqTimeoutSeconds.Should().BeNull();
        parsed.Attempts.Should().BeNull();
        parsed.LogPath.Should().BeNull();
        parsed.FecMode.Should().BeNull();
    }

    [Fact]
    public void Tnc_Endpoint_Is_Split_Into_Host_And_Port()
    {
        ParsedArgs? parsed = Program.ParseArgs(["-t", "pdn-soundmodem:8200"]);

        parsed.Should().NotBeNull();
        parsed!.TncHost.Should().Be("pdn-soundmodem");
        parsed.TncPort.Should().Be(8200);
    }

    [Fact]
    public void Missing_Tnc_Returns_Exit_Code_2()
        => Program.ParseArgs(["ping", "G8BPQ", "-s", "M0LTE", "--bw", "500"]).Should().BeNull();

    [Fact]
    public void Malformed_Tnc_Endpoint_Returns_Exit_Code_2()
        => Program.ParseArgs(["-t", "localhost"]).Should().BeNull();

    [Fact]
    public void Unknown_Option_Returns_Exit_Code_2()
        => Program.ParseArgs(["-t", "localhost:8515", "--bogus"]).Should().BeNull();

    [Fact]
    public void Unknown_Subcommand_Returns_Exit_Code_2()
        => Program.ParseArgs(["wibble", "-t", "localhost:8515"]).Should().BeNull();

    [Fact]
    public void Ping_Without_Mycall_Returns_Exit_Code_2()
        => Program.ParseArgs(["ping", "G8BPQ", "-t", "localhost:8515", "--bw", "500"]).Should().BeNull();

    [Fact]
    public void Connect_Without_Mycall_Returns_Exit_Code_2()
        => Program.ParseArgs(["connect", "G8BPQ", "-t", "localhost:8515", "--bw", "500"]).Should().BeNull();

    [Fact]
    public void Listen_Without_Mycall_Returns_Exit_Code_2()
        => Program.ParseArgs(["listen", "-t", "localhost:8515", "--bw", "500"]).Should().BeNull();

    [Fact]
    public void Id_Without_Mycall_Returns_Exit_Code_2()
        => Program.ParseArgs(["id", "-t", "localhost:8515", "--bw", "500"]).Should().BeNull();

    [Fact]
    public void Transmitting_Without_Bandwidth_Returns_Exit_Code_2()
    {
        // ARQBW is the one value ardopcall will not inherit from the TNC.
        Program.ParseArgs(["connect", "G8BPQ", "-t", "localhost:8515", "-s", "M0LTE"]).Should().BeNull();
        Program.ParseArgs(["ping", "G8BPQ", "-t", "localhost:8515", "-s", "M0LTE"]).Should().BeNull();
        Program.ParseArgs(["listen", "-t", "localhost:8515", "-s", "M0LTE"]).Should().BeNull();
    }

    [Fact]
    public void Connect_Without_A_Destination_Returns_Exit_Code_2()
        => Program.ParseArgs(["connect", "-t", "localhost:8515", "-s", "M0LTE", "--bw", "500"]).Should().BeNull();

    [Fact]
    public void Connect_With_Two_Destinations_Returns_Exit_Code_2()
        => Program.ParseArgs(["connect", "G8BPQ", "GM8BPQ", "-t", "localhost:8515", "-s", "M0LTE", "--bw", "500"]).Should().BeNull();

    [Fact]
    public void Monitor_With_An_Argument_Returns_Exit_Code_2()
        => Program.ParseArgs(["monitor", "G8BPQ", "-t", "localhost:8515"]).Should().BeNull();

    [Fact]
    public void Invalid_Callsign_Returns_Exit_Code_2()
    {
        Program.ParseArgs(["connect", "G8BPQ", "-t", "localhost:8515", "-s", "", "--bw", "500"]).Should().BeNull();
        Program.ParseArgs(["connect", "not a call", "-t", "localhost:8515", "-s", "M0LTE", "--bw", "500"]).Should().BeNull();
    }

    [Fact]
    public void Callsigns_Are_Upper_Cased()
    {
        ParsedArgs? parsed = Program.ParseArgs(["connect", "g8bpq", "-t", "localhost:8515", "-s", "m0lte", "--bw", "500"]);

        parsed.Should().NotBeNull();
        parsed!.MyCall.Should().Be("M0LTE");
        parsed.Target.Should().Be("G8BPQ");
    }

    [Fact]
    public void An_Ssid_Is_Accepted_In_A_Callsign()
        => Program.ParseArgs(["connect", "G8BPQ-5", "-t", "localhost:8515", "-s", "M0LTE-1", "--bw", "500"]).Should().NotBeNull();

    [Fact]
    public void Bandwidth_Must_Be_One_Of_The_Four_Ardop_Bandwidths()
    {
        Program.ParseArgs(["ping", "G8BPQ", "-t", "localhost:8515", "-s", "M0LTE", "--bw", "700"]).Should().BeNull();
        Program.ParseArgs(["ping", "G8BPQ", "-t", "localhost:8515", "-s", "M0LTE", "--bw", "2000"]).Should().NotBeNull();
    }

    [Fact]
    public void Forced_Without_Bandwidth_Returns_Exit_Code_2()
        => Program.ParseArgs(["-t", "localhost:8515", "--forced"]).Should().BeNull();

    [Fact]
    public void Forced_Is_Carried_Through_With_The_Bandwidth()
    {
        ParsedArgs? parsed = Program.ParseArgs(["ping", "G8BPQ", "-t", "localhost:8515", "-s", "M0LTE", "--bw", "500", "--forced"]);

        parsed.Should().NotBeNull();
        parsed!.Bandwidth.Should().Be(500);
        parsed.Forced.Should().BeTrue();
    }

    [Fact]
    public void Timeout_Outside_The_Tncs_Range_Returns_Exit_Code_2()
    {
        Program.ParseArgs(["-t", "localhost:8515", "--timeout", "10"]).Should().BeNull();
        Program.ParseArgs(["-t", "localhost:8515", "--timeout", "300"]).Should().BeNull();
        Program.ParseArgs(["-t", "localhost:8515", "--timeout", "240"]).Should().NotBeNull();
    }

    [Fact]
    public void Attempts_Must_Be_Positive()
    {
        Program.ParseArgs(["ping", "G8BPQ", "-t", "localhost:8515", "-s", "M0LTE", "--bw", "500", "--attempts", "0"]).Should().BeNull();
        Program.ParseArgs(["ping", "G8BPQ", "-t", "localhost:8515", "-s", "M0LTE", "--bw", "500", "--attempts", "-1"]).Should().BeNull();
        Program.ParseArgs(["ping", "G8BPQ", "-t", "localhost:8515", "-s", "M0LTE", "--bw", "500", "--attempts", "3"])!.Attempts.Should().Be(3);
    }

    [Fact]
    public void Fec_Takes_Exactly_One_Payload_Argument()
    {
        Program.ParseArgs(["fec", "-t", "localhost:8515", "-s", "M0LTE", "--bw", "500"]).Should().BeNull();
        Program.ParseArgs(["fec", "hello", "-t", "localhost:8515", "-s", "M0LTE", "--bw", "500"])!.FecText.Should().Be("hello");
        Program.ParseArgs(["fec", "-", "-t", "localhost:8515", "-s", "M0LTE", "--bw", "500"])!.FecText.Should().Be("-");
    }

    [Fact]
    public void Fecmode_Only_Applies_To_Fec()
    {
        Program.ParseArgs(["ping", "G8BPQ", "-t", "localhost:8515", "-s", "M0LTE", "--bw", "500", "--fecmode", "4FSK.500.100"]).Should().BeNull();
        Program.ParseArgs(["fec", "hi", "-t", "localhost:8515", "-s", "M0LTE", "--bw", "500", "--fecmode", "4FSK.500.100"])!
            .FecMode.Should().Be("4FSK.500.100");
    }

    [Fact]
    public void Tee_Needs_A_Local_Port_And_Refuses_A_Callsign()
    {
        Program.ParseArgs(["tee", "-t", "localhost:8515"]).Should().BeNull();
        Program.ParseArgs(["tee", "-t", "localhost:8515", "--local", "8515", "-s", "M0LTE"]).Should().BeNull();
        Program.ParseArgs(["tee", "-t", "localhost:8515", "--local", "8615"])!.LocalPort.Should().Be(8615);
    }

    [Fact]
    public void Local_Port_Only_Applies_To_Tee()
        => Program.ParseArgs(["-t", "localhost:8515", "--local", "8615"]).Should().BeNull();

    [Fact]
    public void Abort_Needs_Neither_Callsign_Nor_Bandwidth()
    {
        // It is the command someone types at a keyed transmitter, so it asks
        // for as little as possible.
        ParsedArgs? parsed = Program.ParseArgs(["abort", "-t", "localhost:8515"]);

        parsed.Should().NotBeNull();
        parsed!.Command.Should().Be(ArdopSubcommand.Abort);
    }

    [Fact]
    public void Abort_Tolerates_A_Callsign_Rather_Than_Refusing_To_Run()
        => Program.ParseArgs(["abort", "-t", "localhost:8515", "-s", "M0LTE"]).Should().NotBeNull();

    [Fact]
    public void A_Missing_Option_Value_Returns_Exit_Code_2()
    {
        Program.ParseArgs(["-t"]).Should().BeNull();
        Program.ParseArgs(["-t", "localhost:8515", "--log"]).Should().BeNull();
    }

    [Fact]
    public async Task Unreachable_Tnc_Returns_Exit_Code_3()
    {
        // Port 1 is almost certainly not listening.
        int code = await Program.Main(["-t", "127.0.0.1:1"]);

        code.Should().Be(3);
    }
}
