using Bonsai;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using OpenCV.Net;

[Combinator]
[Description("Makes a border around each buffer in the sequence.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class MakeBorder
{
    [Description("The duration, in seconds, by which to extend the buffer.")]
    public double Duration { get; set; }

    [Description("The offset, in seconds, used to position the original buffer within the extended buffer.")]
    public double Offset { get; set; }

    [Description("The type of border to create around the original buffer.")]
    public IplBorder BorderType { get; set; }

    [Description("The value used to fill a constant border.")]
    public Scalar Value { get; set; }

    [Description("The sampling rate of the generated signal waveform, in Hz.")]
    public int SampleRate { get; set; }

    public IObservable<Mat> Process(IObservable<Mat> source)
    {
        return source.Select(buffer =>
        {
            var sampleRate = SampleRate;
            var durationSamples = (int)(Duration * sampleRate);
            var offsetSamples = (int)(Offset * sampleRate);
            var output = new Mat(buffer.Rows, durationSamples, buffer.Depth, buffer.Channels);
            CV.CopyMakeBorder(buffer, output, new Point(offsetSamples, 0), BorderType);
            return output;
        });
    }
}
