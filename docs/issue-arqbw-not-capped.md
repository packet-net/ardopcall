Title: config: an ardop modem's "bandwidth" never reaches the TNC, so ARQBW stays 2000MAX and the docs say otherwise

## What happens

GB7RDG's live station runs this modem entry:

    { "subChannel": 1, "mode": "ardop", "bandwidth": 500, "rfFrequency": 7050950, "port": 8200 }

Querying the running TNC's host interface, read-only:

    -> VERSION
    <- VERSION pdn-soundmodem_0.4.0
    -> ARQBW
    <- ARQBW 2000MAX

The configured 500 Hz has not reached the TNC. `ARQBW` is the library default,
`ArdopArqConfig.cs:24`, whose own summary says it "Governs both what an IRS accepts and
what an ISS requests". So with this config the station would accept, and would itself
request, sessions up to 2000 Hz.

## Why that is wrong

Two places document the key as doing exactly what it does not do:

- `src/Packet.SoundModem.Daemon/DaemonConfig.cs:83` - "Setting it also caps what ARDOP will
  negotiate (200/500/1000/2000)."
- `src/Packet.SoundModem.Daemon/RfPlan.cs:409-410` - "An ARDOP modem's width is its
  negotiated maximum; \"bandwidth\" on it plans for less and caps what it negotiates."

Nothing sets it. `grep -rn "ArqBandwidth\|CallBandwidth" src/` matches nothing outside the
M0LTE.Ardop package itself. The key is consumed in exactly two places, both of which only
describe the signal rather than constrain it:

- `Program.cs:1378` - the survey's band accounting, `ardop.Bandwidth ?? WidestBandwidthHz`.
- `TransmitFilterPlan.cs` - the radio's transmit filter high cut.

## Why it matters on air

7050.95 is slot 2 of the 40m carve-out, coordinated for ARDOP at 500 Hz with 150 Hz of guard
either side (https://ukpacketradio.network/info:40m). A negotiated 2000 Hz session centred
there spans roughly 7049.95 to 7051.95 MHz: across slot 1 (AFSK300 at 7050.30) and slot 3
(BPSK300 at 7051.60), and outside what slot 2 is coordinated for. The station is currently
inert because `MYCALL` is empty, so nothing can happen today, but that is the only thing
stopping it.

## Suggested fix

Apply the configured bandwidth to the TNC at start-up, as both `ARQBW` and `CALLBW`, mapping
200/500/1000/2000 to the MAX form. Reject any other value at config-parse time rather than
silently planning for a width ARDOP cannot negotiate. If the cap is deliberately not wanted,
the two doc comments should say so instead.

Worth deciding at the same time whether an unset `bandwidth` on an ardop entry should keep
defaulting to the widest, or should refuse to start on a planned band where 2000 Hz does not
fit. `ArdopChannelBridge.cs:254` already computes and warns about exactly that.

Found while preparing the ARDOP on-air acceptance (Rung 5), before transmitting anything.
