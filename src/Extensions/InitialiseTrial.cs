using Bonsai;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using UclOpenAvDomeTask;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

[Combinator]
[Description("Draws the next trial from the task configuration: weighted trial-type selection, repeat bookkeeping and per-trial parameter assignment. All parameters come from the task schema.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class InitialiseTrial
{
    [Description("Training stage id selecting a row of the trainingStages table; ids past the end use the last row.")]
    public int trainingStageId { get; set; }  // This is set by bonsai over the GUI.

    [Description("Set to -1 to use the weighted random selection (recommended). Or force specific trial: 0=UnisensoryVisual, 1=UnisensoryAudio, 2=Multisensory (coherent), 3=Multisensory (conflict), 4=Neutral")]
    public int TrialType { get; set; }

    [Description("Whether the last response was correct. This is used to determine if a trial should be repeated.")]
    public bool WasCorrect { get; set; }

    [Description("Whether the last trial ended in a timeout. This is used to determine if a trial should be repeated.")]
    public bool WasTimeout { get; set; }

    [Description("Offset added to the configured rngSeed so multiple operator instances draw distinct deterministic streams.")]
    public int SeedOffset { get; set; }

    [Description("Trials completed so far this session (map from the TrialCount subject). Trials with TrialNumber < optogenetics.warmupTrials are never stimulated.")]
    public int TrialNumber { get; set; }

    // One RNG per operator instance, seeded once from the config on the first trial so a session
    // is reproducible from its task YAML. Instance state persists across re-subscriptions
    // (e.g. inside a SelectMany), matching the shuffle-block and repeat bookkeeping below.
    private Random trialRand;
    private int rngSeedUsed;

    // Track if we need to shuffle the trial order for true randomization
    private List<int> shuffledTrialSequence;
    private int currentTrialIndex = 0;

    // Trial repetition tracking
    private Trial lastTrial = null;
    private int currentRepeatCount = 0;
    private bool isFirstTrial = true;

    private static List<int> _zapitConditions = new List<int>();
    private static List<int> _conditionQueue = new List<int>();
    private static int _currentConditionIndex = 0;
    private static DateTime _zapitConfigLastLoaded = DateTime.MinValue;

    public IObservable<Trial> Process(IObservable<UclOpenAvDomeTaskLogic> source)
    {
        return source.Select(value =>
        {
            UclOpenAvDomeTaskParameters parameters = value.TaskParameters;
            TrainingStage stage = GetTrainingStage(parameters);

            EnsureRandomInitialised(parameters);
            EnsureConfigChecked(parameters);

            // Check if we need to repeat the last trial
            if (ShouldRepeatTrial(parameters, stage))
            {
                currentRepeatCount++;
                // Return a copy of the same trial with repeat information
                var repeatTrial = lastTrial.Clone();
                repeatTrial.IsRepeat = true;
                repeatTrial.RepeatCount = currentRepeatCount;
                // The copy carries the original trial's optogenetic draw; clear it unless
                // repeats may be stimulated, in which case the same condition is presented again.
                if (parameters.Optogenetics.ExcludeRepeats)
                {
                    ClearOptogeneticParameters(repeatTrial);
                }
                return repeatTrial;
            }

            if (parameters.Optogenetics.Enabled)
            {
                LoadZapitConfigIfNeeded(parameters.Optogenetics.ZapitConfigPath);
            }

            // Generate new trial
            currentRepeatCount = 0;
            isFirstTrial = false;

            var newTrial = GenerateNewTrial(parameters, stage);

            // Keep a record of the last trial for trial repeats.
            lastTrial = newTrial;
            return newTrial;
        });
    }

    private void EnsureRandomInitialised(UclOpenAvDomeTaskParameters parameters)
    {
        if (trialRand != null) return;
        int seed;
        if (parameters.RngSeed.HasValue)
        {
            seed = (int)parameters.RngSeed.Value + SeedOffset;
        }
        else
        {
            seed = Guid.NewGuid().GetHashCode();
        }
        rngSeedUsed = seed;
        trialRand = new Random(seed);
    }

    // Bonsai deserializes the task YAML into the generated types without running the pydantic
    // validators, so the rules the draws depend on are checked here, once, before the first trial.
    private bool configChecked;

    private void EnsureConfigChecked(UclOpenAvDomeTaskParameters parameters)
    {
        if (configChecked) return;
        var visual = parameters.VisualStimulus;
        var azimuths = visual.OverwriteAzimuths;
        if (azimuths != null && azimuths.Count > 0)
        {
            double cap = visual.CoherentAzimuthCap;
            if (azimuths.Any(azimuth => azimuth < -180 || azimuth > 180))
            {
                throw new InvalidOperationException("overwriteAzimuths values must be within [-180, 180].");
            }
            if (!azimuths.Any(azimuth => azimuth > 0 && azimuth <= cap) ||
                !azimuths.Any(azimuth => azimuth < 0 && azimuth >= -cap))
            {
                throw new InvalidOperationException(
                    "overwriteAzimuths needs a value on each side within coherentAzimuthCap (" + cap + ").");
            }
        }
        if (parameters.AudioStimulus.SpeakerLocations.Any(location => location.Azimuth == 0))
        {
            throw new InvalidOperationException(
                "speakerLocations must not have an azimuth-0 entry; the centre sound is fixed in code.");
        }
        configChecked = true;
    }

    private TrainingStage GetTrainingStage(UclOpenAvDomeTaskParameters parameters)
    {
        var stages = parameters.TrainingStages;
        if (stages == null || stages.Count == 0)
        {
            throw new InvalidOperationException("Task configuration has no training stages.");
        }
        int index = trainingStageId;
        if (index < 0) index = 0;
        if (index >= stages.Count) index = stages.Count - 1;
        return stages[index];
    }

    private bool ShouldRepeatTrial(UclOpenAvDomeTaskParameters parameters, TrainingStage stage)
    {
        // Don't repeat if no previous trial, max repeats reached, this is the first trial,
        // or the current stage disables repeats.
        if (lastTrial == null ||
            currentRepeatCount >= parameters.Repeats.MaxRepeats ||
            isFirstTrial ||
            !stage.AllowRepeats)
            return false;

        // Check the repetition rule configured for the last trial's type
        var rules = parameters.Repeats.RulesPerType;
        if (rules == null || lastTrial.trialType >= rules.Count)
            return false;

        switch (rules[lastTrial.trialType])
        {
            case RepeatRule.OnFailure:
                // Repeat if incorrect OR timeout
                return !WasCorrect || WasTimeout;
            case RepeatRule.OnTimeout:
                // Only repeat for timeout (not for unrewarded choice)
                return WasTimeout;
            default:
                return false;
        }
    }

    private Trial GenerateNewTrial(UclOpenAvDomeTaskParameters parameters, TrainingStage stage)
    {
        var trialData = new Trial();
        trialData.rngSeedUsed = rngSeedUsed;

        // // Define general trial parameters
        // Variables defined by training-stage
        trialData.autoRewardProbability = stage.AutoRewardProbability;  // probability of automatically rewarding the trial (for early training stages)
        trialData.autoReward = drawRandomBasedOnProbability(stage.AutoRewardProbability, trialRand);  // This defines whether the current trial is automatically rewarded
        trialData.RewardSizeInUl = stage.RewardSizeUl;  // Size of rewards in ul.

        trialData.BothSpoutsProbability = stage.BothSpoutsProbability;
        trialData.BothSpouts = drawRandomBasedOnProbability(trialData.BothSpoutsProbability, trialRand);  // This defines whether the current trial is a BothSpouts Trial.

        // If largeReward trial, overwrite the reward size for this trial.
        trialData.LargeRewardMultiplier = parameters.Reward.LargeRewardMultiplier;
        trialData.LargeRewardProbability = parameters.Reward.LargeRewardProbability;
        trialData.largeReward = drawRandomBasedOnProbability(trialData.LargeRewardProbability, trialRand);  // This defines whether the current trial is a largeReward Trial.
        trialData.RewardSizeInUl = trialData.largeReward ? stage.RewardSizeUl * trialData.LargeRewardMultiplier : stage.RewardSizeUl;

        // Trial structure timings
        trialData.ITI = drawRandomUniformFromRange(parameters.TrialStructure.ItiMin, parameters.TrialStructure.ItiMax, trialRand);
        trialData.Quiescent = drawRandomUniformFromRange(parameters.TrialStructure.QuiescentMin, parameters.TrialStructure.QuiescentMax, trialRand);
        trialData.SpoutINDelay = parameters.TrialStructure.SpoutInDelay;
        trialData.IncorrectTimeoutDuration = parameters.TrialStructure.IncorrectTimeoutDuration;
        trialData.openLoopDuration = parameters.TrialStructure.OpenLoopDuration;
        trialData.closedLoopDuration = parameters.TrialStructure.ClosedLoopDuration;

        // Stimulus parameters common to all trial types; positions are drawn per trial in the
        // setup methods (visual from overwriteAzimuths or the configured region, audio from
        // speakerLocations or the fixed centre)
        trialData.visualGaussianWindowSigma = parameters.VisualStimulus.GaussianWindowSigma;
        trialData.visualOrientation = parameters.VisualStimulus.Orientation;
        trialData.visualSpatialFrequency = parameters.VisualStimulus.SpatialFrequency;
        trialData.visualTemporalFrequency = parameters.VisualStimulus.TemporalFrequency;
        // One task-level pulse rate: the grating gates at it and the audio wav is generated to match
        trialData.visualFlickerFrequency = parameters.FlickerFrequency;
        trialData.audioFlickerFrequency = parameters.FlickerFrequency;

        // define random phase of grating:
        trialData.visualPhase = 2 * Math.PI * trialRand.NextDouble();

        // Option to externally define trial type for testing. Set to -1 for regular operation!!!
        // Determine the trial type (set to above 4 if you want the random number)
        int selectedTrialType = (TrialType >= 0 && TrialType <= 4)
            ? TrialType
            : GetWeightedRandomTrialType(stage, trialRand);

        switch (selectedTrialType)
        {
            case 0: // Unisensory Visual
                SetupUnisensoryVisualTrial(trialData, parameters, stage, trialRand);
                break;
            case 1: // Unisensory Audio
                SetupUnisensoryAudioTrial(trialData, parameters, stage, trialRand);
                break;
            case 2: // Multisensory Coherent
                SetupMultisensoryCoherentTrial(trialData, parameters, stage, trialRand);
                break;
            case 3: // Multisensory Conflict
                SetupMultisensoryConflictTrial(trialData, parameters, stage, trialRand);
                break;
            case 4: // Neutral
                SetupNeutralTrial(trialData, parameters, stage, trialRand);
                break;
        }

        // General Trial Settings
        trialData.trialType = selectedTrialType;
        trialData.IsRepeat = false;
        trialData.RepeatCount = 0;
        SetupOptogeneticParameters(trialData, parameters, trialRand);

        return trialData;
    }

    // Gets a random trial type based on the weighted distribution
    // Now using the Fisher-Yates shuffle to ensure balanced distribution
    private int GetWeightedRandomTrialType(TrainingStage stage, Random random)
    {
        // If we've run through all trials in the block or haven't initialized yet,
        // generate a new shuffled sequence ensuring proper proportions
        if (shuffledTrialSequence == null || currentTrialIndex >= shuffledTrialSequence.Count)
        {
            // Generate a list with trial types in the correct proportion
            shuffledTrialSequence = new List<int>();

            // Add trial types according to their weights
            var trialWeights = stage.TrialWeights;
            for (int type = 0; type < trialWeights.Count; type++)
            {
                for (int count = 0; count < trialWeights[type]; count++)
                {
                    shuffledTrialSequence.Add(type);
                }
            }

            // Shuffle the sequence using Fisher-Yates algorithm
            int n = shuffledTrialSequence.Count;
            while (n > 1)
            {
                n--;
                int k = random.Next(n + 1);
                int temp = shuffledTrialSequence[k];
                shuffledTrialSequence[k] = shuffledTrialSequence[n];
                shuffledTrialSequence[n] = temp;
            }

            currentTrialIndex = 0;
        }

        // Get the next trial type from the shuffled sequence
        return shuffledTrialSequence[currentTrialIndex++];
    }

    private void LoadZapitConfigIfNeeded(string configPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(configPath) &&
                File.Exists(configPath) &&
                (DateTime.Now - _zapitConfigLastLoaded).TotalMinutes > 5)
            {
                LoadZapitConditions(configPath);
                _zapitConfigLastLoaded = DateTime.Now;
                Console.WriteLine("Loaded Zapit config with " + _zapitConditions.Count + " stimulation conditions");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Warning: Error loading Zapit configuration: " + ex.Message);
            _zapitConditions.Clear();
        }
    }

    private void LoadZapitConditions(string configPath)
    {
        var yaml = File.ReadAllText(configPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var yamlData = deserializer.Deserialize<Dictionary<string, object>>(yaml);
        _zapitConditions.Clear();

        foreach (var kvp in yamlData)
        {
            if (kvp.Key.StartsWith("stimLocations"))
            {
                var numberStr = kvp.Key.Substring("stimLocations".Length);
                int conditionNumber;  // Declare separately
                if (int.TryParse(numberStr, out conditionNumber))
                {
                    _zapitConditions.Add(conditionNumber);
                }
            }
        }

        _zapitConditions.Sort();
    }

    private void SetupOptogeneticParameters(Trial trialData, UclOpenAvDomeTaskParameters parameters, Random rand)
    {
        // Default: no optogenetic stimulation
        ClearOptogeneticParameters(trialData);

        // Check if optogenetics is enabled
        var optogenetics = parameters.Optogenetics;
        if (!optogenetics.Enabled)
            return;

        if (TrialNumber < optogenetics.WarmupTrials)
        {
            Console.WriteLine("Optogenetics warmup: trial " + (TrialNumber + 1) + "/" + optogenetics.WarmupTrials + " - no stimulation");
            return;
        }
        // Check if Zapit conditions are available
        if (_zapitConditions.Count == 0)
        {
            Console.WriteLine("Warning: Optogenetics enabled but no Zapit conditions available");
            return;
        }

        // Roll for stimulation
        if (rand.NextDouble() < optogenetics.StimProbability)
        {
            trialData.OptogeneticEnabled = true;

            // Pseudorandom condition selection
            if (optogenetics.PseudorandomSelection)
            {
                trialData.OptogeneticCondition = GetPseudorandomCondition(rand);
            }
            else
            {
                trialData.OptogeneticCondition = _zapitConditions[rand.Next(_zapitConditions.Count)];
            }

            // Set timing parameters from config
            trialData.OptogeneticDelay = optogenetics.StimDelay;
            trialData.OptogeneticDuration = optogenetics.StimDuration;
            trialData.OptogeneticPower = 0; // Will be set by Zapit config
        }
    }

    private static void ClearOptogeneticParameters(Trial trialData)
    {
        trialData.OptogeneticEnabled = false;
        trialData.OptogeneticCondition = 0;
        trialData.OptogeneticDelay = 0;
        trialData.OptogeneticDuration = 0;
        trialData.OptogeneticPower = 0;
    }

    private int GetPseudorandomCondition(Random rand)
    {
        // Initialize or refill the condition queue when empty
        if (_conditionQueue.Count == 0)
        {
            _conditionQueue.AddRange(_zapitConditions);

            // Shuffle using Fisher-Yates
            int n = _conditionQueue.Count;
            while (n > 1)
            {
                n--;
                int k = rand.Next(n + 1);
                int temp = _conditionQueue[k];
                _conditionQueue[k] = _conditionQueue[n];
                _conditionQueue[n] = temp;
            }

            _currentConditionIndex = 0;
        }

        // Get next condition from shuffled queue
        int selectedCondition = _conditionQueue[_currentConditionIndex];
        _currentConditionIndex++;

        // Clear queue when exhausted
        if (_currentConditionIndex >= _conditionQueue.Count)
        {
            _conditionQueue.Clear();
        }

        return selectedCondition;
    }

    private static List<double> NonZero(List<double> values)
    {
        var result = new List<double>();
        foreach (var item in values)
        {
            if (item > 0) result.Add(item);
        }
        return result;
    }

    // Draws a non-zero contrast from the stage's contrast set.
    private static double DrawContrast(TrainingStage stage, Random rand)
    {
        var nonZeroContrasts = NonZero(stage.Contrasts);
        if (nonZeroContrasts.Count > 0)
        {
            return nonZeroContrasts[rand.Next(nonZeroContrasts.Count)];
        }
        return 0.2; // Default value if no non-zero contrasts
    }

    // Draws a visual position from the configured dome region and writes it to the trial.
    // With overwriteAzimuths set, the azimuth is drawn from that list instead (see
    // DrawOverwriteAzimuth). Otherwise it is drawn uniformly; when side restriction is requested,
    // from the side's subrange, capped at coherentAzimuthCap, and an empty subrange falls back
    // to +-60 with a console warning.
    private void DrawVisualPosition(Trial trial, VisualStimulus visual, Random rand, bool restrictSide, bool isRightSide, bool applyCap)
    {
        if (visual.OverwriteAzimuths != null && visual.OverwriteAzimuths.Count > 0)
        {
            trial.visualAzimuth = DrawOverwriteAzimuth(visual, rand, restrictSide, isRightSide, applyCap);
            trial.visualElevation = drawRandomUniformFromRange(visual.ElevationMin, visual.ElevationMax, rand);
            return;
        }

        double azimuthMin = visual.AzimuthMin;
        double azimuthMax = visual.AzimuthMax;
        if (restrictSide)
        {
            double cap = applyCap ? visual.CoherentAzimuthCap : 180.0;
            if (isRightSide)
            {
                azimuthMin = Math.Max(azimuthMin, 0.0);
                azimuthMax = Math.Min(azimuthMax, cap);
            }
            else
            {
                azimuthMin = Math.Max(azimuthMin, -cap);
                azimuthMax = Math.Min(azimuthMax, 0.0);
            }
            if (azimuthMax <= azimuthMin)
            {
                Console.WriteLine("Warning: no visual azimuth range on the requested side; falling back to +-60");
                trial.visualAzimuth = isRightSide ? 60.0 : -60.0;
                trial.visualElevation = drawRandomUniformFromRange(visual.ElevationMin, visual.ElevationMax, rand);
                return;
            }
        }
        // GERION EDIT: HACKY FIX FOR BROKEN VISUAL STIMULI
        // trial.visualAzimuth = drawRandomUniformFromRange(azimuthMin, azimuthMax, rand);
        // trial.visualElevation = drawRandomUniformFromRange(visual.ElevationMin, visual.ElevationMax, rand);
        // TODO:
        trial.visualAzimuth = isRightSide ? 60.0 : -60.0;
        trial.visualElevation = 15.0;
    }

    // Draws one azimuth from overwriteAzimuths, filtered to the requested side (strictly positive or
    // negative) and, with applyCap, to |azimuth| <= coherentAzimuthCap. EnsureConfigChecked refuses
    // a list without a usable value on each side, so the throw is a backstop.
    private double DrawOverwriteAzimuth(VisualStimulus visual, Random rand, bool restrictSide, bool isRightSide, bool applyCap)
    {
        var candidates = new List<double>();
        foreach (var azimuth in visual.OverwriteAzimuths)
        {
            if (restrictSide)
            {
                if (isRightSide ? azimuth <= 0 : azimuth >= 0) continue;
                if (applyCap && Math.Abs(azimuth) > visual.CoherentAzimuthCap) continue;
            }
            candidates.Add(azimuth);
        }
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(string.Format(
                "overwriteAzimuths has no value on the {0} side{1}.",
                isRightSide ? "right" : "left",
                applyCap ? " within coherentAzimuthCap" : string.Empty));
        }
        return candidates[rand.Next(candidates.Count)];
    }

    // Picks one speaker location on the requested side and writes it to the trial.
    // No matching side falls back to +-60 at elevation 15 with a console warning.
    private void PickAudioLocationOnSide(Trial trial, AudioStimulus audio, Random rand, bool isRightSide)
    {
        var candidates = new List<SphericalPosition>();
        foreach (var location in audio.SpeakerLocations)
        {
            if ((isRightSide && location.Azimuth > 0) || (!isRightSide && location.Azimuth < 0))
            {
                candidates.Add(location);
            }
        }
        if (candidates.Count == 0)
        {
            Console.WriteLine("Warning: no speaker location on the requested side; falling back to +-60, elevation 15");
            trial.audioAzimuth = isRightSide ? 60.0 : -60.0;
            trial.audioElevation = 15.0;
            return;
        }
        var picked = candidates[rand.Next(candidates.Count)];
        trial.audioAzimuth = picked.Azimuth;
        trial.audioElevation = picked.Elevation;
    }

    // The centre sound of visual-only and neutral trials, fixed in code as in ActiveExperiments:
    // speakerLocations holds lateral positions only, so unisensory audio trials never play from
    // the centre. Playback maps the azimuth to the nearest wired speaker, the dome's centre one.
    private const double CentreAudioAzimuth = 0.0;
    private const double CentreAudioElevation = 15.0;

    private static void SetCentreAudioLocation(Trial trial)
    {
        trial.audioAzimuth = CentreAudioAzimuth;
        trial.audioElevation = CentreAudioElevation;
    }

    private void SetupUnisensoryVisualTrial(Trial trial, UclOpenAvDomeTaskParameters parameters, TrainingStage stage, Random rand)
    {
        // For unisensory visual: visual stimulus with non-zero contrast anywhere in the configured
        // region, auditory stimulus at the centre

        DrawVisualPosition(trial, parameters.VisualStimulus, rand, false, false, false);

        // Set non-zero contrast
        trial.visualContrast = DrawContrast(stage, rand);

        if (trial.visualAzimuth > 0)
        {
            trial.targetSide = "Right";
        }
        else if (trial.visualAzimuth < 0)
        {
            trial.targetSide = "Left";
        }
        else
        {
            trial.targetSide = rand.NextDouble() >= 0.5 ? "Right" : "Left";
        }

        // Set auditory stimulus to the centre speaker location
        SetCentreAudioLocation(trial);

        trial.RewardProbability = parameters.Reward.RewardProbability;
        trial.IsRewarded = drawRandomBasedOnProbability(trial.RewardProbability, rand);
    }

    private void SetupUnisensoryAudioTrial(Trial trial, UclOpenAvDomeTaskParameters parameters, TrainingStage stage, Random rand)
    {
        // For unisensory audio: zero contrast (gray screen), auditory stimulus drawn from the
        // configured speaker locations

        // Set visual contrast to zero; grating position is irrelevant on a gray screen
        trial.visualContrast = 0;
        trial.visualAzimuth = 0;
        trial.visualElevation = 0;

        var locations = parameters.AudioStimulus.SpeakerLocations;
        var picked = locations[rand.Next(locations.Count)];
        trial.audioAzimuth = picked.Azimuth;
        trial.audioElevation = picked.Elevation;

        if (trial.audioAzimuth > 0)
        {
            trial.targetSide = "Right";
        }
        else if (trial.audioAzimuth < 0)
        {
            trial.targetSide = "Left";
        }
        else
        {
            trial.targetSide = rand.NextDouble() >= 0.5 ? "Right" : "Left";
        }

        trial.RewardProbability = parameters.Reward.RewardProbability;
        trial.IsRewarded = drawRandomBasedOnProbability(trial.RewardProbability, rand);
    }

    private void SetupMultisensoryCoherentTrial(Trial trial, UclOpenAvDomeTaskParameters parameters, TrainingStage stage, Random rand)
    {
        // For coherent trials: visual and auditory stimuli on same side

        // First, pick a side (left or right) for both stimuli
        bool isRightSide = rand.NextDouble() >= 0.5;
        trial.targetSide = isRightSide ? "Right" : "Left";

        // Draw visual position on the chosen side, capped at coherentAzimuthCap
        DrawVisualPosition(trial, parameters.VisualStimulus, rand, true, isRightSide, true);

        // Set non-zero contrast
        trial.visualContrast = DrawContrast(stage, rand);

        // Pick a speaker location on the same side
        PickAudioLocationOnSide(trial, parameters.AudioStimulus, rand, isRightSide);

        trial.RewardProbability = parameters.Reward.RewardProbability;
        trial.IsRewarded = drawRandomBasedOnProbability(trial.RewardProbability, rand);
    }

    private void SetupMultisensoryConflictTrial(Trial trial, UclOpenAvDomeTaskParameters parameters, TrainingStage stage, Random rand)
    {
        // For conflict trials: visual and auditory stimuli on opposite sides

        // First, pick a side for visual stimulus (opposite for audio)
        bool isVisualRight = rand.NextDouble() >= 0.5;
        trial.targetSide = isVisualRight ? "Right" : "Left";

        // Draw visual position on the chosen side (no cap on conflict trials, as before)
        DrawVisualPosition(trial, parameters.VisualStimulus, rand, true, isVisualRight, false);

        // Set non-zero contrast
        trial.visualContrast = DrawContrast(stage, rand);

        // Pick a speaker location on the OPPOSITE side from visual
        PickAudioLocationOnSide(trial, parameters.AudioStimulus, rand, !isVisualRight);

        // Check this again, but the 50% should come from the random targetSide, no additional random, right?!
        trial.RewardProbability = parameters.Reward.RewardProbability;
        trial.IsRewarded = drawRandomBasedOnProbability(trial.RewardProbability, rand);
    }

    private void SetupNeutralTrial(Trial trial, UclOpenAvDomeTaskParameters parameters, TrainingStage stage, Random rand)
    {
        // For neutral trials: zero contrast (gray screen), auditory stimulus at the centre

        trial.targetSide = rand.NextDouble() >= 0.5 ? "Right" : "Left";

        // Gray screen: grating position irrelevant
        trial.visualAzimuth = 0;
        trial.visualElevation = 0;
        trial.visualContrast = 0;

        // Set auditory stimulus to the centre speaker location
        SetCentreAudioLocation(trial);

        // Check this again, but the 50% should come from the random targetSide, no additional random, right?!
        trial.RewardProbability = parameters.Reward.RewardProbability;
        trial.IsRewarded = drawRandomBasedOnProbability(trial.RewardProbability, rand);
    }

    // handy function with overloads
    double drawRandomUniformFromRange(double lowerBound, double upperBound, Random trialRand)
    {
        return lowerBound + (double)(trialRand.NextDouble() * (upperBound - lowerBound));
    }

    // Now also draw random from probability
    bool drawRandomBasedOnProbability(double probability, Random trialRand)
    {
        // draw binary true falls on the given probability
        return trialRand.NextDouble() < probability;
    }

}

public class Trial
{
    // Provenance
    public int rngSeedUsed;  // effective RNG seed for this session's draw stream (config rngSeed + offset, or random)

    // TrialStructure
    public int trialType;
    public double RewardProbability;  // Probability of reward delivery for this trial (used for random reward assignment in conflict and neutral trials)
    public bool IsRewarded;
    // Trial repetition tracking
    public bool IsRepeat;
    public int RepeatCount;
    public string targetSide;
    public double ITI;
    public double Quiescent;
    public double SpoutINDelay;  // in seconds. Relative to stimulus onset when to move response spouts IN
    public double IncorrectTimeoutDuration;  // in seconds
    public double autoRewardProbability;  // probability of automatically rewarding the trial (for early training stages)
    public bool autoReward;  // Bool defining if current trial is autoRewarded or not
    public bool largeReward;  // Bool defining if current trial is autoRewarded or not
    public double RewardSizeInUl;  // Size of rewards in ul.
    public double LargeRewardMultiplier;  // Reward size multiplier on large-reward trials.
    public double LargeRewardProbability;  // Probability of receiving a large reward
    public bool BothSpouts;  // Bool defining if current trial is presenting both spouts (regular trial) or just the correct spout (used for initial training).
    public double BothSpoutsProbability { get; set; }  // by default only regular trials.

    public double openLoopDuration;
    public double closedLoopDuration;

    // VisualStimulus
    public double visualAzimuth;
    public double visualElevation;
    public double visualGaussianWindowSigma;  // in deg
    public double visualContrast;
    public double visualOrientation;
    public double visualSpatialFrequency;
    public double visualTemporalFrequency;
    public double visualFlickerFrequency;  // in Hz
    public double visualPhase;

    // AuditoryStimulus
    public double audioAzimuth;
    public double audioElevation;
    public double audioFlickerFrequency;  // in Hz

    // Optogenetics
    public bool OptogeneticEnabled;
    public int OptogeneticCondition;
    public double OptogeneticDelay;
    public double OptogeneticDuration;
    public double OptogeneticPower;

    public Trial Clone()
    {
        return (Trial)MemberwiseClone();
    }
}
