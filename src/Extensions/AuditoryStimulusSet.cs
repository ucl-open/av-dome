using Bonsai;
using Newtonsoft.Json;
using OpenCV.Net;
using OpenRigs.Core.Calibration;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using UclOpenAvDome;

/// <summary>
/// The auditory stimuli of a session: the rig's set of stimulus waveforms, each already
/// equalised for every speaker that has a filter in rig.yml, so a trial's stimulus is a lookup
/// with no filtering at stimulus onset.
/// </summary>
public class AuditoryStimulusSet
{
    /// <summary>Number of waveforms in the set, filtered_pink_pulse_train_0.wav to _99.wav.</summary>
    public const int WaveformCount = 100;

    /// <summary>Pulse rate the set was generated at, in Hz; the task's flickerFrequency must equal it.</summary>
    public const double PulseRateHz = 8;

    readonly Mat[] raw;
    readonly Dictionary<int, Mat[]> equalised;
    readonly Dictionary<int, Mat> kernels;

    internal AuditoryStimulusSet(Mat[] raw, Dictionary<int, Mat> kernels, int[] speakerChannels)
    {
        this.raw = raw;
        this.kernels = kernels;
        SpeakerChannels = speakerChannels;
        equalised = new Dictionary<int, Mat[]>();
        foreach (var entry in kernels)
        {
            equalised[entry.Key] = raw.Select(waveform => Equalise(waveform, entry.Value)).ToArray();
        }
    }

    /// <summary>The mixer channel of every speaker in rig.yml, in rig.yml's order.</summary>
    public int[] SpeakerChannels { get; private set; }

    /// <summary>Samples in every waveform of the set (they are checked to be equal), for the sync channel's gate.</summary>
    public int WaveformLength
    {
        get { return raw[0].Cols; }
    }

    /// <summary>The waveform a trial plays on a channel, equalised if that speaker has a filter.</summary>
    public Mat Stimulus(int index, int channel)
    {
        if (index < 0 || index >= raw.Length)
        {
            throw new InvalidOperationException(
                "Trial asks for auditory waveform " + index + ", but the set has " + raw.Length + " (0 to " + (raw.Length - 1) + ").");
        }
        Mat[] waveforms;
        return equalised.TryGetValue(channel, out waveforms) ? waveforms[index] : raw[index];
    }

    /// <summary>Any waveform equalised for a channel's speaker; unchanged if it has no filter.</summary>
    public Mat EqualiseFor(Mat waveform, int channel)
    {
        Mat kernel;
        return kernels.TryGetValue(channel, out kernel) ? Equalise(waveform, kernel) : waveform;
    }

    // Each row of the waveform correlated with the kernel, centred on the kernel's middle tap and
    // zero beyond the ends, so the output is as long as the input and not delayed. Load refuses
    // taps that are not symmetric (linear phase), so this is the convolution too. Production's
    // FirFilter gives the same output 128 samples later for its 257 taps.
    static Mat Equalise(Mat waveform, Mat kernel)
    {
        int taps = kernel.Cols;
        int anchor = taps / 2;
        var input = new Mat(waveform.Rows, waveform.Cols, Depth.F32, 1);
        CV.Convert(waveform, input);
        using (var padded = Mat.Zeros(waveform.Rows, waveform.Cols + taps - 1, Depth.F32, 1))
        using (var filtered = new Mat(padded.Size, Depth.F32, 1))
        {
            using (var middle = padded.GetSubRect(new Rect(anchor, 0, waveform.Cols, waveform.Rows)))
            {
                CV.Copy(input, middle);
            }
            CV.Filter2D(padded, filtered, kernel, new Point(anchor, 0));
            var output = new Mat(waveform.Rows, waveform.Cols, Depth.F32, 1);
            using (var result = filtered.GetSubRect(new Rect(anchor, 0, waveform.Cols, waveform.Rows)))
            {
                CV.Copy(result, output);
            }
            input.Dispose();
            return output;
        }
    }

    /// <summary>
    /// Reads the set and the speakers' filters, failing with a message naming every file that is
    /// missing or unusable; public so a check can run it without a workflow.
    /// </summary>
    public static AuditoryStimulusSet Load(SpeakerArray speakerArray, string rigDirectory, string folder, string filePrefix)
    {
        if (speakerArray == null || speakerArray.Speakers == null || speakerArray.Speakers.Count == 0)
        {
            throw new InvalidOperationException("rig.yml has no speakerArray.speakers, so there is nowhere to play the auditory stimuli.");
        }
        int sampleRate = speakerArray.Device.SampleRate;

        var kernels = new Dictionary<int, Mat>();
        var problems = new List<string>();
        foreach (var entry in speakerArray.Speakers)
        {
            var speaker = entry.Value;
            if (string.IsNullOrEmpty(speaker.Filter))
            {
                Console.WriteLine("WARNING: speaker '" + entry.Key + "' (channel " + speaker.Channel + ") has no filter in rig.yml, so it plays unequalised.");
                continue;
            }
            string path = Path.GetFullPath(Path.IsPathRooted(speaker.Filter) ? speaker.Filter : Path.Combine(rigDirectory, speaker.Filter));
            if (!File.Exists(path))
            {
                problems.Add("speaker '" + entry.Key + "' filter not found: " + path);
                continue;
            }
            SpeakerFilter filter;
            try
            {
                filter = JsonConvert.DeserializeObject<SpeakerFilter>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                problems.Add("speaker '" + entry.Key + "' filter is not a SpeakerFilter artefact (" + ex.Message + "): " + path);
                continue;
            }
            if (filter == null || filter.Taps == null || filter.Taps.Count == 0)
            {
                problems.Add("speaker '" + entry.Key + "' filter has no taps: " + path);
                continue;
            }
            if (Math.Abs(filter.SampleRate - sampleRate) > 0.5)
            {
                problems.Add("speaker '" + entry.Key + "' filter was designed for " + filter.SampleRate + " Hz, but the audio device runs at " + sampleRate + " Hz: " + path);
                continue;
            }
            if (!IsSymmetric(filter.Taps))
            {
                problems.Add("speaker '" + entry.Key + "' filter's taps are not symmetric, so equalising by correlation would not be its convolution: " + path);
                continue;
            }
            if (!string.IsNullOrEmpty(filter.MachineName) &&
                !string.Equals(filter.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("WARNING: speaker '" + entry.Key + "' filter was measured on " + filter.MachineName + ", not on this machine (" + Environment.MachineName + "): " + path);
            }
            var kernel = new Mat(1, filter.Taps.Count, Depth.F32, 1);
            Marshal.Copy(filter.Taps.Select(tap => (float)tap).ToArray(), 0, kernel.Data, filter.Taps.Count);
            kernels[speaker.Channel] = kernel;
        }

        string directory = Path.GetFullPath(Path.IsPathRooted(folder) ? folder : Path.Combine(rigDirectory, folder));
        var files = Enumerable.Range(0, WaveformCount).Select(i => Path.Combine(directory, filePrefix + i + ".wav")).ToArray();
        var missing = files.Where(file => !File.Exists(file)).ToArray();
        if (missing.Length > 0)
        {
            problems.Add(missing.Length + " of the " + WaveformCount + " auditory stimuli are missing from " + directory + ", among them " +
                string.Join(", ", missing.Take(3).Select(Path.GetFileName)) + ". The set is " + filePrefix + "0.wav to " + filePrefix + (WaveformCount - 1) + ".wav");
        }

        var raw = new Mat[WaveformCount];
        if (missing.Length == 0)
        {
            for (int i = 0; i < WaveformCount; i++)
            {
                try
                {
                    raw[i] = ReadWave(files[i], sampleRate);
                }
                catch (InvalidDataException ex)
                {
                    problems.Add(ex.Message);
                }
            }
        }

        if (problems.Count == 0)
        {
            var lengths = raw.Select(waveform => waveform.Cols).Distinct().ToArray();
            if (lengths.Length > 1)
            {
                problems.Add("the waveforms in " + directory + " differ in length (" + string.Join(", ", lengths) +
                    " samples); the sync channel's gate needs them all the same length");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The auditory stimuli cannot be loaded: " + string.Join("; ", problems) + ".");
        }

        var channels = speakerArray.Speakers.Values.Select(speaker => speaker.Channel).ToArray();
        return new AuditoryStimulusSet(raw, kernels, channels);
    }

    // Symmetric to within float precision of the largest tap, as a linear-phase FIR is.
    static bool IsSymmetric(IList<double> taps)
    {
        double tolerance = 1e-6 * taps.Max(tap => Math.Abs(tap));
        for (int i = 0; i < taps.Count / 2; i++)
        {
            if (Math.Abs(taps[i] - taps[taps.Count - 1 - i]) > tolerance) return false;
        }
        return true;
    }

    // A mono 16-bit PCM WAV as a 1 x samples F32 waveform in -1 to 1, scaled as the production
    // workflow's Rescale (-32768 to 32767 onto -1 to 1).
    static Mat ReadWave(string path, int sampleRate)
    {
        using (var reader = new BinaryReader(File.OpenRead(path)))
        {
            if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("not a WAV file: " + path);
            reader.ReadInt32();
            if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("not a WAV file: " + path);
            short format = 0, channels = 0, bits = 0;
            int rate = 0;
            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                string id = new string(reader.ReadChars(4));
                int size = reader.ReadInt32();
                if (id == "fmt ")
                {
                    format = reader.ReadInt16();
                    channels = reader.ReadInt16();
                    rate = reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt16();
                    bits = reader.ReadInt16();
                    reader.BaseStream.Seek(size - 16 + (size & 1), SeekOrigin.Current);
                }
                else if (id == "data")
                {
                    if (format != 1 || channels != 1 || bits != 16)
                    {
                        throw new InvalidDataException("must be mono 16-bit PCM (it is format " + format + ", " + channels + " channel(s), " + bits + " bit): " + path);
                    }
                    if (rate != sampleRate)
                    {
                        throw new InvalidDataException("is " + rate + " Hz, but the audio device runs at " + sampleRate + " Hz: " + path);
                    }
                    int samples = size / 2;
                    var values = new float[samples];
                    for (int i = 0; i < samples; i++)
                    {
                        values[i] = (reader.ReadInt16() + 32768f) * (2f / 65535f) - 1f;
                    }
                    var waveform = new Mat(1, samples, Depth.F32, 1);
                    Marshal.Copy(values, 0, waveform.Data, samples);
                    return waveform;
                }
                else
                {
                    reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
                }
            }
            throw new InvalidDataException("has no audio data: " + path);
        }
    }
}

[Combinator]
[Description("Loads the rig's auditory stimulus set at Start: the 100 waveforms in <RigConfigDirectory>\\<Folder>, each equalised for every speaker with a filter (a SpeakerFilter artefact) in rig.yml. Stops the workflow, naming each file, if any waveform or filter is missing, is not mono 16-bit PCM at the audio device's rate, or a filter was designed for another rate. Input is a Zip of RigConfigs.SpeakerArray and RigConfigDirectory.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class LoadAuditoryStimulusSet
{
    public LoadAuditoryStimulusSet()
    {
        Folder = "calibration\\AuditoryStimuli";
        FilePrefix = "filtered_pink_pulse_train_";
    }

    [Description("The folder of the stimulus waveforms, relative to the rig configuration directory.")]
    public string Folder { get; set; }

    [Description("The waveforms' file name before their index: <FilePrefix>0.wav and on.")]
    public string FilePrefix { get; set; }

    public IObservable<AuditoryStimulusSet> Process(IObservable<Tuple<SpeakerArray, string>> source)
    {
        return source.Select(input => AuditoryStimulusSet.Load(input.Item1, input.Item2, Folder, FilePrefix));
    }
}

[Combinator]
[Description("The waveform a trial plays: the stimulus set's waveform at the trial's audioWaveformIdx, equalised for the speaker on the given mixer channel. Input is a Zip of the stimulus set, the trial and the channel.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class SelectAuditoryStimulus
{
    public IObservable<Mat> Process(IObservable<Tuple<AuditoryStimulusSet, Trial, int>> source)
    {
        return source.Select(input => input.Item1.Stimulus(input.Item2.audioWaveformIdx, input.Item3));
    }
}

[Combinator]
[Description("Equalises a waveform for the speaker on the given mixer channel with that speaker's filter from rig.yml; passes it unchanged if the speaker has none. Input is a Zip of the stimulus set, the waveform and the channel.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class EqualiseForSpeaker
{
    public IObservable<Mat> Process(IObservable<Tuple<AuditoryStimulusSet, Mat, int>> source)
    {
        return source.Select(input => input.Item1.EqualiseFor(input.Item2, input.Item3));
    }
}
