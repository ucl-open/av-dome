using Bonsai;
using Bonsai.Harp;
using OpenTK;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

// One row of BonsaiData_Trial. Each field is a column, named and typed as the lab's list
// (K:\config\data-dictionary.json) allows and as schemas\trial-columns.yml, which says what each
// means, lists it (rig-control checks both before launch); the order is the file's. The row
// travels Timestamped, and its Seconds (the Harp time the outcome was known: the choice, or the
// end of the response window) is the file's first column.
public class TrialRow
{
    public int trialNumber;
    public string trialType;  // in words: what was shown (InitialiseTrial.TrialTypeName)
    public string target;
    public string choice;
    public string outcome;
    public double stimulusOnsetTime;  // Harp time the stimulus was commanded on; NaN if it never was
    public double choiceTime;  // Harp time of the lick that made the choice; NaN on NoResponse
    public double rewardSizeUl;
    public bool bothSpouts;
    public int repeatCount;
    public double itiSeconds;  // the interval drawn before this trial (the last trial's), NaN on the first
    public bool punishSound;  // the error sound played: Incorrect, with punishment.sound enabled
    public double punishTimeoutSeconds;  // the timeout after this trial: Incorrect, or a miss not auto-rewarded; else 0
    public double visualAzimuth;
    public double visualElevation;
    public double visualContrast;
    public double visualPhase;
    public double audioAzimuth;  // the speaker that played, after snapping to the nearest wired one
    public double audioElevation;
    public int audioWaveformIndex;
    public int laserCondition;  // 0 when not stimulated (Zapit numbers conditions from 1)
    public bool autoRewarded;  // a miss on an auto-reward trial, rewarded at the target spout
}

[Combinator]
[Description("Builds the trial's BonsaiData_Trial row when its choice is made. Input: the choice (Left, Right or NoResponse) stamped with the Harp time it was made, with the trial as drawn, the Harp time of its stimulus onset (NaN if none), the index of the speaker that played (-1 if none) and the speaker locations.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CreateTrialRow
{
    public IObservable<Timestamped<TrialRow>> Process(IObservable<Tuple<Timestamped<string>, Tuple<Trial, double, int, Vector3[]>>> source)
    {
        return Observable.Defer(() =>
        {
            // The interval before a trial is the one the last trial drew (Pip, 9 Nov 2026: itiSeconds
            // means the wait before this trial in every task); none before the first.
            double previousIti = double.NaN;
            return source.Select(value => Build(value, ref previousIti));
        });
    }

    static Timestamped<TrialRow> Build(Tuple<Timestamped<string>, Tuple<Trial, double, int, Vector3[]>> value, ref double previousIti)
    {
        var choice = value.Item1.Value;
        var trial = value.Item2.Item1;
        var speaker = value.Item2.Item3;
        var locations = value.Item2.Item4;
        bool played = trial.audioEnabled && locations != null && speaker >= 0 && speaker < locations.Length;
        var outcome = choice == "NoResponse" ? "NoResponse" : choice == trial.targetSide ? "Correct" : "Incorrect";
        // ApplyConsequences: the timeout follows an incorrect choice or a miss, unless the miss was
        // auto-rewarded; the error sound only an incorrect choice.
        bool timedOut = outcome == "Incorrect" || (outcome == "NoResponse" && !trial.autoReward);
        var row = new TrialRow
        {
            trialNumber = trial.trialNumber,
            trialType = trial.trialTypeName,
            target = trial.targetSide,
            choice = choice,
            outcome = outcome,
            stimulusOnsetTime = value.Item2.Item2,
            choiceTime = choice == "NoResponse" ? double.NaN : value.Item1.Seconds,
            rewardSizeUl = trial.RewardSizeInUl,
            bothSpouts = trial.BothSpouts,
            repeatCount = trial.RepeatCount,
            itiSeconds = previousIti,
            punishSound = outcome == "Incorrect" && trial.punishSoundEnabled,
            punishTimeoutSeconds = timedOut ? trial.IncorrectTimeoutDuration : 0.0,
            visualAzimuth = trial.visualAzimuth,
            visualElevation = trial.visualElevation,
            visualContrast = trial.visualContrast,
            visualPhase = trial.visualPhase,
            audioAzimuth = played ? locations[speaker].X : double.NaN,
            audioElevation = played ? locations[speaker].Y : double.NaN,
            audioWaveformIndex = trial.audioWaveformIdx,
            laserCondition = trial.OptogeneticCondition,
            autoRewarded = trial.autoReward && choice == "NoResponse",
        };
        previousIti = trial.ITI;
        return new Timestamped<TrialRow>(row, value.Item1.Seconds);
    }
}
