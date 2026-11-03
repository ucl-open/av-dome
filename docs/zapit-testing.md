# Testing the Zapit link

How to check, step by step, that Bonsai can drive Zapit before the first optogenetics session. Steps 1 to 3 need no rig hardware; step 4 is the dummy session on the rig. Each step has a pass condition. Do them in order, because each one assumes the previous passed.

## Before you start

- Zapit 0.12.0 or newer on the Zapit PC.
- In the Zapit settings YAML, under `tcpServer`, set `enable: true`. Restart Zapit after editing. (Zapit docs: [TCP/IP developer notes](https://zapit.gitbook.io/user-guide/developer-notes/tcp-ip).)
- If Zapit runs on a **different PC** from Bonsai: in MATLAB run `zapit.interfaces.getIPaddress`, put that address in `tcpServer.IP` in the Zapit settings, and in the rig YAML (`C:\RigConfigs\<MachineName>.yml`) under `zapit: host:`. Same PC: leave both at the default (`localhost` / `127.0.0.1`). Port stays `1488` unless you changed it in Zapit.
- Zapit accepts **one client at a time**. Close any other Zapit client (MATLAB, Python, another Bonsai) before testing. `hZP.tcpServer.isClientConnected` in MATLAB shows whether one is attached.

## Step 1: start Zapit with a stimulus config

In MATLAB, simulated mode is enough for steps 1 to 3 and safe for step 4:

```matlab
start_zapit('simulated', true)     % creates hZP in the base workspace without touching hardware
hZP.applyUnityStereotaxicCalib     % stands in for a real calibration so stimuli can be sent
```

Load your own stimulus config through the Zapit GUI (File, Load stim config). Note the full path of the file you loaded; step 3 needs it.

## Step 2: connectivity, `src/ZapitDemo.bonsai`

Open `src/ZapitDemo.bonsai` in Bonsai. Click the `Zapit` include and set `Host` and `Port` to match the Zapit PC. Right-click the `ZapitReplies` subscribe node, Show Visualizer, then Start.

Within five seconds you should see, in order:

| Reply | Meaning |
|---|---|
| `IsStimConfigLoaded, value=1` | Zapit has a stim config |
| `GetNumConditions, value=N` | N conditions in that config |
| `StartStimulus, value=3, laserOn=1` | Zapit presented condition 3 |
| `GetState, value=1` | Zapit is stimulating (0 idle, 2 ramp-down) |
| `StopStimulus, value=1` | stopped |

The same rows are written to `src/zapit-demo.csv`.

**Pass:** the five replies arrive with `error=False`.

**Fail modes:**
- the workflow stops immediately with "Could not connect to Zapit at host:port": Zapit is not running, `tcpServer.enable` is false, or wrong host/port.
- It stops with "Zapit has no stimulus configuration loaded": redo step 1.
- A reply times out after five seconds: another client may already be connected to Zapit.

## Step 3: trial logic without the rig, `src/ZapitTrialTest.bonsai`

This runs the real trial draw (`InitialiseTrial`) and the same Zapit wiring as `main.bonsai`, but with timers instead of the behaviour, so it works on any PC with a rig YAML.

1. Make sure `C:\RigConfigs\<MachineName>.yml` exists on the PC you run it on or change the path (copy `config/rig-template.yml` and rename it if you are not on a rig PC). Its `zapit:` block sets host, port and `hardwareTriggered`.
2. Edit `config/task-zapit-test.yml`: set `zapitConfigPath` to the stim config file you loaded in step 1. Bonsai reads the condition numbers from it. Everything else matches `task.yml` except `optogenetics.enabled: true`.
3. Open `src/ZapitTrialTest.bonsai`, open visualizers on `ZapitReplies` and on the `Trial` group, and Start.

Ten trials are drawn, one every two seconds. Each trial line shows `(trialType, IsRepeat, OptogeneticEnabled, OptogeneticCondition, OptogeneticDuration, OptogeneticDelay)`. With the default `stimProbability: 0.75` about three quarters of trials are opto trials. For every opto trial a `StartStimulus` reply must follow whose `value` equals that trial's `OptogeneticCondition` and whose `laserOn` is 1. Non-opto trials produce no reply. In MATLAB, Zapit prints or logs each stimulus as it would in an experiment.

If the rig YAML has `hardwareTriggered: true`, each armed reply pulses the behavior board DO line named by `zapit.triggerLine` (DO3), for `pulseWidths.PulseDO3` milliseconds, and the `TriggerPulse` visualizer shows the line number. The workflow therefore needs the behavior board on the port in the rig YAML; connect DO3 to the Zapit DAQ trigger input to see the laser fire on the TTL.

**Pass:** every opto trial has a matching reply, conditions agree, no `error=True`.

**Fail modes:** console warning "Optogenetics enabled but no Zapit conditions available": the `zapitConfigPath` is wrong or the file has no `stimLocationsNN` entries. Replies show `value=255` or `error=True`: Zapit refused `sendSamples`, check the MATLAB window (missing or incorrect config loaded).

## Step 4: dummy session on the rig, `src/main.bonsai`

With Zapit still in simulated mode (or with the laser shutter closed):

1. In `config/task.yml` set `optogenetics.enabled: true` and `zapitConfigPath` as in step 3.
2. In the rig YAML set `zapit.hardwareTriggered` to `false` for the first run.
3. Run a normal short session from `main.bonsai`. The workflow stops at startup with a clear error if Zapit is unreachable or has no config loaded.
4. In the session folder compare `Zapit.csv` and `TrialSettings.csv`. `Zapit.csv` columns: `Seconds` (Harp clock), `command` (1 = StartStimulus, 2 and 4 = the startup queries), `value` (condition presented), `laserOn`, `isError`, `serverDatenum` (Zapit PC clock). Every `TrialSettings.csv` row with `OptogeneticEnabled = True` must have a `command = 1` row in `Zapit.csv` with the same condition, a fraction of a second later.

**Pass:** the two files agree trial by trial and Zapit's own trial log lists the same sequence.

## Step 5 (once the trigger cable is in): TTL mode

Connect behavior board DO3 to the Zapit DAQ trigger input, set `zapit.hardwareTriggered: true` and `pulseWidths.PulseDO3: 5` in the rig YAML, and repeat step 4. Laser onset now follows the TTL edge; the edge is recorded in the behavior board event log on the Harp clock, so the true onset is known for every trial.

## Which timestamp means what

- `Seconds` in `Zapit.csv` is a software timestamp: the latest Harp time when Bonsai received the reply. It is not laser onset.
- `serverDatenum` is the Zapit PC's wall clock when it sent the reply, after the waveform was written to the DAQ. It is not synchronised with Harp time and since it is a PC clock likely has tens of milliseconds of jitter. Use it only to match rows against Zapit's own trial log.
- In TTL mode the DO3 event in the behavior board log is the hardware timestamp of laser onset (plus any `startDelaySeconds`), precisely aligned with Harp time. Use it for analysis; use `Zapit.csv` for which condition ran and whether the laser was on.
- In software-timed mode there is no hardware onset signal. Zapit starts the DAQ and then replies, so onset lies between the request being sent and `Seconds`, most likely a few milliseconds before `Seconds`, plus `startDelaySeconds` if set. Requests are not logged at present, so only the upper bound is in the data.
- TTL mode starts a few milliseconds later than software mode (reply, then a serial command to the board), but its onset is timestamped exactly. Use TTL mode for experiments.

### Optional: measure the laser directly

To record actual onset and offset in either mode, and to check that every DO3 pulse produced a stimulus and that sham trials stayed dark, wire the laser analog output from the Zapit DAQ into a free behavior board analog input (ADC0 or ADC1, if not already used for a photodiode). The board already samples both inputs at 1 kHz into its log on the Harp clock, so no workflow change is needed.
