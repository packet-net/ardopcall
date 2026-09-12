Title: ardop: port the busy detector, so ARDOP can share a slot without a human as the channel-access mechanism

## What is missing

The busy detector is the one part of ardopcf deliberately left unported. It is documented as a deviation in three places and nothing hides it:

- `M0LTE.Ardop` `Host/ArdopHostTnc.cs:44-56`: "the busy detector is not ported, so `BUSY TRUE/FALSE` notifications are never sent and BUSYDET/BUSYBLOCK are accepted but inert". The two settings are parsed and stored and read back correctly (`:481-489`, `_busyDet` and `_busyBlock` at `:102-103`) and then nothing consults them.
- `M0LTE.Ardop` `Arq/ArdopArqEngine.cs:57-59`: "the busy detector is not ported, so BUSYBLOCK/ConRejBusy origination is absent (we still honour a received ConRejBusy) and the final-ID busy gate is a plain timer", and at the IRS connect path, `:577`: "(BUSYBLOCK/ConRejBusy origination not ported - no busy detector yet.)"
- `README.md:96`: the same statement in the user-facing divergence list.

So we receive and honour `ConRejBusy` from a peer, and we can never send one. We accept `BUSYBLOCK TRUE` from a host and it changes nothing.

## Why it is a prerequisite for unattended or node-backed operation

The ARDOP protocol assumes this facility exists. Spec Rev 2.0 §2.7 lists "listen before transmit and busy detectors" as how the protocol minimises interference with existing users of a frequency, and rule 1.5 makes `ConRejBusy` the mechanism by which a station refuses a connection on a busy channel (`docs/refs/ardop-spec-rev2.md:44`, `:449`, and Fig D-4).

On top of that, ARDOP on this station **deliberately bypasses channel access**. An ARDOP burst is queued with `ownsChannelTiming: true` (`src/Packet.SoundModem.Daemon/Program.cs:1994`), which skips **both** the transmit inhibit and the p-persistence roll (`src/Packet.SoundModem/Channel/SoundModemChannel.cs:446-452`, `:507-509`, `:993-999`). That was the right fix for a real problem, shipped in 3c7fdd5 (#171), 2026-08-02: at a shifted centre ARDOP's own signal sits inside a packet modem's passband and asserts that modem's busy detector, so deferring would partly mean deferring to itself, and an ARQ turnaround has a budget a p-persistence roll does not respect. It was measured costing about 20 % of session time at p=63 before the fix (`docs/mode-validation.md`, 2026-08-02).

Put the two together and the station has **no channel-access mechanism for ARDOP at all except the operator**. The packet modems' CSMA does not apply to it and never sees it; the TNC has nothing to consult; the host cannot ask for the behaviour. It also cannot detect the problem after the fact: receive processing is gated off for the length of every keyup (half duplex), so the station is deaf while it transmits and cannot hear a collision it caused.

That is acceptable for a single attended acceptance session with a human watching a waterfall, which is exactly what `docs/ardop-on-air-bench.md` authorises and no more. It is **not** acceptable for:

- **`LISTEN TRUE` left running.** The live station already has `LISTEN TRUE` and `PROTOCOLMODE ARQ` set; only an empty `MYCALL` keeps it inert (probed read-only 2026-09-12). Give it a callsign and walk away and it will answer inbound calls, key over whatever is in progress, and have no way to refuse a call on a busy channel because `ConRejBusy` origination does not exist.
- **A node or mail client driving port 8200.** A node retries. A retrying transmitter on a shared HF slot with no carrier sense is the thing amateur channel-access rules exist to prevent.
- **Any automatic retry loop.** `ardopcall` has no such loop by design and its design doc says why: "a tool that retries unattended is the thing we are specifically trying not to build yet: the TNC has no busy detector, so a human stays in the loop."

The slot this matters on is coordinated. The UK 40 m band plan (https://ukpacketradio.network/info:40m) says of slot 2, at 7050.95 kHz: "reserved for ARDOP (not AX.25) in coordination with existing users." The existing users are known by name from the off-air campaign: GB7BPQ, GB7BWR-2, DC7DE (`docs/ardop/plan.md`, A0 scoreboard). Sharing with them politely is the whole point of the facility.

## The roadmap already flags it

`docs/roadmap.md` #6, in the same sentence that asks for this bench doc: "Write the on-air bench doc before the session; **add the busy-detector port if channel-sharing needs it on air**."

The bench doc is now written, and its conclusion is that channel-sharing needs it the moment the station is left alone. So this issue is that conditional coming due, not new scope.

## What a port involves

Scoped in `docs/ardop-design.md` at the time of the original survey, and the numbers there are estimates from the reference read, not measurements:

- **Reference**: ardopcf `BusyDetect.c`, 233 lines, estimated ~200 lines ported (`docs/ardop-design.md:79`). The algorithm is an FFT-bin spectral signal-to-noise estimator on ~11.72 Hz bins, `BusyDetect3`, `BusyDetect.c:52` (`:176` item 9).
- **Reuse**: the design's own reuse map already earmarks `Dsp/Fft.cs` "for busy detector spectrum" (`:184`). Note this is *not* the same thing as `Modems/EnergyBusyDetector`, which is the packet side's block-power-vs-floor detector (`PROVENANCE.md:46`) and is a different instrument for a different job; a spectral estimator is what ARDOP's thresholds and its `BUSYDET 0-10` scale are defined against.
- **Where it lands**: `M0LTE.Ardop`, not this repository. The host commands, the notifications and the `ConRejBusy` origination path all live there, and pdn-soundmodem consumes it. MIT to GPL-3.0-or-later is the easy direction and is already settled for this port (`docs/ardop-design.md` §8.1).

The surface it has to light up, all of which already exists and is inert:

1. `BUSY TRUE` / `BUSY FALSE` notifications on the command socket when channel state changes.
2. `BUSYDET <0-10>` actually setting the threshold (it is parsed and range-checked at `ArdopHostTnc.cs:485-487` already).
3. `BUSYBLOCK TRUE` causing `ConRejBusy` origination at the IRS connect path (`ArdopArqEngine.cs:577`), per spec rule 1.5 and Fig D-4.
4. The final-ID busy gate becoming a busy gate rather than the plain timer it is today (`ArdopArqEngine.cs:58-59`).

## How to know it works

The instruments already exist, which is the cheap part.

- **The wild corpus is the natural test set.** `/home/tf/capture-40m/raw` plus `sm-ota ardop-monitor` scored ~45 h of real 40 m traffic, and the same audio replayed through a busy detector gives a measured busy/clear time series against sessions we already know the boundaries of (`docs/ardop/plan.md` A0, and `/home/tf/ardop-campaign-evidence/README.md` for the reproduction commands).
- **ardopcf is the referee**, as it was for the A1 autopsy: it is built at `/home/tf/ardopcf/ardopcf` (pinned commit a7c9228, 1.0.4.1.3) and runs with no sound card as `./ardopcf 18501 null null -m`. Its own `BUSY TRUE`/`BUSY FALSE` over the same audio is the comparison, exactly as the ConAck policy question was settled in A2.
- **A positive control is mandatory**, the campaign's day-one lesson: a detector that never asserts and a broken chain look identical.

Caveat to record with any result: the wild corpus is one-sided, so a "clear" verdict from it means "we could not hear anybody", which is the same thing the detector itself will say on air and not an independent check of it.

## Why it is not urgent, said plainly

Nothing is currently at risk, because the live station's `MYCALL` is empty and it cannot transmit at all. The attended acceptance session is authorised without this. This issue is the gate on everything after that session: unattended listening, a node pointed at the TNC, Winlink service, or any retrying client. It should be closed before any of those, and it does not block the acceptance.

Found while preparing the ARDOP on-air acceptance (Rung 5), before transmitting anything.
