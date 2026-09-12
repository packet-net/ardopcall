# ardopcall: what it is, and what it is not

`ardopcall` is to ARDOP what `axcall` is to AX.25: a small, zero-configuration,
one-session command line tool that lets an operator drive a link by hand, from a
shell, and see exactly what happened.

## Why it exists

pdn-soundmodem carries a complete ARDOP 1 implementation. As of September 2026 that
implementation has never transmitted over a real radio. Its own acceptance ladder has
one rung left:

> Rung 5, on-air. Nothing in this campaign is on-air evidence. In particular no live
> ARQ session has ever exercised the ConAck acceptance shipped in M0LTE.Ardop 0.4.0.
> Exit: one real ARQ connection with a deployed peer, logged.

A Winlink client such as Pat can make a connection, but it can only make a
*connection*. It cannot send a single ping, it cannot send a bare ID frame, it cannot
pin the bandwidth, and when something fails it reports a timeout and nothing else.
That is the wrong instrument for a first transmission on a shared band segment.

## The seam: the host protocol, not the library

ardopcall speaks the ardopcf TCP host interface directly and depends on no ARDOP
library. Three consequences, all of them the point:

1. The same binary drives our TNC **and** a real ardopcf. Run the identical script
   against both on the same antenna and a disagreement is a measurement.
2. It exercises the same command set LinBPQ's ARDOP driver uses, so it pre-validates
   that integration before a node is ever pointed at the TNC.
3. It is useful to anyone running ardopcf, who today has Pat and ARIM but no small
   hand tool.

## Shape

One verb per run, selected by subcommand. stdin is the data path in a session, stdout
is the peer's data, stderr is the operator's view of the link. A transcript of every
line in both directions, plus every framed data block, is written to a file when asked
for. Follows axcall's rule: a flag left unset is not sent to the TNC at all, so the
TNC's own default governs and ardopcall never silently restates it.

### Subcommands

| Command | Transmits? | What it does |
|---|---|---|
| `monitor` | no | attach, `PROTOCOLMODE RXO`, print every line and data block. The safe default. |
| `listen` | on answer only | `LISTEN TRUE`, wait for an inbound ARQ session, relay stdin/stdout, log it. Never initiates. |
| `ping <call>` | yes, one frame | send a ping, print the `PINGACK` with the far station's reported signal-to-noise and quality. |
| `id` | yes, one frame | `SENDID`. |
| `fec <text\|-\>` | yes | connectionless FEC transmission at a chosen frame type. |
| `connect <call>` | yes | dial an ARQ session, relay stdin/stdout until either end drops. |
| `tee` | no | sit between a host (LinBPQ, Pat) and the TNC, relay both sockets, log every byte. |

### Global options

`-t, --tnc <host:port>` (required), `-s, --mycall <call>` (required for anything that
transmits), `--bw <200|500|1000|2000>`, `--forced`, `--grid <locator>`,
`--timeout <seconds>`, `--log <path>`, `--attempts <n>`, `-h`, `-V`.

## Non-goals

- **No ARDOP implementation.** If the TNC is wrong, ardopcall's job is to show you
  precisely how, not to compensate.
- **No B2F, no Winlink, no mailbox.** That is Pat's job, above this seam.
- **No TUI.** A pipe filter, like axcall. `tee` provides the wire view that axcall
  delegated to an external Python script.
- **No automatic retry loop.** A tool that retries unattended is the thing we are
  specifically trying not to build yet: the TNC has no busy detector, so a human
  stays in the loop.
- **No default callsign, ever.** `-s` is mandatory for every transmitting subcommand
  and has no fallback.

## Safety posture

The tool assumes the TNC it is driving has no busy detector, because ours does not.
So:

- `monitor` is the default subcommand when none is given.
- Every transmitting subcommand prints, to stderr, what it is about to do and on which
  callsign, before it does it.
- **Ctrl-C sends `ABORT` before it closes.** This is the only true stop. `ABORT` calls
  `Engine.Abort` plus the FEC abort flag (`ArdopHostTnc.cs:1059-1063`) and stops the
  transmitter immediately.
- **Closing the socket is not a stop, and it is worth being exact about why.** Dropping
  the command socket triggers the TNC's host-link failsafe, `HostLinkLost`
  (`ArdopHostTnc.cs:278-290`). That aborts a FEC transmission, but if an ARQ session is up
  it calls `Engine.Disconnect`, the *orderly* teardown, and `CheckForDisconnect`
  (`ArdopArqEngine.cs:1279-1300`) then transmits DISC frames on a 2000 ms repeat. So
  killing ardopcall mid-session causes more keyups, not silence. Correct protocol
  behaviour, and the opposite of an emergency stop.
