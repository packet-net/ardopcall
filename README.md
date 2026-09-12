# ardopcall

A small, zero-configuration command line tool for driving an **ardopcf-compatible ARDOP 1
TNC** by hand: ping a station, send an ID, make one ARQ connection, watch the channel, or
tap the wire between a host and the TNC. What `axcall` is to AX.25, this is to ARDOP.

It speaks the ardopcf **TCP host interface** directly, over nothing but
`System.Net.Sockets`, and links no ARDOP library at all. That is the design point: the same
binary drives our TNC and a real ardopcf, so a disagreement between the two is a
measurement rather than a guess.

- **Two sockets**, the command port and the data port always after it, framed exactly as
  the protocol says: CR-terminated lines one way, length-prefixed tagged blocks the other.
- **One verb per run**: `monitor` `listen` `ping` `id` `fec` `connect` `tee` `abort`.
- **stdin is the data path, stdout is the peer, stderr is the link**, so it composes with
  pipes like any other filter.
- **A transcript** of every line in both directions and every data block, timestamped to
  the millisecond in UTC, whenever you pass `--log`.
- **It borrows the station, it does not reconfigure it.** Everything ardopcall can change
  is read back before anything is changed, and put back on the way out.

- **Targets** `net10.0`. No package references at all, by design.

## Build

```sh
dotnet build src/Ardopcall/Ardopcall.csproj -c Release
```

## Use

```sh
# Watch the channel. The default subcommand, and receive-only by construction.
ardopcall -t pdn-soundmodem:8200

# Is the path there? One ping, then the far station's reported S/N and quality.
ardopcall ping G8BPQ -t pdn-soundmodem:8200 -s M0LTE --bw 500

# One ARQ session, typed by hand, with a transcript for the evidence file.
ardopcall connect G8BPQ -t pdn-soundmodem:8200 -s M0LTE --bw 500 --log qso.log

# Sit between LinBPQ (or Pat) and the TNC and write down every byte.
ardopcall tee -t pdn-soundmodem:8200 --local 8600 --log wire.log

# Stop the transmitter, from a second terminal.
ardopcall abort -t pdn-soundmodem:8200
```

`ardopcall --help` lists the rest. ardopcf listens on 8515 by default; pdn-soundmodem at
GB7RDG uses 8200.

## Safety

- `-s/--mycall` is **mandatory** for every subcommand that transmits, with no default and
  no fallback.
- `--bw` is mandatory too. The TNC's ARQBW is whatever its default or the last host left it
  at, which is not reliably what its configuration asks for, so ardopcall sets it and then
  **reads it back** and prints what will actually go on the air.
- Every transmitting subcommand says what it is about to send, and under which callsign,
  before it sends it.
- **The station goes back as it was found.** `PROTOCOLMODE`, `LISTEN`, `MYCALL`,
  `GRIDSQUARE`, `ARQBW`, `ARQTIMEOUT` and `FECMODE` are read at attach and restored at
  exit, on the normal path, on Ctrl-C and on a fatal error. This matters at a node: a TNC
  sitting in `PROTOCOLMODE ARQ` with `LISTEN TRUE` stops answering calls the moment
  something leaves it in RXO. A restore that fails is reported loudly and exits 6, rather
  than leaving a station in a state its operator did not choose and saying nothing.
- **Nothing is reset unless you ask.** `INITIALIZE` is sent only with `--initialize`, which
  `monitor` will not accept at all. A reset is the one change no restore can undo.
- The host protocol has no way to clear `MYCALL` once set. If the station had none when
  ardopcall attached, that is reported as a failed restore, because a callsign left on a
  listening TNC makes that station answer calls addressed to it.
- **Ctrl-C sends `ABORT` before it disconnects.** Closing the socket is not a stop: the TNC
  reads a lost host as a request for an *orderly* disconnect and keys up again to send DISC
  frames. A second Ctrl-C exits at once.
- ardopcall makes no channel-busy check of its own, and says so in the transcript, because
  the TNC may not have a busy detector either.

## Not this tool's job

No ARDOP implementation: if the TNC is wrong, ardopcall's job is to show you precisely how,
not to compensate. No B2F, no Winlink, no mailbox; that is Pat's job, above this seam. No
TUI, and no unattended retry loop.

The protocol as ardopcall implements it is written down in
[`docs/host-protocol.md`](docs/host-protocol.md), with citations; the shape of the tool and
what it deliberately is not are in [`docs/design.md`](docs/design.md).

## Licence

AGPL-3.0-or-later (see [`LICENSE`](LICENSE)). Not affiliated with the ARDOP authors.
