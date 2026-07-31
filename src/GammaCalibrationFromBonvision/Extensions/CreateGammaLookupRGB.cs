using Bonsai;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using MathNet.Numerics.Distributions;
using MathNet.Numerics;

[Combinator]
[Description("CreateGammaLookup with a guard that stops the workflow when the ramp did not get brighter, instead of silently fitting a garbage lookup table.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CreateGammaLookupRGB
{
    public IObservable<double[]> Process(IObservable<Tuple<float, double>[]> source)
    {
        return source.Select(value =>
        {
            var x = Generate.Map(value, v => v.Item2);
            var y = Generate.Map(value, v => (double)v.Item1);

            // The ramp arrives darkest first, so the last reading must be brighter than the
            // first. If it is not, the fit below would still produce a lookup table and the
            // display would go black or grey with no indication of why.
            if (y.Length < 2)
            {
                throw new InvalidOperationException(
                    "At least two ramp measurements are needed to fit a gamma curve.");
            }

            if (y[y.Length - 1] <= y[0])
            {
                throw new InvalidOperationException(string.Format(
                    "The ramp produced no measurable increase in brightness: level {0:F3} read " +
                    "{1:F4} V but level {2:F3} read {3:F4} V. Check that the analog input is the " +
                    "photodiode channel and that its signal rises with brightness.",
                    x[0], y[0], x[x.Length - 1], y[y.Length - 1]));
            }

            // var p = Fit.LogarithmFunc(y, x);
            var p = Fit.PolynomialFunc(y, x, 6);
            var vmax = MathNet.Numerics.Statistics.ArrayStatistics.Maximum(y);

            // var p = Fit.Logarithm(y, x);
            //  return  Generate.Map(x, v => p.Item1 + p.Item2 * Math.Log(v));
            var lookup = Generate.Map(x, v => Math.Max(0, p(v * vmax)));

            // Pin the endpoints, which the polynomial fit does not pass through: black
            // cannot be darker than drive 0, and full white must reach full drive.
            lookup[0] = 0;
            lookup[lookup.Length - 1] = 1;

            return lookup;
        });
    }
}
