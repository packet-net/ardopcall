# The ARDOP TCP host interface, as ardopcall speaks it

Source of truth: ardopcf `TCPHostInterface.c` / `HostInterface.c` (git a7c9228, MIT,
(c) 2014-2024 Rick Muething KN6KB, John Wiseman G8BPQ, Peter LaRue AI7YN), and the
managed reimplementation in `M0LTE.Ardop` (`Host/ArdopHostServer.cs`,
`Host/ArdopHostTnc.cs`). Line references below are to the M0LTE.Ardop checkout at
`/home/tf/M0LTE.Ardop`, which carries the ardopcf citations inline.

ardopcall implements this protocol directly over `System.Net.Sockets`. It takes no
dependency on `M0LTE.Ardop`, deliberately: the same binary must drive our TNC and a
real ardopcf, so that a disagreement between them is a measurement rather than a
guess.

## Sockets

Two TCP sockets to the TNC:

- **Command socket** on the configured port (ardopcf default 8515; GB7RDG's
  pdn-soundmodem uses 8200).
- **Data socket** on **command port + 1**, always. Not configurable.
  (`ArdopHostServer.cs:57`.)

Set `TCP_NODELAY` on both. The TNC accepts one host at a time per socket and a new
connection silently replaces the previous one (`ArdopHostServer.cs:193-210`), so
connecting displaces whatever was there. Dropping either socket mid-session triggers
the TNC's host-link failsafe: it aborts transmit and reverts to receive
(`ArdopHostServer.cs:325-331`, ardopcf `ARDOPCommon.c:424`). That failsafe is the
tool's emergency stop: killing ardopcall stops the radio transmitting.

## Command socket framing

Both directions: `<line><CR>`. A bare carriage return, U+000D. **No line feed.**
(`ArdopHostServer.cs:341-343` for TNC to host, `:221-250` for host to TNC.)

Any number of commands may arrive in one TCP segment, and one command may be split
across segments, so the reader must accumulate until it sees a CR
(`ArdopHostServer.cs:236-250`). Empty lines are discarded, not faulted.

Encoding is ASCII in practice; the TNC decodes UTF-8 and encodes UTF-8 on the way out.

## Data socket framing

**Host to TNC:** `[2-byte big-endian length][payload]`. The length counts the payload
only, and there is **no type tag in this direction** (`ArdopHostServer.cs:263-295`,
confirmed by `ArdopHostServerTests.cs:69-76`). Multiple blocks may be written back to
back.

**TNC to host:** `[2-byte big-endian length][3-character tag][payload]`, where the
length counts the tag plus the payload, so it is `payload.Length + 3`
(`ArdopHostServer.cs:363-369`, ardopcf `TCPHostInterface.c:220`).

Tags seen in this direction: `ARQ` (data from a connected session), `FEC` (data from a
connectionless FEC transmission), `ERR` (a failed FEC frame), `IDF` (a decoded ID
frame).

## Replies and notifications

Everything the TNC says arrives on the command socket, and there is no request
identifier, so a reply and an unsolicited notification are indistinguishable except by
their text. The tool must therefore treat the command socket as an **event stream**
and match replies by prefix, not by position.

Query form: sending a command with no parameter returns the current value as
`<NAME> <value>`, for example `MYCALL M0LTE`, `ARQBW 500MAX`, `STATE DISC`,
`VERSION ...` (`ArdopHostTnc.cs:815, 407, 971, 1023`).

Set form: sending a command with a parameter is acknowledged as `<NAME> now <value>`,
for example `PROTOCOLMODE now ARQ`, `ARQBW now 500MAX`, `LISTEN now TRUE`
(`ArdopHostTnc.cs:329, 345, 413`).

Rejection: `FAULT <text>`, for example `FAULT MYCALL not set`,
`FAULT Not from mode RXO`, `FAULT Syntax Err: ...` (`ArdopHostTnc.cs:1034`).

### Asynchronous notifications the tool must understand

Verified in `Arq/ArdopArqEngine.cs` and `Host/ArdopHostTnc.cs`:

| Line | Meaning |
|---|---|
| `PTT TRUE` / `PTT FALSE` | the transmitter keyed or unkeyed (`ArdopHostTnc.cs:1453,1460`) |
| `PENDING` | an inbound ConReq is being decoded (`ArdopArqEngine.cs:301`) |
| `CANCELPENDING` | that inbound attempt went away (`:609`) |
| `TARGET <call>` | the inbound call is addressed to this callsign (`:581`) |
| `CONNECTED <call> <bw>` | ARQ session up, session bandwidth in Hz (`:676`) |
| `DISCONNECTED` | session down, for any reason (`:220,702,720,882,898`) |
| `STATUS <text>` | human-readable session commentary, always alongside the machine-readable line (`:221,677,703`) |
| `NEWSTATE <state> ` | protocol state change, trailing space included (`ArdopHostTnc.cs:1266,1373`) |
| `PINGACK <snDb> <quality>` | **the far station answered our ping**, with its reported signal-to-noise in dB and quality 0-100 (`ArdopArqEngine.cs:558`) |
| `REJECTEDBW <call>` | the far end refused on bandwidth (`:602,656,942`) |
| `REJECTEDBUSY <call>` | the far end refused because it was busy (`:934`) |
| `BUFFER <n>` | bytes now queued for transmission (`ArdopHostTnc.cs:266,1345`) |

`BUSY TRUE` / `BUSY FALSE` is part of the protocol but **M0LTE.Ardop never sends it**:
the busy detector is not ported, and `BUSYDET` / `BUSYBLOCK` are accepted but inert
(`ArdopHostTnc.cs:44-56, 102`). A real ardopcf does send it. The tool must not depend
on it, and must say so plainly in its transcript when it is talking to a TNC that
never reports channel state, because that is the difference that matters on a shared
slot.

## The commands ardopcall uses

Setup, in this order, on every run:

    INITIALIZE
    PROTOCOLMODE ARQ|FEC|RXO
    MYCALL <call>
    GRIDSQUARE <locator>        (optional)
    ARQBW <200|500|1000|2000><MAX|FORCED>
    ARQTIMEOUT <seconds>
    LISTEN TRUE|FALSE
    AUTOBREAK TRUE|FALSE

Actions:

    ARQCALL <call> <attempts>   dial an ARQ session
    PING <call> <attempts>      send a ping, expect PINGACK
    SENDID                      send an ID frame
    FECSEND TRUE|FALSE          start/stop a connectionless FEC transmission
    FECMODE <framename>         the FEC waveform, e.g. 4FSK.500.100
    DISCONNECT                  orderly ARQ teardown
    ABORT                       immediate, dirty stop
    PURGEBUFFER                 drop queued data
    STATE                       query protocol state
    TWOTONETEST                 5 seconds of leader tones (a transmit test)

`ARQCALL` and `PING` both take a station and an attempt count and both fault with
`MYCALL not set` if the callsign has not been set, and with `Not from mode FEC` /
`Not from mode RXO` if the protocol mode is wrong (`ArdopHostTnc.cs:422-440, 829-850`).

## Things that key the transmitter

Worth listing explicitly, because ardopcall's whole safety story is that the operator
knows when the radio is about to talk:

`ARQCALL`, `PING`, `SENDID`, `FECSEND TRUE`, `TWOTONETEST`, and any ARQ session
already up (data, ACKs, BREAK, DISC). Everything else is receive-only.

`PROTOCOLMODE RXO` is the safe mode: it is receive-only by construction, and the TNC
refuses `ARQCALL` and `PING` from it.
