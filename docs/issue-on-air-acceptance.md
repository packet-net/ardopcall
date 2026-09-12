Title: ardop: the on-air acceptance (Rung 5) has no issue, and it is the last thing between ARDOP and "done"

## Why this issue exists

Two documents have said for two months that this is the remaining ARDOP work, and neither is an issue.

- `docs/roadmap.md` #6: "**Remaining: the on-air acceptance** - peer-to-peer ARDOP on the 40m UK packet channel from **GB7RDG's HF port** (operate as M0LTE), where ARDOP stations already run."
- `docs/ardop/plan.md`, "Open legs", leg 2: "**Rung 5, on-air.** Nothing in this campaign is on-air evidence ... In particular no live ARQ session has ever exercised the ConAck acceptance shipped in M0LTE.Ardop 0.4.0. Exit: one real ARQ connection with a deployed peer, logged."

An audit on 2026-09-12 found no issue tracking it. Phases A to D of `docs/ardop-design.md` §7 are complete and the ladder's Rungs 0 to 4 are green; Rung 5 is the only one left, and it is the only rung that needs a radio, a licence and a human. It should be trackable like everything else.

## Exit criterion

Not to be widened. From both documents above, identically:

> one real ARQ connection with a deployed peer, logged

`CONNECTED <call> <bw>`, data moving, an orderly `DISCONNECTED`, with a transcript. A ping answered, an ID heard, or an FEC frame decoded elsewhere are all useful intermediate rungs and none of them closes this. A Winlink gateway session is explicitly optional (roadmap #6).

## The procedure

`docs/ardop-on-air-bench.md`, written 2026-09-12, which roadmap #6 and its "Needs Tom + a radio" item 3 both required before the session. It carries the pre-flight, a six-rung transmit ladder from receive-only monitoring to a 500 Hz ARQ session with data, the abort procedure, and the evidence plan.

## What this rung will be the first to exercise

- **Any ARDOP transmission at all.** pdn-soundmodem has never keyed a transmitter with an ARDOP waveform, on air or into a load: `docs/flex-integration.md` §8 item 2 is unexecuted, and roadmap #11's last remaining item is exactly that dummy-load frame. The bench doc folds it in as rung 0b, so this issue can close roadmap #11's tail as a side effect.
- **The ConAck acceptance shipped in M0LTE.Ardop 0.4.0** (M0LTE.Ardop#3, tag v0.4.0 on merge 7616ce9). It is on the connection path and has bench plus wild-replay evidence only.
- **A real path measurement of any kind.** The first `PINGACK <snDb> <quality>` will be the first end-to-end figure this project has ever had for ARDOP.

## Known blockers and risks, all recorded in the bench doc

1. **No busy detector.** `BUSY TRUE/FALSE` is never sent and `BUSYDET`/`BUSYBLOCK` are inert (`ArdopHostTnc.cs:44-56`, README.md:96). Nothing in software declines to transmit over somebody else's session.
2. **ARDOP bypasses channel access** (`ownsChannelTiming: true`, `Program.cs:1994`, shipped in 3c7fdd5 / #171), skipping both the transmit inhibit and the p-persistence roll (`SoundModemChannel.cs:446-452`, `:993-999`). The operator is the only channel-access mechanism. Together with 1, that is why the bench doc authorises attended operation only.
3. **`ARQBW` is not capped by the config.** Measured on the live station 2026-09-12: `ARQBW 2000MAX` despite `"bandwidth": 500`. Filed separately (`issue-arqbw-not-capped.md`); the bench doc requires setting and reading back `ARQBW` before any transmission. Without it, a negotiated 2000 Hz session spans 7049.95 to 7051.95 kHz, across three of our own slots and outside slot 2's coordination.
4. **The interop evidence is old, narrow and out of CI.** `ArdopHostLiveTests` is skip-by-default on `ARDOPCF` / `ARDOP_ALOOP_CARD` / `PAT` (`:442`, `:513`, `:569`, `:619-624`, `:803`), last substantive commit 200de2c, 2026-07-17, and `.github/workflows/ci.yml` sets none of those variables. It also cannot be re-run on the dev box: `snd-aloop` is not available in the LXC.
5. **The unresolved question in `ArdopSharedChannelSessionTests.cs:420-434`**: whether the flaky pdn-to-pdn session bench indicates a genuine timing margin problem or an unrealistic cross-connect. 2 of 4 runs failing on 2026-08-02, failures always "CONNECT TO ... FAILED" and never wrong data. A rung-4 result on air is the first real evidence either way. Note the station runs ARDOP at its native 1500 Hz centre, so the bench's two shifted-centre cases do not describe it; the native case is the test's own control.

## Station and peers

GB7RDG, FlexRadio 6500 into ANT1, slot 2 of the UK 40 m plan at 7050.95 kHz, 500 Hz, ARDOP host interface on `pdn-soundmodem:8200` (data 8201). Operate as **M0LTE**, not GB7RDG: this is a hand-run experiment, not a node service. Known peers on the slot from the off-air campaign: **GB7BPQ, GB7BWR-2, DC7DE**. GB7BPQ runs a half-hourly ID pair at :00:57 and :30:57, which is a free recurring receive check.

The band plan (https://ukpacketradio.network/info:40m) says slot 2 is "reserved for ARDOP (not AX.25) in coordination with existing users". Those users were there first, and this issue is not closed by any transmission that ignores them.

## Definition of done

- [ ] The dummy-load frame keyed and confirmed (closes roadmap #11's tail).
- [ ] The exit criterion met and logged, or the attempt recorded as an honest negative with its mechanism.
- [ ] Evidence filed under `/home/tf/ardop-campaign-evidence/on-air-<date>/` with a README in the existing shape.
- [ ] Results written into `docs/ardop-on-air-bench.md`; a dated entry in `docs/mode-validation.md`; leg 2 closed in `docs/ardop/plan.md`; #6 closed in `docs/roadmap.md`; the amendment-log entry in `docs/plan.md` §17.

Blocked on: a radio, a licensed operator, and a quiet window on a shared slot. Not blocked on code.
