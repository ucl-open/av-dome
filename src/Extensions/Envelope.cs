using Bonsai;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using OpenCV.Net;

[Combinator]
[Description("Applies a cosine-shaped attack/release envelope to the input waveform.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class Envelope
{
    [Description("The duration of the envelope attack phase. Must be zero or less than the size of the buffer.")]
    public double AttackDuration { get; set; }

    [Description("The duration of the envelope release phase. Must be zero or less than the size of the buffer and not overlap with attack phase.")]
    public double ReleaseDuration { get; set; }

    [Description("The sampling rate of the generated signal waveform, in Hz.")]
    public int SampleRate { get; set; }

    public IObservable<Mat> Process(IObservable<Mat> source)
    {
        return source.Select(buffer =>
        {
            var sampleRate = SampleRate;
            var attackSampleCount = (int)(sampleRate * AttackDuration);
            var releaseSampleCount = (int)(sampleRate * ReleaseDuration);
            var envelope = Mat.Ones(buffer.Size, buffer.Depth, buffer.Channels);
            if (attackSampleCount > 0)
            {
                if (attackSampleCount > buffer.Cols)
                    throw new InvalidOperationException("Attack duration exceeds buffer length.");

                using (var attackBuffer = envelope.GetCols(0, attackSampleCount))
                {
                    for (int i = 0; i < attackSampleCount; i++)
                    {
                        var value = attackSampleCount == 1
                            ? 1.0
                            : 0.5 - 0.5 * Math.Cos(Math.PI * i / (attackSampleCount - 1));
                        using (var column = attackBuffer.GetCols(i, i + 1))
                        {
                            column.Set(Scalar.All(value));
                        }
                    }
                }
            }

            if (releaseSampleCount > 0)
            {
                if (releaseSampleCount > buffer.Cols || (releaseSampleCount + attackSampleCount) > buffer.Cols)
                    throw new InvalidOperationException("Release duration exceeds buffer length or overlaps attack duration.");

                using (var releaseBuffer = envelope.GetCols(buffer.Cols - releaseSampleCount, buffer.Cols))
                {
                    for (int i = 0; i < releaseSampleCount; i++)
                    {
                        var value = releaseSampleCount == 1
                            ? 0.0
                            : 0.5 + 0.5 * Math.Cos(Math.PI * i / (releaseSampleCount - 1));
                        using (var column = releaseBuffer.GetCols(i, i + 1))
                        {
                            column.Set(Scalar.All(value));
                        }
                    }
                }
            }

            CV.Mul(buffer, envelope, envelope);
            return envelope;
        });
    }
}
