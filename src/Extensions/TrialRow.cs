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
    public int trialType;
    public string target;
    public string choice;
    public string outcome;
    public double stimulusOnsetTime;  // Harp time the stimulus was commanded on; NaN if it never was
    public double choiceTime;  // Harp time of the lick that made the choice; NaN on NoResponse
    public double rewardSizeUl;
    public bool bothSpouts;
    public bool largeReward;
    public int repeatCount;
    public double itiSeconds;
    public double visualAzimuth;
    public double visualElevation;
    public double visualContrast;
    public double visualPhase;
    public double audioAzimuth;  // the speaker that played, after snapping to the nearest wired one
    public double audioElevation;
    public int audioWaveformIndex;
    public bool optoStimulated;
    public int optoCondition;
    public string taskType;
    public string targetModality;
    public bool autoRewarded;  // a miss on an auto-reward trial, rewarded at the target spout
}

[Combinator]
[Description("Builds the trial's BonsaiData_Trial row when its choice is made. Input: the choice (Left, Right or NoResponse) stamped with the Harp time it was made, with the trial as drawn, the Harp time of its stimulus onset (NaN if none), the index of the speaker that played (-1 if none) and the speaker locations.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CreateTrialRow
{
    public IObservable<Timestamped<TrialRow>> Process(IObservable<Tuple<Timestamped<string>, Tuple<Trial, double, int, Vector3[]>>> source)
    {
        return source.Select(value =>
        {
            var choice = value.Item1.Value;
            var trial = value.Item2.Item1;
            var speaker = value.Item2.Item3;
            var locations = value.Item2.Item4;
            bool played = trial.audioEnabled && locations != null && speaker >= 0 && speaker < locations.Length;
            var row = new TrialRow
            {
                trialNumber = trial.trialNumber,
                trialType = trial.trialType,
                target = trial.targetSide,
                choice = choice,
                outcome = choice == "NoResponse" ? "NoResponse" : choice == trial.targetSide ? "Correct" : "Incorrect",
                stimulusOnsetTime = value.Item2.Item2,
                choiceTime = choice == "NoResponse" ? double.NaN : value.Item1.Seconds,
                rewardSizeUl = trial.RewardSizeInUl,
                bothSpouts = trial.BothSpouts,
                largeReward = trial.largeReward,
                repeatCount = trial.RepeatCount,
                itiSeconds = trial.ITI,
                visualAzimuth = trial.visualAzimuth,
                visualElevation = trial.visualElevation,
                visualContrast = trial.visualContrast,
                visualPhase = trial.visualPhase,
                audioAzimuth = played ? locations[speaker].X : double.NaN,
                audioElevation = played ? locations[speaker].Y : double.NaN,
                audioWaveformIndex = trial.audioWaveformIdx,
                optoStimulated = trial.OptogeneticEnabled,
                optoCondition = trial.OptogeneticCondition,
                taskType = trial.taskType,
                targetModality = trial.targetModality,
                autoRewarded = trial.autoReward && choice == "NoResponse",
            };
            return new Timestamped<TrialRow>(row, value.Item1.Seconds);
        });
    }
}
