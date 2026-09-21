# Local Bonsai extensions

Bonsai compiles every `*.cs` file in this folder when a workflow in `src/` loads. The compiler is
pre-C# 6, so keep to explicit `string.Format`, plain auto-properties and no `?.`, `??`, `nameof` or
string interpolation. Do not run `dotnet build` on `Extensions.csproj`; open the workflow in the
Bonsai editor (or run it headless) to check that the code compiles.

## Zapit optogenetics client

Zapit ([Zapit-Optostim/zapit](https://github.com/Zapit-Optostim/zapit)) is the MATLAB scanning
opto-stim software. It exposes a small TCP server; these operators are the Bonsai client, written
from the protocol in [zapit-tcp-bridge](https://github.com/Zapit-Optostim/zapit-tcp-bridge) and the
server code in `zapit/+zapit/+interfaces/@TCPserver`.

### Zapit side

- Zapit 0.12.0 or newer, started with `tcpServer.enable: true` in its settings YAML.
- Default endpoint `localhost:1488`. If Zapit runs on a separate PC, set `tcpServer.IP` there to that
  PC's address and put the same address in the rig YAML.
- A stimulus configuration must be loaded before the session starts; the workflow checks this on
  connect and stops with an error otherwise.
- For a dry run without hardware: `start_zapit('simulated', true)`, then
  `hZP.applyUnityStereotaxicCalib` and load any example stim config.

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
  raises an error so a session never runs with nothing to stimulate. The subjects are
  BehaviorSubjects declared by the host workflow, so per-trial scopes completing never close the
  connection.
- `main.bonsai` wiring: the `Zapit` group under `HardwareDevices` connects only when the task YAML
  has `optogenetics.enabled: true`, with `Host`/`Port` mapped from the rig YAML `zapit` block. In
  `StimuliPresentation`, trials whose `OptogeneticEnabled` flag was drawn true send one
  `StartStimulus` with the trial's `OptogeneticCondition`, `OptogeneticDuration` and
  `OptogeneticDelay` (Zapit times the delay and duration itself; `HardwareTriggered` is false).
  Every reply is logged to `Zapit.csv` alongside `TrialSettings.csv` on the same Harp clock.
- `../ZapitDemo.bonsai`: standalone connectivity check against a running Zapit, no rig hardware
  needed. Prints and logs every reply.

### Timing model

Two modes, selected per rig by `zapit.hardwareTriggered` in the rig YAML.

- **Software timed** (`hardwareTriggered: false`): the command is sent at stimulus onset and Zapit
  starts the waveform on receipt, plus `StartDelay`. Onset is the TCP round trip plus Zapit's DAQ
  write after the visual and auditory onset, typically a few tens of milliseconds, and is known only
  from Zapit's reply time.
- **TTL triggered** (`hardwareTriggered: true`): the same command carries the `hardwareTriggered`
  flag, so Zapit loads the waveforms and waits for its DAQ trigger input. When Zapit's reply
  confirms it is armed, the workflow writes `zapit.triggerLine` to the behavior board `TriggerValve`
  subject, which pulses that DO line with the width in `pulseWidths` (DO3, 5 ms in the template).
  Laser onset is then the TTL edge, recorded on the Harp clock in the behavior board log, so the
  exact onset is known per trial. The arming still happens at stimulus onset, so the laser lags the
  visual and auditory onset by the same round trip as above; arming earlier in the trial is the next
  step if a zero lag is required. Requires a cable from that DO line to the Zapit DAQ trigger input.

Laser power and ramp-down are not sent: they come from the stimulus config loaded in Zapit.
