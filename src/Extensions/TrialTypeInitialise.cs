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
    public int TrialType { get; set; }
    
    // Response handling properties
    [Description("Last response from subject: -1 (Left), 1 (Right), 0 (Timeout/No response)")]
    public float LastResponse { get; set; }
    
    [Description("Whether the last trial ended in a timeout")]
    public bool WasTimeout { get; set; }
    
    [Description("Whether the last response was correct")]
    public bool WasCorrect { get; set; }

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
    private int totalWeight;
    
    // Track if we need to shuffle the trial order for true randomization
    private List<int> shuffledTrialSequence;
    private int currentTrialIndex = 0;
    
    // Trial repetition tracking
    private TrialData lastTrialData = null;
    private int currentRepeatCount = 0;
    private const int MAX_REPEATS = 9;
    private bool isFirstTrial = true;

    private static List<int> _zapitConditions = new List<int>();
    private static List<int> _conditionQueue = new List<int>();
    private static int _currentConditionIndex = 0;
    private static DateTime _zapitConfigLastLoaded = DateTime.MinValue;

    public IObservable<TrialData> Process(IObservable<AvTask> source)
    {
        // Set up the weighted distribution
        // Ratio of 10/10/5/5/1 for trial types 0,1,2,3,4
        List<int> trialWeights = new List<int> { 10, 10, 5, 5, 1 };
        int totalWeight = trialWeights.Sum();

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
                return repeatTrial;
            }

            if (value.Optogenetics != null && value.Optogenetics.Enabled)
            {
                LoadZapitConfigIfNeeded(value.Optogenetics.ZapitConfigPath);
            }

            // Generate new trial
            currentRepeatCount = 0;
            isFirstTrial = false;
            var newTrial = GenerateNewTrial(value);
            lastTrialData = newTrial;
            return newTrial;
        });
    }
    
    private bool ShouldRepeatTrial()
    {
        // Don't repeat if no previous trial or max repeats reached or this is the first trial
        if (lastTrialData == null || currentRepeatCount >= MAX_REPEATS || isFirstTrial) 
            return false;
            
        // Check repetition rules based on trial type
        switch (lastTrialData.trialType)
        {
            case 0: // Unisensory Visual
            case 1: // Unisensory Audio  
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
        // return new TrialData
        // {
        //     ITI = original.ITI,
        //     Quiescent = original.Quiescent,
        //     visualAzimuth = original.visualAzimuth,
        //     visualContrast = original.visualContrast,
        //     visualExtentX = original.visualExtentX,
        //     visualExtentY = original.visualExtentY,
        //     isVisualActive = original.isVisualActive,
        //     audioAzimuth = original.audioAzimuth,
        //     audioElevation = original.audioElevation,
        //     isAudioActive = original.isAudioActive,
        //     trialType = original.trialType,
        //     IsRepeat = true,
        //     RepeatCount = currentRepeatCount + 1,
        //     RewardProbability = original.RewardProbability,
        //     IsRewarded = original.IsRewarded, 
        //     targetSide = original.targetSide,
        //     OptogeneticCondition = original.OptogeneticCondition,
        //     OptogeneticDelay = original.OptogeneticDelay,
        //     OptogeneticDuration = original.OptogeneticDuration,
        //     OptogeneticPower = original.OptogeneticPower,
        //     OptogeneticEnabled = original.OptogeneticEnabled
            
        // };
        return original;
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

        if (value.TrialStructure != null &&
            value.TrialStructure.Iti != null &&
            value.TrialStructure.Iti.Count >= 2)
        {
            trialData.ITI = value.TrialStructure.Iti[0] + 
                (trialRand.NextDouble() * (value.TrialStructure.Iti[1] - value.TrialStructure.Iti[0]));
        }

        if (value.TrialStructure != null &&
            value.TrialStructure.Quiescent != null &&
            value.TrialStructure.Quiescent.Count >= 2)
        {
            trialData.Quiescent = value.TrialStructure.Quiescent[0] + 
                (trialRand.NextDouble() * (value.TrialStructure.Quiescent[1] - value.TrialStructure.Quiescent[0]));
        }

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
        
        trialData.trialType = selectedTrialType;
        trialData.IsRepeat = false;
        trialData.RepeatCount = 0;
        SetupOptogeneticParameters(trialData, value, trialRand);
        return trialData;
    }
    
    // Gets a random trial type based on the weighted distribution
    // Now using the Fisher-Yates shuffle to ensure balanced distribution
    private int GetWeightedRandomTrialType(TrialData trialData, AvTask config, Random random)
    {
        // Set up the weighted distribution
        // Ratio of 10/10/5/5/1 for trial types 0,1,2,3,4
        // List<int> trialWeights = new List<int> { 10, 10, 5, 5, 1 };
        // List<int> trialWeights = new List<int> { 10, 10, 5, 5, 1 };
        List<int> trialWeights = config.TrialStructure.TrialTypeRatios;
        // Console.WriteLine(trialWeights);
        // Console.WriteLine(config.TrialStructure.TrialTypeRatios);

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

        // Set other visual parameters
        if (spec.GratingsSpecifications != null)
        {
            trial.visualExtentX = spec.GratingsSpecifications.ExtentX;
            trial.visualExtentY = spec.GratingsSpecifications.ExtentY;
        }

        // Set auditory stimulus to center (0°)
        trial.audioAzimuth = 0;
        trial.audioElevation = 0;
        // trial.audioLocationX = 0; // Sin(0°) = 0
        // trial.audioSpeakerID = GetClosestSpeaker(trial.audioAzimuth, trial.audioElevation);

        trial.isVisualActive = true;
        trial.isAudioActive = true;

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
        if (spec.GratingsSpecifications != null)
        {
            trial.visualExtentX = spec.GratingsSpecifications.ExtentX;
            trial.visualExtentY = spec.GratingsSpecifications.ExtentY;
        }

        // Set auditory azimuth (-60°, 0°, or +60°)
        if (spec.AudioSpecifications != null &&
            spec.AudioSpecifications.Azimuth != null &&
            spec.AudioSpecifications.Azimuth.Count > 0)
        {
            var index = rand.Next(spec.AudioSpecifications.Azimuth.Count);
            trial.audioAzimuth = Convert.ToDouble(spec.AudioSpecifications.Azimuth[index]);
        }

        // Set audio elevation from AudioSpecifications
        trial.audioElevation = (spec.AudioSpecifications != null) ? spec.AudioSpecifications.Elevation : 0;

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

        trial.isVisualActive = true;
        trial.isAudioActive = true;
        
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

            trial.visualExtentX = spec.GratingsSpecifications.ExtentX;
            trial.visualExtentY = spec.GratingsSpecifications.ExtentY;
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

            // Set audio elevation from AudioSpecifications
            trial.audioElevation = (spec.AudioSpecifications != null) ? spec.AudioSpecifications.Elevation : 0;
        }

        // // Calculate locations from azimuth
        // trial.visualLocationX = Math.Sin(trial.visualAzimuth * Math.PI / 180.0);
        // trial.audioLocationX = Math.Sin(trial.audioAzimuth * Math.PI / 180.0);

        // // Get the speaker ID for this azimuth/elevation combination
        // trial.audioSpeakerID = GetClosestSpeaker(trial.audioAzimuth, trial.audioElevation);

        trial.isVisualActive = true;
        trial.isAudioActive = true;
        
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

            trial.visualExtentX = spec.GratingsSpecifications.ExtentX;
            trial.visualExtentY = spec.GratingsSpecifications.ExtentY;
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
            trial.audioElevation = 0;
        }

        // // Calculate locations from azimuth
        // trial.visualLocationX = Math.Sin(trial.visualAzimuth * Math.PI / 180.0);
        // trial.audioLocationX = Math.Sin(trial.audioAzimuth * Math.PI / 180.0);

        // // Get the speaker ID for this azimuth/elevation combination
        // trial.audioSpeakerID = GetClosestSpeaker(trial.audioAzimuth, trial.audioElevation);

        trial.isVisualActive = true;
        trial.isAudioActive = true;

        trial.RewardProbability = 0.5; 
        trial.IsRewarded = rand.NextDouble() < 0.5;
    }

    private void SetupNeutralTrial(TrialData trial, AvTask spec, Random rand)
    {
        // For neutral trials: zero contrast (gray screen), auditory stimulus in center (0°)

        // Initially had it as both for Bonsai to handle the random reward assignment. But We might as well do this here already: 
        // trial.targetSide = "Both";
        trial.targetSide = rand.NextDouble() >= 0.5 ? trial.targetSide = "Right" : trial.targetSide = "Left";

        // Set visual contrast to zero
        trial.visualContrast = 0;

        // Set other visual parameters
        if (spec.GratingsSpecifications != null)
        {
            trial.visualExtentX = spec.GratingsSpecifications.ExtentX;
            trial.visualExtentY = spec.GratingsSpecifications.ExtentY;
        }

        // Set auditory stimulus to center (0°)
        trial.audioAzimuth = 0;
        trial.audioElevation = 0;
        // trial.audioLocationX = 0; // Sin(0°) = 0

        // // Get the speaker ID for center position
        // trial.audioSpeakerID = GetClosestSpeaker(trial.audioAzimuth, trial.audioElevation);

        // Set visual position to center (irrelevant for gray screen)
        // trial.visualLocationX = 0;
        trial.visualAzimuth = 0;

        trial.isVisualActive = true;
        trial.isAudioActive = true;
        
        trial.RewardProbability = 0.5; // Default reward probability for conflict trials
        trial.IsRewarded = rand.NextDouble() < 0.5;
    }
}

public class TrialData
{
    public double ITI;
    public double Quiescent;

    // public double visualLocationX;
    public double visualAzimuth;
    public double visualContrast;
    public double visualExtentX;
    public double visualExtentY;
    public bool isVisualActive;

    // public double audioLocationX;
    public double audioAzimuth;
    public double audioElevation;
    // public int audioSpeakerID;
    public bool isAudioActive;

    public int trialType;

    public double RewardProbability;

    public bool IsRewarded;

    // Trial repetition tracking
    public bool IsRepeat;
    public int RepeatCount;

    public string targetSide;
    // public double X { get { return visualLocationX; } }
    public double Y { get { return 0; } }
    public double Orientation { get { return visualAzimuth; } }
    public double SpatialFrequency { get { return 0.1; } }
    public double TemporalFrequency { get { return 0; } }
    public double Contrast { get { return visualContrast; } }
    public double Diameter { get { return visualExtentX; } }
    public string Id { get { return Guid.NewGuid().ToString(); } }
    
    public bool OptogeneticEnabled;
    public int OptogeneticCondition;
    public double OptogeneticDelay;
    public double OptogeneticDuration;
    public double OptogeneticPower;
}