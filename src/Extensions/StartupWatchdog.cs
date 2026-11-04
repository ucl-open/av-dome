using Bonsai;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;

// The workflow reports Ready only once every stepper has been zeroed (SteppersZEROed). On
// GREEN-DOME-1 (4 Nov 2026) two launches out of about 25 stayed in Starting for minutes with
// nothing in stderr, so nobody could tell which stage never came. This names it.
[Combinator]
[Description("Watches the names of the startup stages as they arrive. Completes once every name in Expected has arrived; if TimeoutSeconds pass first, ends the workflow with an error naming the stages that arrived and those still missing.")]
[WorkflowElementCategory(ElementCategory.Sink)]
public class StartupWatchdog
{
    public StartupWatchdog()
    {
        TimeoutSeconds = 60;
        Expected = string.Empty;
        What = "Startup";
    }

    [Description("The stages that must all arrive, comma-separated, in the order they normally do.")]
    public string Expected { get; set; }

    [Description("How long the stages may take, from the start of the workflow, in seconds.")]
    public double TimeoutSeconds { get; set; }

    [Description("What the stages make up, for the error message (for example 'Zeroing the spout steppers').")]
    public string What { get; set; }

    public IObservable<string> Process(IObservable<string> source)
    {
        return Observable.Defer(() =>
        {
            var expected = (Expected ?? string.Empty)
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            var arrived = new List<string>();
            var gate = new object();
            var timeoutSeconds = TimeoutSeconds;
            var what = What;

            // A stage source that completes without its value must not end the watch early.
            var stages = source
                .Concat(Observable.Never<string>())
                .Do(stage =>
                {
                    lock (gate)
                    {
                        if (!arrived.Contains(stage)) arrived.Add(stage);
                    }
                })
                .Where(stage =>
                {
                    lock (gate) { return expected.All(arrived.Contains); }
                })
                .Take(1);

            var timeout = Observable.Timer(TimeSpan.FromSeconds(timeoutSeconds))
                .SelectMany(_ =>
                {
                    List<string> seen, missing;
                    lock (gate)
                    {
                        seen = expected.Where(arrived.Contains).ToList();
                        missing = expected.Where(s => !arrived.Contains(s)).ToList();
                    }
                    return Observable.Throw<string>(new TimeoutException(string.Format(
                        "{0} did not finish within {1} s. Arrived: {2}. Never arrived: {3}.",
                        what,
                        timeoutSeconds,
                        seen.Count > 0 ? string.Join(", ", seen) : "nothing",
                        string.Join(", ", missing))));
                });

            return stages.Amb(timeout).IgnoreElements();
        });
    }
}
