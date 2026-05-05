using Bonsai;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using OpenCV.Net;

[Combinator]
[Description("Broadcasts input waveforms onto a multi-channel matrix.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class Broadcast
{
    [Description("The number of channels in the output matrix.")]
    public int ChannelCount { get; set; }

    public IObservable<Mat> Process(IObservable<Mat> source)
    {
        return source.Select(buffer =>
        {
            var channelCount = ChannelCount;
            if (buffer.Rows != channelCount)
            {
                if (buffer.Rows != 1)
                    throw new InvalidOperationException("Only single-channel waveforms can be broadcast to all channels.");

                var outputBuffer = new Mat(channelCount, buffer.Cols, buffer.Depth, buffer.Channels);
                CV.Repeat(buffer, outputBuffer);

                buffer = outputBuffer;
            }

            return buffer;
        });
    }
}
