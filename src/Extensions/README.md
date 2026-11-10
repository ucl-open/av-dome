# Local Bonsai extensions

Bonsai compiles every `*.cs` file in this folder when a workflow in `src/` loads. The compiler is
pre-C# 6, so keep to explicit `string.Format`, plain auto-properties and no `?.`, `??`, `nameof` or
string interpolation. Do not run `dotnet build` on `Extensions.csproj`; open the workflow in the
Bonsai editor (or run it headless) to check that the code compiles.

## Reward lines and valve calibration

Each side's reward valve is a `RewardLine` built at startup from the machine's `rig.yml`: `rewardValves`
(`{ left: DO1, right: DO2 }`, required) says which behavior board line each side is, and that line's
`outputs` entry how it pulses: a `FixedPulse` keeps its width and assumes the nominal 2 µl per pulse,
a `CalibratedPulse` reads its artefact, a platform `OpenRigs.Core.Calibration.CalibrationCurve`
written by ucl-open/coen-qc-calibrate-valve from rig-control's Calibrations page, and fits it with the entry's `fit` model. A requested
volume becomes `ceil(ul / largest calibrated volume)` equal pulses looked up on the inverse curve
and fired through the PulseController's `TriggerCalibratedPulse`.

| Operator | Kind | Role |
|---|---|---|
| `LoadSideRewardLine` | Transform `(rig, directory) -> RewardLine` | One per side (`Side` Left or Right), in the `RewardLines` group: the line `rewardValves` names for it, loaded by `LoadRewardLine.Load`. Fails at startup when `rewardValves` is missing, gives both sides one line, or names `zapit.triggerLine`'s. |
| `LoadRewardLine` | Transform `(PulseOutput, directory) -> RewardLine` | One line's entry. Fails at startup when the artefact is missing (naming the file and the workflow that creates it), malformed or not monotone; warns when it was measured on another machine or is older than the entry's `maxAgeDays`. |
| `PlanReward` | Transform `((side, ul), (left, right)) -> RewardDelivery` | Plans a command (side 1 left, 2 right) on that side's line, which fires on its own DO line: side, line, pulse count, width, volume per pulse, whether it was clamped to the calibrated range. |
| `RewardLine`, `RewardDelivery` | types | The line and the plan; `RewardDelivery` is what `DeliverReward.bonsai` fires, and the `Logging` group writes each one as a `RewardDelivered` row of `BonsaiData_Async`: Detail `<Side> DO<Line> requested=… pulses=… calibrated=… clamped=…` (rig-control reads the side from its first word), Value the µl delivered. Each calibrated line's artefact is snapshotted by its line, as `ValveCalibrationDO1` or `ValveCalibrationDO2`, whichever side `rewardValves` gives it; which side that was is `rewardValves` in the experiment's `Settings_Rig`. |

Calibrating: from rig-control's Calibrations page, which runs ucl-open/coen-qc-calibrate-valve on
this rig for the chosen line, with its board and artefact taken from `rig.yml`, and each
weight read off the balance and typed in. Then point the line at the artefact in `rig.yml`:
`DO1: { discriminatorType: CalibratedPulse, artefact: calibration/valve-DO1.json }`.
`src/CalibrateValve.bonsai`, run by hand in Bonsai, writes the same artefact.

## Zapit optogenetics client

Zapit ([Zapit-Optostim/zapit](https://github.com/Zapit-Optostim/zapit)) is the MATLAB scanning
opto-stim software. It exposes a small TCP server; these operators are the Bonsai client, written
from the protocol in [zapit-tcp-bridge](https://github.com/Zapit-Optostim/zapit-tcp-bridge) and the
server code in `zapit/+zapit/+interfaces/@TCPserver`.

### Zapit side

- Zapit 0.12.0 or newer, started with `tcpServer.enable: true` in its settings YAML.
- Default endpoint `localhost:1488`. If Zapit runs on a separate PC, set `tcpServer.IP` there to that
  PC's address and put the same address in the rig YAML.
- A conditions file must be loaded before the experiment starts; the workflow checks this on
  connect and stops with an error otherwise.
- For a dry run without hardware: `start_zapit('simulated', true)`, then
  `hZP.applyUnityStereotaxicCalib` and load any example conditions file.

### Operators

| Operator | Kind | Role |
|---|---|---|
| `ZapitClient` | Transform `ZapitCommand -> ZapitReply` | Owns the TCP connection for the lifetime of the subscription. Requests run on a dedicated thread, one at a time, so trial logic never blocks on the socket. Properties: `Host`, `Port`, `ReceiveTimeout`. |
| `CreateStartStimulus` | Transform `T -> ZapitCommand` | Builds a `sendSamples` request from its properties on every input. `ConditionNumber` 0 lets Zapit pick; `StimDuration`, `LaserPower` and `StartDelay` of 0 are left unset so Zapit applies its own defaults. |
| `CreateStopStimulus` | Transform `T -> ZapitCommand` | `stopOptoStim`: ramps the laser down. |
| `CreateZapitQuery` | Transform `T -> ZapitCommand` | `IsStimConfigLoaded`, `GetState` or `GetNumConditions`; the answer arrives in `ZapitReply.Value`. |
| `ZapitCommand`, `ZapitReply` | types | Request and reply objects; `ZapitReply` carries the presented condition (`Value`), `LaserOn`, `IsError`, and the Zapit PC clock as `ServerDatenum` (MATLAB datenum) and `ServerTime`. |

### Workflows

- `Zapit.bonsai` (include): reads `ZapitCommands`, runs `ZapitClient`, writes `ZapitReplies`.
  On connect it queries `IsStimConfigLoaded` and `GetNumConditions`; a reply of 0 to the first
  raises an error so an experiment never runs with nothing to stimulate. The subjects are
  BehaviorSubjects declared by the host workflow, so per-trial scopes completing never close the
  connection.
- `main.bonsai` wiring: the `Zapit` group under `HardwareDevices` connects only when the task YAML
  has `optogenetics.useZapit: true`, with `Host`/`Port` mapped from the rig YAML `zapit` block, and
  stops the experiment at startup when Zapit's `GetNumConditions` differs from the task's Zapit file
  (`CheckZapitConditionCount`), on any error reply from Zapit, and when a mouse other than `test`
  runs optogenetics on a rig without hardware triggering (as the corridor's, Pip 10 Nov 2026). In
  `TrialLoop`, before each trial, `ArmZapit` sends one `StartStimulus` on a trial whose
  `OptogeneticEnabled` was drawn true, with the trial's `OptogeneticCondition`,
  `OptogeneticDuration` (the task's `laserDurationSeconds`; Zapit times it itself) and
  `OptogeneticPower` (the task's `laserPowerMw`), `HardwareTriggered` from the rig YAML `zapit`
  block, and goes on only once Zapit answers that it armed that condition with the laser on
  (`CheckZapitArmed`; `ZapitClient` fails after 5 s without an answer); laser-off trials send
  nothing. Repeat trials copy the original trial's draw, so
  with `optogenetics.noLaserOnRepeats: false` a repeat presents the same condition again; with
  `true` (the default) repeats are never stimulated.
  Every reply is logged to `BonsaiData\BonsaiData_ZapitReplies` alongside `BonsaiData_Trial` on the same Harp clock.

### Timing model

Every trial runs `ArmZapit` (on a laser-on trial, the round trip to Zapit, tens of ms, so laser-on
trials start that much later after their ITI than laser-off trials), a 0.1 s wait, `StartLaser` (which pulses `zapit.triggerLine` on every
trial, laser on or off), then a wait of the task's `optogenetics.laserToStimulusSeconds`, then
`ExecuteTrial`, which commands the stimulus at once (`stimulusOnsetTime`). So the laser leads the
stimulus. Two modes, selected per rig by `zapit.hardwareTriggered` in the rig YAML:

- **TTL triggered** (`hardwareTriggered: true`): the command carries the `hardwareTriggered` flag,
  so Zapit loads the waveforms and waits for its DAQ trigger input; `StartLaser` writes
  `zapit.triggerLine` to the behavior board `TriggerPulse` subject 0.1 s after Zapit confirms it is
  armed, and the board pulses that DO line for its width in
  rig.yml's `outputs` (3000 ms on BLUE-OPTO-2, 5 ms in the template). Laser onset is the TTL edge,
  in the behavior board's Harp log, `laserToStimulusSeconds` before `stimulusOnsetTime`.
  `RewardCounter` counts only DO1 and DO2 pulses, so the trigger never registers as water.
  Requires a cable from that DO line to the Zapit DAQ trigger input.
- **Software timed** (`hardwareTriggered: false`, `test` mice only): Zapit starts the waveform
  when the command arrives, before its answer, so a little over `laserToStimulusSeconds` + 0.1 s
  before the stimulus; onset is known only from Zapit's reply time.

Laser power is sent with each `StartStimulus` (the task's `laserPowerMw`, overriding the conditions
file's); the ramp-down comes from the conditions file loaded in Zapit.
