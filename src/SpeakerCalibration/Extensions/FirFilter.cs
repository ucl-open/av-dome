// This file is licensed under the MIT License
// https://github.com/bonsai-rx/bonsai
// See the LICENSE file in the project root for permission notice
// Copyright (c) Bonsai Foundation CIC and Contributors
using Bonsai;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using OpenCV.Net;
using System.Xml.Serialization;

/// <summary>
/// Represents an operator that convolves the input signal with a finite-impulse
/// response filter kernel.
/// </summary>
/// <remarks>
/// This operator is implemented using cross-correlation. If kernels are symmetric,
/// there is no difference between correlation and convolution. When using asymmetric
/// kernels, note the kernel needs to be flipped to obtain a true convolution.
/// </remarks>
[Description("Convolves the input signal with a finite-impulse response filter kernel.")]
public class FirFilter : Transform<Mat, Mat>
{
    public FirFilter()
    {
        Anchor = -1;
    }

    /// <summary>
    /// Gets or sets the anchor of the kernel that indicates the relative position
    /// of a filtered point within the kernel.
    /// </summary>
    [Description("The anchor of the kernel that indicates the relative position of a filtered point within the kernel.")]
    public int Anchor { get; set; }

    /// <summary>
    /// Gets or sets the convolution kernel for the FIR filter.
    /// </summary>
    [XmlIgnore]
    [Description("The convolution kernel for the FIR filter.")]
    public Mat Kernel { get; set; }

    /// <summary>
    /// Convolves the input signal with a finite-impulse response filter kernel.
    /// </summary>
    /// <param name="source">
    /// A sequence of <see cref="Mat"/> objects representing the waveform of the
    /// signal to filter.
    /// </param>
    /// <returns>
    /// A sequence of <see cref="Mat"/> objects representing the waveform of the
    /// filtered signal.
    /// </returns>
    public override IObservable<Mat> Process(IObservable<Mat> source)
    {
        return Observable.Defer(() =>
        {
            Mat overlap = null;
            Mat overlapInput = null;
            Mat overlapEnd = null;
            Mat overlapStart = null;
            Mat overlapFilter = null;
            Rect overlapOutput = default(Rect);
            Mat kernel = null;
            return source.Select(input =>
            {
                if (Kernel != kernel ||
                    kernel != null &&
                    (input.Rows != overlapOutput.Height ||
                        input.Cols != overlapOutput.Width))
                {
                    kernel = Kernel;
                    if (kernel == null) kernel = null;
                    else
                    {
                        var anchor = Anchor;
                        if (anchor == -1) anchor = kernel.Cols / 2;
                        overlap = new Mat(input.Rows, input.Cols + kernel.Cols - 1, input.Depth, input.Channels);
                        overlapInput = overlap.GetSubRect(new Rect(kernel.Cols - 1, 0, input.Cols, input.Rows));
                        overlapFilter = new Mat(overlap.Rows, overlap.Cols, overlap.Depth, overlap.Channels);
                        if (kernel.Cols > 1)
                        {
                            overlapEnd = overlap.GetSubRect(new Rect(overlap.Cols - kernel.Cols + 1, 0, kernel.Cols - 1, input.Rows));
                            overlapStart = overlap.GetSubRect(new Rect(0, 0, kernel.Cols - 1, input.Rows));
                        }

                        overlapOutput = new Rect(anchor, 0, input.Cols, input.Rows);
                        CV.CopyMakeBorder(input, overlap, new Point(kernel.Cols - 1, 0), IplBorder.Reflect);
                    }
                }

                if (kernel == null) return input;
                else
                {
                    CV.Copy(input, overlapInput);
                    for (int i = 0; i < kernel.Rows; i++)
                    {
                        using (var overlapRow = overlap.GetRow(i))
                        using (var overlapFilterRow = overlapFilter.GetRow(i))
                        using (var kernelRow = kernel.GetRow(i))
                        {
                            CV.Filter2D(overlapRow, overlapFilterRow, kernelRow);
                        }
                    }
                    if (overlapEnd != null) CV.Copy(overlapEnd, overlapStart);
                    return overlapFilter.GetSubRect(overlapOutput).Clone();
                }
            });
        });
    }

    /// <summary>
    /// Convolves the input signal with a finite-impulse response filter kernel.
    /// </summary>
    /// <param name="source">
    /// A sequence of floating-point numbers representing the waveform of the
    /// signal to filter.
    /// </param>
    /// <returns>
    /// A sequence of floating-point numbers representing the waveform of the
    /// filtered signal.
    /// </returns>
    public IObservable<double> Process(IObservable<double> source)
    {
        return Observable.Using(
            () => new Mat(1, 1, Depth.F64, 1),
            buffer =>
            {
                return Process(source.Do(x => buffer.SetReal(0, x))
                                        .Select(x => buffer))
                                        .Select(x => x.GetReal(0));
            });
    }

    /// <summary>
    /// Convolves the input position signal with a finite-impulse response filter kernel.
    /// </summary>
    /// <param name="source">
    /// A sequence of 2D points representing the position signal to filter.
    /// </param>
    /// <returns>
    /// A sequence of 2D points representing the filtered position signal.
    /// </returns>
    public IObservable<Point2f> Process(IObservable<Point2f> source)
    {
        return Observable.Using(
            () => new Mat(2, 1, Depth.F32, 1),
            buffer =>
            {
                return Process(source.Do(p => { buffer.SetReal(0, 0, p.X); buffer.SetReal(1, 0, p.Y); })
                                        .Select(x => buffer))
                                        .Select(x => new Point2f(
                                            (float)x.GetReal(0, 0),
                                            (float)x.GetReal(1, 0)));
            });
    }
}
