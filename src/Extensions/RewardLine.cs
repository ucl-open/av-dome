using Bonsai;
using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using OpenRigs.Core;
using OpenRigs.Core.Calibration;
using UclOpenAvDome;

/// <summary>
/// One reward valve line (the line rig.yml's rewardValves gives a side) and how a requested volume becomes pulses on it.
/// A fixed line pulses at its configured width and assumes the nominal volume per pulse; a
/// calibrated line looks the volume up on its valve calibration curve.
/// </summary>
public class RewardLine
{
    /// <summary>Volume per pulse assumed for a fixed (uncalibrated) line, in microlitres.</summary>
    public const double NominalPulseVolumeUl = 2.0;

    const int MaximumWidthMs = 65535;
    const double SplitTolerance = 1e-9;

    readonly int line;
    readonly bool calibrated;
    readonly int fixedWidthMs;
    readonly CalibrationCurveFit curve;
    readonly string artefactPath;
    readonly string artefactJson;

    /// <summary>Creates a fixed line that pulses at the given width.</summary>
    public RewardLine(int line, int fixedWidthMs)
    {
        this.line = line;
        this.fixedWidthMs = fixedWidthMs;
        calibrated = false;
    }

    /// <summary>Creates a calibrated line from a fitted valve calibration curve.</summary>
    public RewardLine(int line, CalibrationCurveFit curve, string artefactPath, string artefactJson)
    {
        if (curve == null) throw new ArgumentNullException("curve");
        this.line = line;
        this.curve = curve;
        this.artefactPath = artefactPath;
        this.artefactJson = artefactJson;
        calibrated = true;
    }

    /// <summary>Gets the behavior board DO line, 1 to 3.</summary>
    public int Line { get { return line; } }

    /// <summary>Gets a value indicating whether the line's width comes from a calibration curve.</summary>
    public bool Calibrated { get { return calibrated; } }

    /// <summary>Gets the configured width of a fixed line, in milliseconds; 0 for a calibrated line.</summary>
    public int FixedWidthMs { get { return fixedWidthMs; } }

    /// <summary>Gets the fitted calibration curve, pulse width (ms) against volume (ul); null for a fixed line.</summary>
    public CalibrationCurveFit Curve { get { return curve; } }

    /// <summary>Gets the absolute path of the calibration artefact; null for a fixed line.</summary>
    public string ArtefactPath { get { return artefactPath; } }

    /// <summary>Gets the calibration artefact as read, for the experiment's snapshot; null for a fixed line.</summary>
    public string ArtefactJson { get { return artefactJson; } }

    /// <summary>
    /// Plans the pulses that deliver a requested volume. Fixed: ceil(ul / 2) pulses of the fixed
    /// width. Calibrated: the volume is split into n = ceil(ul / largest calibrated volume) equal
    /// pulses, each looked up on the curve. A request of zero or less gives no pulses.
    /// </summary>
    public RewardDelivery Plan(RewardSide side, double requestedUl, int interPulseIntervalMs)
    {
        if (double.IsNaN(requestedUl) || requestedUl <= 0)
        {
            return new RewardDelivery(side, line, 0, calibrated ? 0 : fixedWidthMs, 0, requestedUl, calibrated, false, interPulseIntervalMs);
        }

        if (!calibrated)
        {
            int pulses = (int)Math.Ceiling(requestedUl / NominalPulseVolumeUl - SplitTolerance);
            return new RewardDelivery(side, line, pulses, fixedWidthMs, NominalPulseVolumeUl, requestedUl, false, false, interPulseIntervalMs);
        }

        int count = Math.Max(1, (int)Math.Ceiling(requestedUl / curve.MaxY - SplitTolerance));
        CalibrationLookup lookup = LookupCalibration.Lookup(
            curve, requestedUl / count, LookupDirection.Inverse, OutOfRangePolicy.Clamp);
        int width = (int)Math.Round(lookup.Output);
        width = Math.Max(1, Math.Min(MaximumWidthMs, width));
        return new RewardDelivery(side, line, count, width, lookup.EffectiveInput, requestedUl, true, lookup.Clamped, interPulseIntervalMs);
    }
}

/// <summary>The pulses that deliver one reward on one line, and what they deliver.</summary>
public class RewardDelivery
{
    readonly RewardSide side;
    readonly int line;
    readonly int count;
    readonly int widthMs;
    readonly double volumePerPulseUl;
    readonly double requestedUl;
    readonly bool calibrated;
    readonly bool clamped;
    readonly int interPulseIntervalMs;

    /// <summary>Creates a reward delivery.</summary>
    public RewardDelivery(RewardSide side, int line, int count, int widthMs, double volumePerPulseUl, double requestedUl, bool calibrated, bool clamped, int interPulseIntervalMs)
    {
        this.side = side;
        this.line = line;
        this.count = count;
        this.widthMs = widthMs;
        this.volumePerPulseUl = volumePerPulseUl;
        this.requestedUl = requestedUl;
        this.calibrated = calibrated;
        this.clamped = clamped;
        this.interPulseIntervalMs = interPulseIntervalMs;
    }

    /// <summary>Gets the side the reward was for; rig.yml's rewardValves gave it Line.</summary>
    public RewardSide Side { get { return side; } }

    /// <summary>Gets the behavior board DO line, 1 to 3.</summary>
    public int Line { get { return line; } }

    /// <summary>Gets the number of pulses.</summary>
    public int Count { get { return count; } }

    /// <summary>Gets the width of each pulse, in milliseconds.</summary>
    public int WidthMs { get { return widthMs; } }

    /// <summary>Gets the volume each pulse delivers, in microlitres; nominal on a fixed line.</summary>
    public double VolumePerPulseUl { get { return volumePerPulseUl; } }

    /// <summary>Gets the volume the task asked for, in microlitres.</summary>
    public double RequestedUl { get { return requestedUl; } }

    /// <summary>Gets the volume the pulses deliver, in microlitres; nominal on a fixed line.</summary>
    public double DeliveredUl { get { return count * volumePerPulseUl; } }

    /// <summary>Gets a value indicating whether the width came from a calibration curve.</summary>
    public bool Calibrated { get { return calibrated; } }

    /// <summary>Gets a value indicating whether the per-pulse volume was below the calibrated range and raised to its smallest volume.</summary>
    public bool Clamped { get { return clamped; } }

    /// <summary>Gets the time from one pulse onset to the next: width plus inter-pulse interval, in seconds.</summary>
    public double PeriodSeconds { get { return 0.001 * (widthMs + interPulseIntervalMs); } }

    /// <summary>Gets the (line, width ms) pair the PulseController's TriggerCalibratedPulse subject expects.</summary>
    public Tuple<int, int> Trigger { get { return Tuple.Create(line, widthMs); } }
}

/// <summary>The side of the dome a reward valve serves.</summary>
public enum RewardSide
{
    /// <summary>The left lick spout.</summary>
    Left,
    /// <summary>The right lick spout.</summary>
    Right
}

/// <summary>
/// Builds one side's reward line from rig.yml: rewardValves names the side's line (left: DO1), and
/// that line's entry in behaviorBoard.main.pulseController.outputs is loaded as LoadRewardLine
/// loads it. Fails at startup, naming what to fix in rig.yml, if rewardValves is missing, gives
/// both sides one line, or names the line zapit.triggerLine uses.
/// </summary>
[Combinator]
[DefaultProperty("Side")]
[Description("Builds the left or right reward line from rig.yml: the line rewardValves names for the side, with its entry in behaviorBoard.main.pulseController.outputs (a calibrated entry reads and fits its valve calibration, a fixed one keeps its width). Input is a Zip of the RigConfigs and RigConfigDirectory subjects.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class LoadSideRewardLine
{
    RewardSide side = RewardSide.Left;

    /// <summary>Gets or sets which side's reward line to build.</summary>
    [Description("Which side's reward line to build: rig.yml's rewardValves says which line it is.")]
    public RewardSide Side
    {
        get { return side; }
        set { side = value; }
    }

    /// <summary>Builds the side's reward line for each (rig configuration, rig configuration directory) pair.</summary>
    public IObservable<RewardLine> Process(IObservable<Tuple<UclOpenAvDomeRig, string>> source)
    {
        return source.Select(delegate (Tuple<UclOpenAvDomeRig, string> input)
        {
            return Load(input.Item1, input.Item2, side, DateTimeOffset.UtcNow);
        });
    }

    /// <summary>The DO line number (1 to 3) rig.yml's rewardValves gives a side, or why it gives none.</summary>
    public static int LineOf(UclOpenAvDomeRig rig, RewardSide side)
    {
        RewardValves valves = rig.RewardValves;
        if (valves == null)
        {
            throw new InvalidOperationException(
                "rig.yml has no rewardValves, which says which behavior board line drives each side's reward valve. " +
                "Add it, such as rewardValves: { left: DO1, right: DO2 }.");
        }
        if (valves.Left == valves.Right)
        {
            throw new InvalidOperationException(
                "rig.yml's rewardValves gives " + valves.Left + " to both sides; each side needs its own line.");
        }
        ValveLine valve = side == RewardSide.Left ? valves.Left : valves.Right;
        int line = (int)valve + 1;
        if (rig.Zapit != null && rig.Zapit.TriggerLine == line)
        {
            throw new InvalidOperationException(
                "rig.yml's rewardValves gives " + valve + " to the " + side.ToString().ToLowerInvariant() +
                " valve, but zapit.triggerLine uses DO" + line + " for the Zapit TTL.");
        }
        return line;
    }

    /// <summary>Builds the side's reward line; public so the check can run without a workflow.</summary>
    public static RewardLine Load(UclOpenAvDomeRig rig, string directory, RewardSide side, DateTimeOffset now)
    {
        int line = LineOf(rig, side);
        BehaviorBoard board;
        if (rig.BehaviorBoard == null || !rig.BehaviorBoard.TryGetValue("main", out board) ||
            board.PulseController == null || board.PulseController.Outputs == null)
        {
            throw new InvalidOperationException("rig.yml has no behaviorBoard.main.pulseController.outputs, which the reward lines are.");
        }
        PulseOutputs outputs = board.PulseController.Outputs;
        PulseOutput output = line == 1 ? outputs.DO1 : line == 2 ? outputs.DO2 : outputs.DO3;
        return LoadRewardLine.Load(output, directory, line, now);
    }
}

/// <summary>
/// Builds a reward line from its entry in the rig configuration. A CalibratedPulse entry reads its
/// valve calibration artefact, a platform CalibrationCurve, from the rig configuration directory
/// and fits it with the model named in the entry; a FixedPulse entry keeps its width. Fails at
/// startup if the artefact is missing, malformed or not monotone, and warns on the console if it
/// was measured on another machine or is older than the entry's maxAgeDays.
/// </summary>
[Combinator]
[DefaultProperty("Line")]
[Description("Builds a reward line from its pulse output entry in the rig configuration and the rig configuration directory. A calibrated entry reads its valve calibration artefact (a CalibrationCurve written by the platform's DispenseCalibration procedure, run from rig-control's Calibrations page by ucl-open/coen-calibrate-valve) and fits it with the entry's fit model; a fixed entry keeps its width. Warns when the artefact was measured on another machine or is older than the entry's maxAgeDays. Input is a Zip of the line's output entry and the RigConfigDirectory subject.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class LoadRewardLine
{
    int line = 1;

    /// <summary>Gets or sets the behavior board DO line this entry drives, 1 (left) or 2 (right).</summary>
    [Description("The behavior board DO line this entry drives: 1 for the left valve, 2 for the right.")]
    public int Line
    {
        get { return line; }
        set { line = value; }
    }

    /// <summary>Builds the reward line for each (pulse output, rig configuration directory) pair.</summary>
    public IObservable<RewardLine> Process(IObservable<Tuple<PulseOutput, string>> source)
    {
        return source.Select(delegate (Tuple<PulseOutput, string> input)
        {
            return Load(input.Item1, input.Item2, line, DateTimeOffset.UtcNow);
        });
    }

    /// <summary>Builds the reward line for one entry; public so the check can run without a workflow.</summary>
    public static RewardLine Load(PulseOutput output, string directory, int line, DateTimeOffset now)
    {
        FixedPulse fixedPulse = output as FixedPulse;
        if (fixedPulse != null)
        {
            return new RewardLine(line, fixedPulse.PulseWidthMs);
        }

        CalibratedPulse calibratedPulse = output as CalibratedPulse;
        if (calibratedPulse == null)
        {
            throw new InvalidOperationException("Reward line DO" + line + " has no pulse source in the rig configuration.");
        }
        if (string.IsNullOrEmpty(calibratedPulse.Artefact))
        {
            throw new InvalidOperationException("Reward line DO" + line + " is calibrated but names no artefact.");
        }

        string path = Path.IsPathRooted(calibratedPulse.Artefact)
            ? calibratedPulse.Artefact
            : Path.Combine(directory, calibratedPulse.Artefact);
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                "No valve calibration for reward line DO" + line + ". Expected: " + path +
                ". Calibrate DO" + line + " on this rig from rig-control's Calibrations page to create it, or point the line at a FixedPulse.");
        }

        string json = File.ReadAllText(path);
        CalibrationCurve artefact = JsonConvert.DeserializeObject<CalibrationCurve>(json);
        if (artefact == null || artefact.Points == null || artefact.Points.Count < 2)
        {
            throw new InvalidOperationException("The valve calibration for reward line DO" + line + " needs at least two points: " + path);
        }

        if (!string.IsNullOrEmpty(artefact.MachineName) &&
            !string.Equals(artefact.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                "WARNING: the valve calibration for reward line DO" + line + " was measured on " + artefact.MachineName +
                ", not on this machine (" + Environment.MachineName + "): " + path + ". Recalibrate here before trusting the volumes.");
        }
        if (calibratedPulse.MaxAgeDays > 0)
        {
            double ageDays = (now - artefact.CalibratedAt).TotalDays;
            if (ageDays > calibratedPulse.MaxAgeDays)
            {
                Console.WriteLine(
                    "WARNING: the valve calibration for reward line DO" + line + " is " + Math.Floor(ageDays) + " days old (measured " +
                    artefact.CalibratedAt.ToString("yyyy-MM-dd") + "), older than the " + calibratedPulse.MaxAgeDays +
                    " days allowed by maxAgeDays in rig.yml: " + path + ". Recalibrate, or raise maxAgeDays if that is intended.");
            }
        }

        CalibrationCurveFit fit = new CalibrationCurveFit(
            artefact.Points.Select(delegate (CalibrationPoint point) { return point.X; }),
            artefact.Points.Select(delegate (CalibrationPoint point) { return point.Y; }),
            calibratedPulse.Fit,
            2);
        if (!fit.IsMonotone)
        {
            throw new InvalidOperationException(
                "The valve calibration for reward line DO" + line + " is not monotone with the " + calibratedPulse.Fit +
                " fit, so a volume cannot be turned into a pulse width: " + path + ". Recalibrate DO" + line + " from rig-control's Calibrations page.");
        }
        return new RewardLine(line, fit, path, json);
    }
}

/// <summary>
/// Plans each reward command, a (side, microlitres) pair, on that side's reward line.
/// </summary>
[Combinator]
[DefaultProperty("InterPulseIntervalMs")]
[Description("Plans each reward command, a (side, microlitres) pair with 1 the left side and 2 the right, on that side's reward line, the line rig.yml's rewardValves gives it: how many pulses, how wide, and what they deliver. Input is a WithLatestFrom of the command and a Zip of the left and right reward lines.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class PlanReward
{
    int interPulseIntervalMs = 50;

    /// <summary>Gets or sets the gap between consecutive pulses, in milliseconds.</summary>
    [Description("The gap between consecutive reward pulses, in milliseconds; mapped from the rig configuration's interPulseInterval.")]
    public int InterPulseIntervalMs
    {
        get { return interPulseIntervalMs; }
        set { interPulseIntervalMs = value; }
    }

    /// <summary>Plans each command on the left or right reward line.</summary>
    public IObservable<RewardDelivery> Process(IObservable<Tuple<Tuple<int, double>, Tuple<RewardLine, RewardLine>>> source)
    {
        return source.Select(delegate (Tuple<Tuple<int, double>, Tuple<RewardLine, RewardLine>> input)
        {
            return Plan(input.Item1.Item1, input.Item1.Item2, input.Item2.Item1, input.Item2.Item2, interPulseIntervalMs);
        });
    }

    /// <summary>Plans one command, side 1 (left) or 2 (right); public so the check can run without a workflow.</summary>
    public static RewardDelivery Plan(int side, double requestedUl, RewardLine left, RewardLine right, int interPulseIntervalMs)
    {
        if (side == 1) return left.Plan(RewardSide.Left, requestedUl, interPulseIntervalMs);
        if (side == 2) return right.Plan(RewardSide.Right, requestedUl, interPulseIntervalMs);
        throw new ArgumentException("No reward side " + side + "; a reward command names the left (1) or the right (2) side.");
    }
}
