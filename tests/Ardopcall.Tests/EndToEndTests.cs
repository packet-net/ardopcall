using System.Text;
using FluentAssertions;
using Xunit;

namespace Ardopcall.Tests;

/// <summary>
/// ardopcall against a TNC that speaks the protocol, over real loopback sockets:
/// the framing, the setup sequence, the event-stream reply matching and the data
/// path, all at once.
/// </summary>
public sealed class EndToEndTests
{
    [Fact]
    public async Task Ping_Sets_The_Tnc_Up_And_Gets_A_PingAck()
    {
        await using FakeTnc tnc = FakeTnc.Start();

        int code = await Program.Main(["ping", "G8BPQ", "-t", $"127.0.0.1:{tnc.CommandPort}", "-s", "M0LTE", "--bw", "500"]);

        code.Should().Be(0);
        tnc.ReceivedLines.Should().ContainInOrder(
            "PROTOCOLMODE ARQ",
            "MYCALL M0LTE",
            "ARQBW 500MAX",
            "ARQBW",
            "LISTEN FALSE",
            "PING G8BPQ 2");

        // Nothing is reset on a station ardopcall does not own.
        tnc.ReceivedLines.Should().NotContain("INITIALIZE");
    }

    [Fact]
    public async Task Initialize_Is_Sent_Only_When_Asked_For()
    {
        await using FakeTnc tnc = FakeTnc.Start();

        int code = await Program.Main(
            ["ping", "G8BPQ", "-t", $"127.0.0.1:{tnc.CommandPort}", "-s", "M0LTE", "--bw", "500", "--initialize"]);

        code.Should().Be(0);
        tnc.ReceivedLines.Should().Contain("INITIALIZE");
    }

    [Fact]
    public async Task The_Station_Is_Put_Back_As_It_Was_Found()
    {
        // The station is in ARQ, listening, with its own callsign and a 2 kHz
        // bandwidth. A ping borrows all four, and has to hand them back.
        await using FakeTnc tnc = FakeTnc.Start();

        int code = await Program.Main(["ping", "GM8BPQ", "-t", $"127.0.0.1:{tnc.CommandPort}", "-s", "M0LTE", "--bw", "500"]);

        code.Should().Be(0);
        tnc.ReceivedLines.Should().ContainInOrder(
            "PING GM8BPQ 2",
            "MYCALL G8BPQ",
            "ARQBW 2000MAX",
            "PROTOCOLMODE ARQ",
            "LISTEN TRUE");

        // Only what it changed: the locator and the timeout were never touched,
        // so they are not restated either.
        tnc.ReceivedLines.Should().NotContain("GRIDSQUARE IO91");
        tnc.ReceivedLines.Should().NotContain("ARQTIMEOUT 120");
    }

    [Fact]
    public async Task A_Callsign_That_Cannot_Be_Cleared_Is_Reported_And_Exits_6()
    {
        // GB7RDG's TNC has no MYCALL. Setting one makes that station answer
        // calls addressed to it, and the host protocol has no way to unset it
        // again, so the run has to end loudly rather than quietly.
        await using FakeTnc tnc = FakeTnc.Start();
        tnc.Settings["MYCALL"] = string.Empty;

        int code = await Program.Main(["ping", "GM8BPQ", "-t", $"127.0.0.1:{tnc.CommandPort}", "-s", "M0LTE", "--bw", "500"]);

        code.Should().Be(6);

        // It did try, with the only form the protocol offers.
        tnc.ReceivedLines.Should().Contain("MYCALL ");

        // And everything it could put back, it did.
        tnc.ReceivedLines.Should().ContainInOrder("PROTOCOLMODE ARQ", "LISTEN TRUE");
    }

    [Fact]
    public async Task A_Ping_That_Is_Never_Answered_Exits_4()
    {
        await using FakeTnc tnc = FakeTnc.Start();

        // A TNC that transmits the ping and hears nothing back says nothing
        // back either, and silence is a result, not a hang.
        tnc.Script = static async (fake, line) =>
        {
            if (ArdopNotification.KeywordOf(line).Equals("PING", StringComparison.OrdinalIgnoreCase))
            {
                await fake.SendLineAsync(line);
                return;
            }

            await FakeTnc.DefaultReplyAsync(fake, line);
        };

        int code = await Program.Main(
            ["ping", "G8BPQ", "-t", $"127.0.0.1:{tnc.CommandPort}", "-s", "M0LTE", "--bw", "500", "--attempts", "1"]);

        code.Should().Be(4);
    }

    [Fact]
    public async Task A_Faulted_Command_Exits_5()
    {
        await using FakeTnc tnc = FakeTnc.Start();
        tnc.Script = static async (fake, line) =>
        {
            // The set form only, so the state snapshot's query still works.
            if (line.StartsWith("MYCALL ", StringComparison.OrdinalIgnoreCase))
            {
                await fake.SendLineAsync("FAULT Syntax Err: MYCALL");
                return;
            }

            await FakeTnc.DefaultReplyAsync(fake, line);
        };

        int code = await Program.Main(["id", "-t", $"127.0.0.1:{tnc.CommandPort}", "-s", "M0LTE", "--bw", "500"]);

        code.Should().Be(5);
        tnc.ReceivedLines.Should().NotContain("SENDID");
    }

    [Fact]
    public async Task Monitor_Puts_The_Tnc_In_Rxo_And_Puts_It_Back_When_Interrupted()
    {
        // The scenario that matters at a node: monitor borrows the TNC, and
        // Ctrl-C has to leave it answering calls again. Without the restore the
        // station would sit in RXO with LISTEN FALSE and nobody would know.
        await using FakeTnc tnc = FakeTnc.Start();
        ParsedArgs? parsed = Program.ParseArgs(["-t", $"127.0.0.1:{tnc.CommandPort}"]);
        parsed.Should().NotBeNull();
        parsed!.Command.Should().Be(ArdopSubcommand.Monitor);

        using var cts = new CancellationTokenSource();
        Task<string> settled = tnc.ExpectLineAsync(l => l == "LISTEN FALSE");
        Task<int> run = ArdopCommands.RunAsync(parsed, cts.Token);
        await settled.WaitAsync(FakeTnc.Timeout, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        int code = await run.WaitAsync(FakeTnc.Timeout, TestContext.Current.CancellationToken);

        code.Should().Be(0);
        tnc.ReceivedLines.Should().ContainInOrder(
            "PROTOCOLMODE RXO",
            "LISTEN FALSE",
            "ABORT",
            "PROTOCOLMODE ARQ",
            "LISTEN TRUE");

        // A monitor resets nothing and transmits nothing.
        tnc.ReceivedLines.Should().NotContain("INITIALIZE");
        tnc.ReceivedLines.Should().NotContain(l => l.StartsWith("ARQCALL", StringComparison.Ordinal));
        tnc.ReceivedLines.Should().NotContain(l => l.StartsWith("MYCALL ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_Interrupted_Session_Aborts_Before_It_Restores()
    {
        // Order matters: the transmitter stops first, the settings go back
        // second. Closing the sockets on their own would not stop an ARQ
        // session at all.
        await using FakeTnc tnc = FakeTnc.Start();
        ParsedArgs? parsed = Program.ParseArgs(
            ["listen", "-t", $"127.0.0.1:{tnc.CommandPort}", "-s", "M0LTE", "--bw", "500"]);

        using var cts = new CancellationTokenSource();
        Task<string> listening = tnc.ExpectLineAsync(l => l == "LISTEN TRUE");
        Task<int> run = ArdopCommands.RunAsync(parsed!, cts.Token);
        await listening.WaitAsync(FakeTnc.Timeout, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await run.WaitAsync(FakeTnc.Timeout, TestContext.Current.CancellationToken);

        tnc.ReceivedLines.Should().ContainInOrder("LISTEN TRUE", "ABORT", "MYCALL G8BPQ", "PROTOCOLMODE ARQ", "LISTEN TRUE");
    }

    [Fact]
    public async Task Abort_Sends_One_Command_And_Nothing_Else()
    {
        await using FakeTnc tnc = FakeTnc.Start();

        int code = await Program.Main(["abort", "-t", $"127.0.0.1:{tnc.CommandPort}"]);

        code.Should().Be(0);
        tnc.ReceivedLines.Should().Equal("ABORT");
    }

    [Fact]
    public async Task Id_Holds_The_Sockets_Until_The_Transmitter_Unkeys()
    {
        // Found by hand-running the tool against a real TNC: SENDID is
        // acknowledged before the transmitter keys, so a run that exited on the
        // acknowledgement dropped the sockets in the middle of the ID.
        await using FakeTnc tnc = FakeTnc.Start();
        Task<string> sendId = tnc.ExpectLineAsync(l => l == "SENDID");

        Task<int> run = Program.Main(["id", "-t", $"127.0.0.1:{tnc.CommandPort}", "-s", "M0LTE", "--bw", "500"]);
        await sendId.WaitAsync(FakeTnc.Timeout, TestContext.Current.CancellationToken);

        await tnc.SendLineAsync("PTT TRUE");

        // Nothing correct can have finished here, so this cannot fail
        // spuriously; only a regression makes it fire.
        run.IsCompleted.Should().BeFalse("the ID is still going out");

        await tnc.SendLineAsync("PTT FALSE");
        (await run.WaitAsync(FakeTnc.Timeout, TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Connect_Carries_Data_In_Both_Directions_And_Disconnects_Cleanly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeTnc tnc = FakeTnc.Start();
        await using ArdopHostClient client = await ArdopHostClient.ConnectAsync("127.0.0.1", tnc.CommandPort, null, ct);

        // Register the interest before the command goes out: the answer can
        // arrive before the send has even returned.
        using LineWaiter connected = client.Expect(n => n is ArdopConnected);
        Task<byte[]> hostBlock = tnc.ExpectDataAsync();
        await client.SendCommandAsync("ARQCALL G8BPQ 5", ct);

        var session = (ArdopConnected)await connected.Line.WaitAsync(FakeTnc.Timeout, ct);
        session.Call.Should().Be("G8BPQ");
        session.BandwidthHz.Should().Be(500);

        var input = new ScriptedReader("hello");
        var output = new SignalWriter();
        Task<string> peerSpoke = output.ExpectAsync("world");
        Task<int> relay = new SessionRelay(client, input, output).RelayAsync(ct);

        // Host to TNC: untagged, length-prefixed, CR-terminated line.
        byte[] sent = await hostBlock.WaitAsync(FakeTnc.Timeout, ct);
        Encoding.ASCII.GetString(sent).Should().Be("hello\r");

        // TNC to host: an ARQ-tagged block, rendered to stdout with its CR
        // turned into a line break.
        await tnc.SendDataAsync("ARQ", Encoding.ASCII.GetBytes("world\r"));
        await peerSpoke.WaitAsync(FakeTnc.Timeout, ct);
        output.Text.Should().Be("world\n");

        // End of input asks for an orderly teardown, and the relay waits for
        // the TNC to confirm it.
        input.EndInput();
        int code = await relay.WaitAsync(FakeTnc.Timeout, ct);

        code.Should().Be(0);
        tnc.ReceivedLines.Should().Contain("DISCONNECT");
    }

    [Fact]
    public async Task End_Of_Input_Waits_For_The_Transmit_Buffer_Before_Disconnecting()
    {
        // Found by hand-running the tool: reaching end of input and
        // disconnecting in the same breath tears the session down with the last
        // line still queued, which on a slow ARQ link is most of what was typed.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeTnc tnc = FakeTnc.Start();
        await using ArdopHostClient client = await ArdopHostClient.ConnectAsync("127.0.0.1", tnc.CommandPort, null, ct);

        using LineWaiter connected = client.Expect(n => n is ArdopConnected);
        Task<byte[]> hostBlock = tnc.ExpectDataAsync();
        await client.SendCommandAsync("ARQCALL G8BPQ 5", ct);
        await connected.Line.WaitAsync(FakeTnc.Timeout, ct);

        var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NotificationReceived += (_, n) =>
        {
            if (n is ArdopBuffer { Bytes: 12 })
            {
                buffered.TrySetResult();
            }
        };

        var input = new ScriptedReader("hello");
        Task<int> relay = new SessionRelay(client, input, new SignalWriter()).RelayAsync(ct);
        await hostBlock.WaitAsync(FakeTnc.Timeout, ct);

        // The TNC now says it has bytes queued, and the host knows it.
        Task<string> disconnect = tnc.ExpectLineAsync(l => l == "DISCONNECT");
        await tnc.SendLineAsync("BUFFER 12");
        await buffered.Task.WaitAsync(FakeTnc.Timeout, ct);

        input.EndInput();

        // A correct implementation cannot have sent this yet, so the check
        // cannot fail spuriously; only a regression makes it fire.
        disconnect.IsCompleted.Should().BeFalse("the disconnect must wait for the queued bytes to go out");

        await tnc.SendLineAsync("BUFFER 0");
        await disconnect.WaitAsync(FakeTnc.Timeout, ct);
        (await relay.WaitAsync(FakeTnc.Timeout, ct)).Should().Be(0);
    }

    [Fact]
    public async Task A_Data_Block_Split_Across_Segments_Still_Arrives_Whole()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeTnc tnc = FakeTnc.Start();
        await using ArdopHostClient client = await ArdopHostClient.ConnectAsync("127.0.0.1", tnc.CommandPort, null, ct);

        var received = new TaskCompletionSource<ArdopDataBlock>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DataReceived += (_, block) => received.TrySetResult(block);

        // A payload whose length needs the high byte, written in pieces so the
        // reassembly is doing real work.
        var payload = new byte[300];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        await tnc.SendDataInPiecesAsync("FEC", payload, pieces: 5);

        ArdopDataBlock block = await received.Task.WaitAsync(FakeTnc.Timeout, ct);

        block.Tag.Should().Be("FEC");
        block.Payload.Should().Equal(payload);
    }
}
