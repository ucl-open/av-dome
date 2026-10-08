using Bonsai;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using OpenCV.Net;

#region TypeConverters

/// <summary>
/// Converts a single integer or a delimited string into an int[].
/// Accepts: 3  |  1,2,3  |  [1, 2, 3]  |  (1 2 3)
/// </summary>
public class IntArrayOrSingleConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
    {
        return sourceType == typeof(string)
            || sourceType == typeof(int)
            || base.CanConvertFrom(context, sourceType);
    }

    public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
    {
        if (culture == null) culture = CultureInfo.CurrentCulture;

        if (value is int)
        {
            return new[] { (int)value };
        }

        var s = value as string;
        if (s != null)
        {
            s = (s ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(s))
                return null;

            // Remove optional wrapping brackets/parentheses
            s = s.TrimStart('[', '(').TrimEnd(']', ')');

            var parts = s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Select(p => int.Parse(p, culture)).ToArray();
        }

        return base.ConvertFrom(context, culture, value);
    }
}

/// <summary>
/// Converts a single double or a delimited string into a double[].
/// Accepts: 0.5  |  1.0,0.8,0.2  |  [1, 2, 3]
/// </summary>
public class DoubleArrayOrSingleConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
    {
        return sourceType == typeof(string)
            || sourceType == typeof(double)
            || base.CanConvertFrom(context, sourceType);
    }

    public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
    {
        if (culture == null) culture = CultureInfo.CurrentCulture;

        if (value is double)
        {
            return new[] { (double)value };
        }

        var s = value as string;
        if (s != null)
        {
            s = (s ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(s))
                return null;

            s = s.TrimStart('[', '(').TrimEnd(']', ')');

            var parts = s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Select(p => double.Parse(p, culture)).ToArray();
        }

        return base.ConvertFrom(context, culture, value);
    }
}

#endregion

[Combinator]
[Description("Sets the activation gain for specified channels in the input waveform.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class ChannelActivation
{
    static readonly double[] DefaultGain = new[] { 1.0 };

    [TypeConverter(typeof(IntArrayOrSingleConverter))]
    [Description("The indices of the channels to activate in the output buffer. All other channels will be set to zero.")]
    public int[] Channels { get; set; }

    [TypeConverter(typeof(DoubleArrayOrSingleConverter))]
    [Description("The gain at which to set each channel, or the array of gains for individual channels. If not specified, the default value is one.")]
    public double[] Gain { get; set; }

    public IObservable<Mat> Process(IObservable<Mat> source)
    {
        return source.Select(buffer =>
        {
            var channels = Channels;
            var gain = Gain;

            if (channels != null && channels.Length == 0)
                channels = null;
            if (gain != null && gain.Length == 0)
                gain = null;

            if (channels == null && gain == null)
                return buffer;

            gain = gain ?? DefaultGain;

            if (channels == null)
            {
                var output = new Mat(buffer.Size, buffer.Depth, buffer.Channels);

                if (gain.Length == 1)
                {
                    CV.ConvertScale(buffer, output, gain[0]);
                }
                else if (gain.Length != buffer.Rows)
                {
                    throw new InvalidOperationException(
                        "Array of gains must match the number of channels in the input waveform.");
                }
                else
                {
                    var gainVector = new Mat(gain.Length, 1, buffer.Depth, buffer.Channels);
                    using (var gainHeader = Mat.CreateMatHeader(gain, gain.Length, 1, Depth.F64, 1))
                    {
                        CV.Convert(gainHeader, gainVector);
                    }

                    CV.Repeat(gainVector, output);
                    CV.Mul(buffer, output, output);
                }

                return output;
            }
            else
            {
                if (gain.Length != 1 && gain.Length != channels.Length)
                    throw new InvalidOperationException("Array of gains must match the number of selected channels.");

                var output = Mat.Zeros(buffer.Size, buffer.Depth, buffer.Channels);
                for (int i = 0; i < channels.Length; i++)
                {
                    using (var row = output.GetRow(channels[i]))
                        row.Set(Scalar.All(gain[i % gain.Length]));
                }
                CV.Mul(buffer, output, output);
                return output;
            }
        });
    }
}
