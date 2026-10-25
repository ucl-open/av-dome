using Bonsai;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using DataSchema;
using System.Dynamic;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

[Combinator]
[Description("Initializes trial data with a ratio of 10/10/5/5/1 for unisensory audio/visual/multisensory coherent/conflict/neutral")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class TrialTypeInitialise
{
    public int Seed { get; set; }
    [Description("Set to -1 to use the weighted random selection (recommended). Or force specific trial: 0=UnisensoryVisual, 1=UnisensoryAudio, 2=Multisensory (coherent), 3=Multisensory (conflict), 4=Neutral")]
    public int trainingStageId { get; set; }  // This is set by bonsai over the GUI.
    public int TrialType { get; set; }
    // public String mouseID { get; set; }
    
    // Response handling properties
    // Currently unused:
    // [Description("Last response from subject: -1 (Left), 1 (Right), 0 (Timeout/No response)")]
    // public float LastResponse { get; set; }
    
    [Description("Whether the last response was correct. This is used to determine if a trial should be repeated.")]
    public bool WasCorrect { get; set; }

    [Description("Whether the last trial ended in a timeout. This is used to determine if a trial should be repeated.")]
    public bool WasTimeout { get; set; }

    // Speaker calibration data
    private static readonly Dictionary<string, int> SpeakerCalibration = new Dictionary<string, int>
    {
        // Layer 1 (Elevation = 75°)
        { "0,75", 1 },
        { "90,75", 2 },
        { "-90,75", 3 },
        
        // Layer 2 (Elevation = 45°)
        { "22.5,45", 4 },
        { "67.5,45", 5 },
        { "112.5,45", 6 },
        { "-22.5,45", 7 },
        { "-67.5,45", 8 },
        { "-112.5,45", 9 },
        
        // Layer 3 (Elevation = 15°)
        { "30,15", 10 },
        { "60,15", 11 },
        { "90,15", 12 },
        { "120,15", 13 },
        { "-30,15", 14 },
        { "-60,15", 15 },
        { "-90,15", 16 },
        { "-120,15", 17 },
        
        // Layer 4 (Elevation = -15°)
        { "30,-15", 18 },
        { "60,-15", 19 },
        { "90,-15", 20 },
        { "120,-15", 21 },
        { "-30,-15", 22 },
        { "-60,-15", 23 },
        { "-90,-15", 24 },
        { "-120,-15", 25 }
    };

    // We'll create a new Random instance for each trial
    
    private List<int> trialWeights;
    public double autoRewardProbability;  // probability of automatically rewarding the trial (for early training stages)
    public double BothSpoutsProbability = 1.0;  // by default only regular trials. 
    public double RewardSizeInUl;  // Size of rewards in ul.

    // Track if we need to shuffle the trial order for true randomization
    private List<int> shuffledTrialSequence;
    private int currentTrialIndex = 0;
    
    // Trial repetition tracking
    private TrialData lastTrialData = null;
    private int currentRepeatCount = 0;
    private const int MAX_REPEATS = 5;
    private bool isFirstTrial = true;

    private static List<int> _zapitConditions = new List<int>();
    private static List<int> _conditionQueue = new List<int>();
    private static int _currentConditionIndex = 0;
    private static DateTime _zapitConfigLastLoaded = DateTime.MinValue;

    public IObservable<TrialData> Process(IObservable<AvTask> source)
    {
        // Set up the weighted distribution
        // Ratio of 10/10/5/5/1 for trial types 0,1,2,3,4
        // List<int> trialWeights = new List<int> { 10, 10, 5, 5, 1 };
        // int totalWeight = trialWeights.Sum();

        return source.Select(value =>
        {

            // Check if we need to repeat the last trial
            if (ShouldRepeatTrial())
            {
                currentRepeatCount++;
                // Return the same trial with repeat information
                var repeatTrial = CloneTrial(lastTrialData);
                repeatTrial.IsRepeat = true;
                repeatTrial.RepeatCount = currentRepeatCount;
                return repeatTrial;  // Note no optogenetics in repeat trials!
            }

            if (value.Optogenetics != null && value.Optogenetics.Enabled)
            {
                LoadZapitConfigIfNeeded(value.Optogenetics.ZapitConfigPath);
            }

            // Generate new trial
            currentRepeatCount = 0;
            isFirstTrial = false;

            // Depending on training stage, different trialsTypes are presented and autoRewards enabled/disabled. Changes made here, affect GenerateNewTrial()
            value = ApplyTrainingStageRules(value);

            // Determine settings for the upcoming trials
            var newTrial = GenerateNewTrial(value);

            // Keep a records of the last trial for trial cloning.
            lastTrialData = newTrial;
            return newTrial;
        });
    }
    
    private bool ShouldRepeatTrial()
    {
        // Don't repeat if no previous trial or max repeats reached or this is the first trial
        if (lastTrialData == null || currentRepeatCount >= MAX_REPEATS || isFirstTrial || trainingStageId == 0)
            return false;
            
        // Check repetition rules based on trial type
        switch (lastTrialData.trialType)
        {
            case 0: // Unisensory Visual
                // Repeat if incorrect OR timeout
                return !WasCorrect || WasTimeout;
            case 1: // Unisensory Audio  
                // Repeat if incorrect OR timeout
                return !WasCorrect || WasTimeout;
            case 2: // Multisensory Coherent
                // Repeat if incorrect OR timeout
                return !WasCorrect || WasTimeout;
            case 3: // Multisensory Conflict
                // Only repeat for timeout (not for unrewarded choice)
                return WasTimeout;
            case 4: // Neutral
                // Only repeat for timeout
                return WasTimeout;
                
            default:
                return false;
        }
    }
    
    private TrialData CloneTrial(TrialData original) 
    {
        return original;
    }
    
    private AvTask ApplyTrainingStageRules(AvTask value)
    {
        // // Training Stages (5 total):
        // Stage 0 — Habituation - Present coherent trials only but automatically reward animal in 80% of trials. 
        // Stage 1 — Coherent trials only: Mice were trained until they reached ~70% performance with only audiovisual coherent trials.
        // Stage 2 — Unisensory auditory added: Auditory trials were introduced.
        // Stage 3 — Unisensory visual added: Visual trials were introduced.
        // Stage 4 — Conflict trials added: Neutral and conflict trials were introduced based on experimenter discretion.
        switch (trainingStageId)
        {
            case 0: // Habituation phase - Coherent trials only
                trialWeights = new List<int> { 0, 0, 10, 0, 0 };
                autoRewardProbability = 0.0; // Automatically reward 80% of trials (handled in trial generation)
                BothSpoutsProbability = 0.5; // Only regular trials
                RewardSizeInUl = 3; // Currently quite hacky. In bonsai I define the opening duration of the solenoid for 2ul and here, where I set 4ul, I'll make bonsai open the valve twice in a row. This needs to chance sooner rather than later!
                value.GratingsSpecifications.Contrast = new List<double> { 3.0 }; // Set contrast for gratings
                break;
            case 1: // Coherent trials only
                trialWeights = new List<int> { 0, 0, 10, 0, 0 };
                autoRewardProbability = 0.0; // Still automatically reward 20% of trials to help mice transition to new task structure
                BothSpoutsProbability = 1.0; // Only regular trials
                RewardSizeInUl = 3; // Currently quite hacky. In bonsai I define the opening duration of the solenoid for 2ul and here, where I set 4ul, I'll make bonsai open the valve twice in a row. This needs to chance sooner rather than later!
                value.GratingsSpecifications.Contrast = new List<double> { 3.0 }; // Set contrast for gratings
                break;
            case 2: // Unisensory auditory added
                trialWeights = new List<int> { 0, 10, 10, 0, 0 };
                autoRewardProbability = 0.0; // Still automatically reward 10% of trials
                BothSpoutsProbability = 1.0; // Only regular trials
                RewardSizeInUl = 3; // Currently quite hacky. In bonsai I define the opening duration of the solenoid for 2ul and here, where I set 4ul, I'll make bonsai open the valve twice in a row. This needs to chance sooner rather than later!
                value.GratingsSpecifications.Contrast = new List<double> { 0.5, 1.0, 2.0, 3.0 }; // Set contrast for gratings
                break;
            case 3: // Unisensory visual added
                trialWeights = new List<int> { 10, 10, 10, 0, 0 };
                autoRewardProbability = 0.0; // Still automatically reward 10% of trials
                BothSpoutsProbability = 1.0; // Only regular trials
                RewardSizeInUl = 3; // Currently quite hacky. In bonsai I define the opening duration of the solenoid for 2ul and here, where I set 4ul, I'll make bonsai open the valve twice in a row. This needs to chance sooner rather than later!
                value.GratingsSpecifications.Contrast = new List<double> { 0.5, 1.0, 2.0, 3.0 }; // Set contrast for gratings
                break;
            case 4: // Conflict and Neutral trials added
                trialWeights = new List<int> { 10, 10, 5, 5, 1 };
                autoRewardProbability = 0.0;  // No more automatic rewards in final stage
                BothSpoutsProbability = 1.0; // Only regular trials
                RewardSizeInUl = 3; // Currently quite hacky. In bonsai I define the opening duration of the solenoid for 2ul and here, where I set 4ul, I'll make bonsai open the valve twice in a row. This needs to chance sooner rather than later!
                value.GratingsSpecifications.Contrast = new List<double> { 0.5, 1.0, 2.0, 3.0 }; // Set contrast for gratings
                break;
            default:
                trialWeights = new List<int> { 10, 10, 5, 5, 1 };
                autoRewardProbability = 0.0;
                BothSpoutsProbability = 1.0; // Only regular trials
                RewardSizeInUl = 3; // Currently quite hacky. In bonsai I define the opening duration of the solenoid for 2ul and here, where I set 4ul, I'll make bonsai open the valve twice in a row. This needs to chance sooner rather than later!
                value.GratingsSpecifications.Contrast = new List<double> { 0.5, 1.0, 2.0, 3.0 }; // Set contrast for gratings
                break;
        }
        return value;
    }

    private TrialData GenerateNewTrial(AvTask value)
    {
        // Create a new random instance for each trial to ensure different sequences
        // Use a unique seed based on the configured Seed and current time
        // Cant set null in Bonsai, so enter -1 to get a random Seed.
        // int uniqueSeed = Seed;
        // if (Seed == null || Seed == -1)
        // {
        //     int uniqueSeed = (int)Guid.NewGuid().GetHashCode();
        // }
        // int uniqueSeed = Seed != null ? Seed : (int)Guid.NewGuid().GetHashCode();
        int uniqueSeed = Seed != -1 ? Seed : (int)Guid.NewGuid().GetHashCode();
        Random trialRand = new Random(uniqueSeed);
        
        var trialData = new TrialData();

        // // Define general trial parameters
        // Variables defined by training-stage
        trialData.autoRewardProbability = autoRewardProbability;  // probability of automatically rewarding the trial (for early training stages)
        trialData.autoReward = drawRandomBasedOnProbability(autoRewardProbability, trialRand);  // This defines whether the current trial is automatically rewarded
        trialData.RewardSizeInUl = RewardSizeInUl;  // Size of rewards in ul.

        trialData.BothSpoutsProbability = BothSpoutsProbability;
        trialData.BothSpouts = drawRandomBasedOnProbability(trialData.BothSpoutsProbability, trialRand);  // This defines whether the current trial is a BothSpouts Trial.

       // If largeReward trial, overwrite the reward size for this trial.
        trialData.largeReward = drawRandomBasedOnProbability(trialData.LargeRewardProbability, trialRand);  // This defines whether the current trial is a largeReward Trial.
        trialData.RewardSizeInUl = trialData.largeReward ? RewardSizeInUl * trialData.LargeRewardMultiplier : RewardSizeInUl;

        trialData.audioWaveformIdx = (int)Math.Round(99 * trialRand.NextDouble());
        trialData.ITI = drawRandomUniformFromRange(value.TrialStructure.Iti, trialRand);
        // trialData.Quiescent = drawRandomUniformFromRange(value.TrialStructure.Quiescent, trialRand);

        // define random phase of grating:
        trialData.visualPhase = 2 * Math.PI * trialRand.NextDouble();


        // Option to externally define trial type for testing. Set to -1 for regular operation!!!
        // Determine the trial type (set to above 4 if you want the random number)
        int selectedTrialType = (TrialType >= 0 && TrialType <= 4) 
            ? TrialType 
            : GetWeightedRandomTrialType(trialData, value, trialRand);

        switch (selectedTrialType)
        {
            case 0: // Unisensory Visual
                SetupUnisensoryVisualTrial(trialData, value, trialRand);
                break;
            case 1: // Unisensory Audio  
                SetupUnisensoryAudioTrial(trialData, value, trialRand);
                break;
            case 2: // Multisensory Coherent
                SetupMultisensoryCoherentTrial(trialData, value, trialRand);
                break;
            case 3: // Multisensory Conflict
                SetupMultisensoryConflictTrial(trialData, value, trialRand);
                break;
            case 4: // Neutral
                SetupNeutralTrial(trialData, value, trialRand);
                break;
        }

        // General Trial Settings
        trialData.trialType = selectedTrialType;
        trialData.IsRepeat = false;
        trialData.RepeatCount = 0;
        SetupOptogeneticParameters(trialData, value, trialRand);

        // this seems like a good spot to individualize mice for testing purposes:

        // trialData.licksMoveGrating = 0;
        // trialData.licksToTarget = 1;
        // var list = new List<string>() { "AM009", "AM018", "AM020", "Dummy" };
        // if (list.Contains(mouseID)) {
        //     trialData.licksMoveGrating = 1;
        //     trialData.licksToTarget = 4;
        // }

        return trialData;
    }
    
    // Gets a random trial type based on the weighted distribution
    // Now using the Fisher-Yates shuffle to ensure balanced distribution
    private int GetWeightedRandomTrialType(TrialData trialData, AvTask config, Random random)
    {
        // trialWeights: now defined by ApplyTrainingStageRules() at GenerateNewTrial() and stored in the class variable.

        // If we've run through all trials in the block or haven't initialized yet,
        // generate a new shuffled sequence ensuring proper proportions
        if (shuffledTrialSequence == null || currentTrialIndex >= shuffledTrialSequence.Count)
        {
            // Generate a list with trial types in the correct proportion
            shuffledTrialSequence = new List<int>();
            
            // Add trial types according to their weights
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

    private void SetupOptogeneticParameters(TrialData trialData, AvTask config, Random rand)
    {
        // Default: no optogenetic stimulation
        trialData.OptogeneticEnabled = false;
        trialData.OptogeneticCondition = 0;
        trialData.OptogeneticDelay = 0;
        trialData.OptogeneticDuration = 0;
        trialData.OptogeneticPower = 0;

        // Check if optogenetics is enabled
        if (config.Optogenetics == null || !config.Optogenetics.Enabled)
            return;

        // Check if Zapit conditions are available
        if (_zapitConditions.Count == 0)
        {
            Console.WriteLine("Warning: Optogenetics enabled but no Zapit conditions available");
            return;
        }

        // Exclude stimulation on repeat trials if configured
        if (config.Optogenetics.ExcludeRepeats && trialData.IsRepeat)
            return;

        // Roll for stimulation (75% default)
        if (rand.NextDouble() < config.Optogenetics.StimProbability)
        {
            trialData.OptogeneticEnabled = true;
            
            // Pseudorandom condition selection
            if (config.Optogenetics.PseudorandomSelection)
            {
                trialData.OptogeneticCondition = GetPseudorandomCondition(rand);
            }
            else
            {
                trialData.OptogeneticCondition = _zapitConditions[rand.Next(_zapitConditions.Count)];
            }
            
            // Set timing parameters from config
            trialData.OptogeneticDelay = config.Optogenetics.StimDelay;
            trialData.OptogeneticDuration = config.Optogenetics.StimDuration;
            trialData.OptogeneticPower = 0; // Will be set by Zapit config
        }
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


    private void SetupUnisensoryVisualTrial(TrialData trial, AvTask spec, Random rand)
    {
        // For unisensory visual: visual stimulus with non-zero contrast on left or right,
        // auditory stimulus in center (0°)

        // Set visual azimuth (-60° or +60°)
        if (spec.GratingsSpecifications != null &&
            spec.GratingsSpecifications.Azimuth != null &&
            spec.GratingsSpecifications.Azimuth.Count > 0)
        {
            var index = rand.Next(spec.GratingsSpecifications.Azimuth.Count);
            trial.visualAzimuth = Convert.ToDouble(spec.GratingsSpecifications.Azimuth[index]);
        }

        // Set non-zero contrast
        if (spec.GratingsSpecifications != null &&
            spec.GratingsSpecifications.Contrast != null &&
            spec.GratingsSpecifications.Contrast.Count > 0)
        {
            var nonZeroContrasts = new List<double>();
            foreach (var item in spec.GratingsSpecifications.Contrast)
            {
                double contrast = Convert.ToDouble(item);
                if (contrast > 0) nonZeroContrasts.Add(contrast);
            }

            if (nonZeroContrasts.Count > 0)
            {
                trial.visualContrast = nonZeroContrasts[rand.Next(nonZeroContrasts.Count)];
            }
            else
            {
                trial.visualContrast = 0.2; // Default value if no non-zero contrasts
            }
        }

        // // Calculate locationX from azimuth
        // trial.visualLocationX = Math.Sin(trial.visualAzimuth * Math.PI / 180.0);

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
            // Initially had it as both for Bonsai to handle the random reward assignment. But We might as well do this here already: 
            trial.targetSide = rand.NextDouble() >= 0.5 ? trial.targetSide = "Right" : trial.targetSide = "Left";
        }

        // // Set other visual parameters
        // if (spec.GratingsSpecifications != null)
        // {
        //     trial.visualExtentX = spec.GratingsSpecifications.ExtentX;
        //     trial.visualExtentY = spec.GratingsSpecifications.ExtentY;
        // }

        // Set auditory stimulus to center (0°)
        trial.audioAzimuth = 0;
        // trial.audioElevation = 0;
        // trial.audioLocationX = 0; // Sin(0°) = 0
        // trial.audioSpeakerID = GetClosestSpeaker(trial.audioAzimuth, trial.audioElevation);

        // trial.isVisualActive = true;
        // trial.isAudioActive = true;

        trial.RewardProbability = 1;
        trial.IsRewarded = true;
    }

    private void SetupUnisensoryAudioTrial(TrialData trial, AvTask spec, Random rand)
    {
        // For unisensory audio: zero contrast (gray screen), 
        // auditory stimulus on left, center, or right (-60°, 0°, or +60°)

        // Set visual contrast to zero
        trial.visualContrast = 0;

        // Set other visual parameters
        // if (spec.GratingsSpecifications != null)
        // {
        //     trial.visualExtentX = spec.GratingsSpecifications.ExtentX;
        //     trial.visualExtentY = spec.GratingsSpecifications.ExtentY;
        // }

        // Set auditory azimuth (-60°, 0°, or +60°)
        if (spec.AudioSpecifications != null &&
            spec.AudioSpecifications.Azimuth != null &&
            spec.AudioSpecifications.Azimuth.Count > 0)
        {
            var index = rand.Next(spec.AudioSpecifications.Azimuth.Count);
            trial.audioAzimuth = Convert.ToDouble(spec.AudioSpecifications.Azimuth[index]);
        }

        // Set audio elevation from AudioSpecifications
        // trial.audioElevation = (spec.AudioSpecifications != null) ? spec.AudioSpecifications.Elevation : 0;

        // // Calculate auditory locationX from azimuth
        // trial.audioLocationX = Math.Sin(trial.audioAzimuth * Math.PI / 180.0);

        // Get the speaker ID for this azimuth/elevation combination
        // trial.audioSpeakerID = GetClosestSpeaker(trial.audioAzimuth, trial.audioElevation);

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
            // Initially had it as both for Bonsai to handle the random reward assignment. But We might as well do this here already: 
            // trial.targetSide = "Both";
            trial.targetSide = rand.NextDouble() >= 0.5 ? trial.targetSide = "Right" : trial.targetSide = "Left";
        }

        // Set visual locationX to center (for gray screen)
        // trial.visualLocationX = 0;
        trial.visualAzimuth = 0;

        // trial.isVisualActive = true;
        // trial.isAudioActive = true;
        
        trial.RewardProbability = 1; 
        trial.IsRewarded = true;
    }

    private void SetupMultisensoryCoherentTrial(TrialData trial, AvTask spec, Random rand)
    {
        // For coherent trials: visual and auditory stimuli on same side

        // First, pick a side (left or right) for both stimuli
        bool isRightSide = rand.NextDouble() >= 0.5;

        if (isRightSide)
        {
            trial.targetSide = "Right";
        }
        else
        {
            trial.targetSide = "Left";
        }

        // Set visual parameters
        if (spec.GratingsSpecifications != null)
        {
            // Set visual azimuth based on side
            List<double> visualAzimuths = new List<double>();
            if (spec.GratingsSpecifications.Azimuth != null &&
                spec.GratingsSpecifications.Azimuth.Count > 0)
            {
                foreach (var item in spec.GratingsSpecifications.Azimuth)
                {
                    double azimuth = Convert.ToDouble(item);
                    // Filter for only positive or negative azimuths based on selected side
                    if ((isRightSide && azimuth > 0 && azimuth < 80) || (!isRightSide && azimuth < 0 && azimuth > -80))
                    {
                        visualAzimuths.Add(azimuth);
                    }
                }
            }

            // If we found suitable azimuths, pick one at random
            if (visualAzimuths.Count > 0)
            {
                trial.visualAzimuth = visualAzimuths[rand.Next(visualAzimuths.Count)];
            }
            else
            {
                // Fallback in case no matching azimuths were found
                trial.visualAzimuth = isRightSide ? 60.0 : -60.0;
            }

            // Set non-zero contrast
            if (spec.GratingsSpecifications.Contrast != null &&
                spec.GratingsSpecifications.Contrast.Count > 0)
            {
                var nonZeroContrasts = new List<double>();
                foreach (var item in spec.GratingsSpecifications.Contrast)
                {
                    double contrast = Convert.ToDouble(item);
                    if (contrast > 0) nonZeroContrasts.Add(contrast);
                }

                if (nonZeroContrasts.Count > 0)
                {
                    trial.visualContrast = nonZeroContrasts[rand.Next(nonZeroContrasts.Count)];
                }
                else
                {
                    trial.visualContrast = 0.2; // Default value if no non-zero contrasts
                }
            }

            // trial.visualExtentX = spec.GratingsSpecifications.ExtentX;
            // trial.visualExtentY = spec.GratingsSpecifications.ExtentY;
        }

        // Set audio parameters
        if (spec.AudioSpecifications != null)
        {
            // Set audio azimuth based on the same side
            List<double> audioAzimuths = new List<double>();
            if (spec.AudioSpecifications.Azimuth != null &&
                spec.AudioSpecifications.Azimuth.Count > 0)
            {
                foreach (var item in spec.AudioSpecifications.Azimuth)
                {
                    double azimuth = Convert.ToDouble(item);
                    // Filter for only positive or negative azimuths based on selected side
                    if ((isRightSide && azimuth > 0) || (!isRightSide && azimuth < 0))
                    {
                        audioAzimuths.Add(azimuth);
                    }
                }
            }

            // If we found suitable azimuths, pick one at random
            if (audioAzimuths.Count > 0)
            {
                trial.audioAzimuth = audioAzimuths[rand.Next(audioAzimuths.Count)];
            }
            else
            {
                // Fallback in case no matching azimuths were found
                trial.audioAzimuth = isRightSide ? 60.0 : -60.0;
            }

            // // Set audio elevation from AudioSpecifications
            // trial.audioElevation = (spec.AudioSpecifications != null) ? spec.AudioSpecifications.Elevation : 0;
        }

        // // Calculate locations from azimuth
        // trial.visualLocationX = Math.Sin(trial.visualAzimuth * Math.PI / 180.0);
        // trial.audioLocationX = Math.Sin(trial.audioAzimuth * Math.PI / 180.0);

        // // Get the speaker ID for this azimuth/elevation combination
        // trial.audioSpeakerID = GetClosestSpeaker(trial.audioAzimuth, trial.audioElevation);

        // trial.isVisualActive = true;
        // trial.isAudioActive = true;
        
        trial.RewardProbability = 1;
        trial.IsRewarded = true;
    }

    private void SetupMultisensoryConflictTrial(TrialData trial, AvTask spec, Random rand)
    {
        // For conflict trials: visual and auditory stimuli on opposite sides

        // First, pick a side for visual stimulus (opposite for audio)
        bool isVisualRight = rand.NextDouble() >= 0.5;
        trial.targetSide = isVisualRight ? trial.targetSide = "Right" : trial.targetSide = "Left";


        // Set visual parameters
        if (spec.GratingsSpecifications != null)
        {
            // Set visual azimuth based on side
            List<double> visualAzimuths = new List<double>();
            if (spec.GratingsSpecifications.Azimuth != null &&
                spec.GratingsSpecifications.Azimuth.Count > 0)
            {
                foreach (var item in spec.GratingsSpecifications.Azimuth)
                {
                    double azimuth = Convert.ToDouble(item);
                    // Filter for only positive or negative azimuths based on selected side
                    if ((isVisualRight && azimuth > 0) || (!isVisualRight && azimuth < 0))
                    {
                        visualAzimuths.Add(azimuth);
                    }
                }
            }

            // If we found suitable azimuths, pick one at random
            if (visualAzimuths.Count > 0)
            {
                trial.visualAzimuth = visualAzimuths[rand.Next(visualAzimuths.Count)];
            }
            else
            {
                // Fallback in case no matching azimuths were found
                trial.visualAzimuth = isVisualRight ? 60.0 : -60.0;
            }

            // Set non-zero contrast
            if (spec.GratingsSpecifications.Contrast != null &&
                spec.GratingsSpecifications.Contrast.Count > 0)
            {
                var nonZeroContrasts = new List<double>();
                foreach (var item in spec.GratingsSpecifications.Contrast)
                {
                    double contrast = Convert.ToDouble(item);
                    if (contrast > 0) nonZeroContrasts.Add(contrast);
                }

                if (nonZeroContrasts.Count > 0)
                {
                    trial.visualContrast = nonZeroContrasts[rand.Next(nonZeroContrasts.Count)];
                }
                else
                {
                    trial.visualContrast = 0.2; // Default value if no non-zero contrasts
                }
            }

            // trial.visualExtentX = spec.GratingsSpecifications.ExtentX;
            // trial.visualExtentY = spec.GratingsSpecifications.ExtentY;
        }

        // Set audio parameters on the opposite side
        if (spec.AudioSpecifications != null)
        {
            // Set audio azimuth based on the opposite side from visual
            List<double> audioAzimuths = new List<double>();
            if (spec.AudioSpecifications.Azimuth != null &&
                spec.AudioSpecifications.Azimuth.Count > 0)
            {
                foreach (var item in spec.AudioSpecifications.Azimuth)
                {
                    double azimuth = Convert.ToDouble(item);
                    // Filter for only positive or negative azimuths based on OPPOSITE side
                    if ((!isVisualRight && azimuth > 0) || (isVisualRight && azimuth < 0))
                    {
                        audioAzimuths.Add(azimuth);
                    }
                }
            }

            // If we found suitable azimuths, pick one at random
            if (audioAzimuths.Count > 0)
            {
                trial.audioAzimuth = audioAzimuths[rand.Next(audioAzimuths.Count)];
            }
            else
            {
                // Fallback in case no matching azimuths were found
                trial.audioAzimuth = !isVisualRight ? 60.0 : -60.0;
            }

            // Set audio elevation (default to 0° if not specified)
            // trial.audioElevation = 0;
        }

        // // Calculate locations from azimuth
        // trial.visualLocationX = Math.Sin(trial.visualAzimuth * Math.PI / 180.0);
        // trial.audioLocationX = Math.Sin(trial.audioAzimuth * Math.PI / 180.0);

        // // Get the speaker ID for this azimuth/elevation combination
        // trial.audioSpeakerID = GetClosestSpeaker(trial.audioAzimuth, trial.audioElevation);

        // trial.isVisualActive = true;
        // trial.isAudioActive = true;

        // Check this again, but the 50% should come from the random targetSide, no additional random, right?!
        trial.RewardProbability = 1.0; // Default reward probability for conflict trials
        trial.IsRewarded = true;
        // trial.RewardProbability = 0.5; 
        // trial.IsRewarded = rand.NextDouble() < 0.5;
    }

    private void SetupNeutralTrial(TrialData trial, AvTask spec, Random rand)
    {
        // For neutral trials: zero contrast (gray screen), auditory stimulus in center (0°)

        // Initially had it as both for Bonsai to handle the random reward assignment. But We might as well do this here already: 
        // trial.targetSide = "Both";
        trial.targetSide = rand.NextDouble() >= 0.5 ? trial.targetSide = "Right" : trial.targetSide = "Left";

        // Set visual position to center (irrelevant for gray screen)
        trial.visualAzimuth = 0;
        // Set visual contrast to zero
        trial.visualContrast = 0;

        // Set auditory stimulus to center (0°)
        trial.audioAzimuth = 0;
        // trial.audioElevation = 15;

        // // Get the speaker ID for center position
        // trial.audioSpeakerID = GetClosestSpeaker(trial.audioAzimuth, trial.audioElevation);

        // trial.isVisualActive = true;
        // trial.isAudioActive = true;
        
        // trial.RewardProbability = 0.5; // Default reward probability for conflict trials
        // trial.IsRewarded = rand.NextDouble() < 0.5;
        
        // Check this again, but the 50% should come from the random targetSide, no additional random, right?!
        trial.RewardProbability = 1.0; // Default reward probability for conflict trials
        trial.IsRewarded = true;
    }

    // handy function with overloads    
    double drawRandomUniformFromRange(double lowerBound, double upperBound, Random trialRand)
    {
        return lowerBound + (double)(trialRand.NextDouble() * (upperBound - lowerBound));
    }

    double drawRandomUniformFromRange(double[] range, Random trialRand)
    {
        return range[0] + (double)(trialRand.NextDouble() * (range[1] - range[0]));
    }

    // Overload to accept List<double> since some configs use lists
    double drawRandomUniformFromRange(List<double> range, Random trialRand)
    {
        if (range == null || range.Count < 2) return 0.0;
        return range[0] + (double)(trialRand.NextDouble() * (range[1] - range[0]));
    }

    // These felt like a bit much, and would siable the ability to define a Seed...
    // float drawRandomUniformFromRange(float lowerBound, float upperBound)
    // {
    //     Random trialRand = new Random();
    //     return lowerBound + (float)(trialRand.NextDouble() * (upperBound - lowerBound));
    // }

    // float drawRandomUniformFromRange(float[] boundVar)
    // {
    //     Random trialRand = new Random();
    //     return boundVar[0] + (float)(trialRand.NextDouble() * (boundVar[1] - boundVar[0]));
    // }

    // Now also draw random from probability
    bool drawRandomBasedOnProbability(double probability, Random trialRand)
    {
        // draw binary true falls on the given probability
        return trialRand.NextDouble() < probability;
    }

}

public class TrialData
{
    // TrialStructure
    public int trialType;
    public double RewardProbability;  // Probability of reward delivery for this trial (used for random reward assignment in conflict and neutral trials)
    public bool IsRewarded;
    // Trial repetition tracking
    public bool IsRepeat;
    public int RepeatCount;
    public string targetSide;
    public double ITI;
    public double SpoutINDelay { get { return 0.5; } }  // in seconds. Relative to stimulus onset when to move response spouts IN
    public double IncorrectTimeoutDuration { get { return 4.0; } }  // in seconds
    public double autoRewardProbability;  // probability of automatically rewarding the trial (for early training stages)
    public bool autoReward;  // Bool defining if current trial is autoRewarded or not
    public bool largeReward;  // Bool defining if current trial is autoRewarded or not
    public double RewardSizeInUl;  // Size of rewards in ul.
    public double LargeRewardMultiplier { get { return 1; } }  // Size of rewards in ul.
    public double LargeRewardProbability { get { return 0.00; } }  // Probability of receiving a large reward
    public bool BothSpouts;  // Bool defining if current trial is presenting both spouts (regular trial) or just the correct spout (used for initial training).
    public double BothSpoutsProbability { get; set; }  // by default only regular trials. 

    // public double licksMoveGrating { get; set; }  // use lick-grating closed loop?
    // public double licksToTarget { get; set; }  // when above true, how often until reward and how much to move by
 
    public double openLoopDuration { get { return 0.0; } }
    public double closedLoopDuration { get { return 1.5; } }

    // VisualStimulus
    public double visualAzimuth;
    public double visualElevation { get { return 15; } }
    public double visualGaussianWindowSigma { get { return 20; } }  // in deg
    public double visualContrast;
    public double visualOrientation { get { return 0; } }
    public double visualSpatialFrequency { get { return 0.066666667; } }
    public double visualTemporalFrequency { get { return 0; } }
    public double visualFlickerFrequency { get { return 8; } }  // in Hz
    public double visualPhase;
    
    // public bool isVisualActive;

    // AuditoryStimulus
    public double audioAzimuth;
    public double audioElevation { get { return 15; } }
    public double audioFlickerFrequency { get { return 8; } }  // in Hz
    // public int audioSpeakerID;
    // public bool isAudioActive;
    public int audioWaveformIdx;  // index of 0:100 waveforms pre-generated
    
    // Optogenetics
    public bool OptogeneticEnabled;
    public int OptogeneticCondition;
    public double OptogeneticDelay;
    public double OptogeneticDuration;
    public double OptogeneticPower;
}
