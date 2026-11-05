using System;
using System.ComponentModel;
using System.Globalization;
using System.Reactive.Linq;
using Bonsai;
using UclOpenAvDomeTask;

namespace UclOpenAvDome
{
    /// <summary>
    /// Applies the experiment controller's overrides to the task configuration read at Go: the
    /// reward size (every training stage's rewardSizeUl), the inter-trial interval bounds
    /// (trialStructure.itiMin and itiMax) and optogenetics (optogenetics.enabled, "on" or
    /// "off"). Each is text, as ControlServer holds parameters; blank keeps the task file's
    /// value. A value that is not a number of at least 0, or a Zapit value other than on, off or
    /// blank, or a minimum above the maximum, stops the workflow at Go naming the parameter,
    /// rather than running an experiment on a setting nobody asked for.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Applies the controller's RewardSizeUl, ItiMin, ItiMax and Zapit (on/off) to the task configuration; blank keeps the task file's value. Input: the configuration, then the four values as text.")]
    public class ApplyTaskOverrides
    {
        public IObservable<UclOpenAvDomeTaskLogic> Process(IObservable<Tuple<UclOpenAvDomeTaskLogic, string, string, string, string>> source)
        {
            return source.Select(item => Apply(item.Item1, item.Item2, item.Item3, item.Item4, item.Item5));
        }

        public static UclOpenAvDomeTaskLogic Apply(UclOpenAvDomeTaskLogic config, string rewardSizeUl, string itiMin, string itiMax, string zapit)
        {
            var parameters = config.TaskParameters;
            double value;
            if (TryNumber("RewardSizeUl", rewardSizeUl, out value))
            {
                foreach (var stage in parameters.TrainingStages) stage.RewardSizeUl = value;
            }
            if (TryNumber("ItiMin", itiMin, out value)) parameters.TrialStructure.ItiMin = value;
            if (TryNumber("ItiMax", itiMax, out value)) parameters.TrialStructure.ItiMax = value;
            if (parameters.TrialStructure.ItiMin > parameters.TrialStructure.ItiMax)
            {
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                    "ItiMin ({0}) is above ItiMax ({1}); set them so the minimum is at most the maximum.",
                    parameters.TrialStructure.ItiMin, parameters.TrialStructure.ItiMax));
            }

            var flag = (zapit ?? string.Empty).Trim().ToLowerInvariant();
            if (flag == "on") parameters.Optogenetics.Enabled = true;
            else if (flag == "off") parameters.Optogenetics.Enabled = false;
            else if (flag.Length > 0)
            {
                throw new ArgumentException("Zapit must be on, off or blank (the task file's setting); '" + zapit + "' is not.");
            }
            return config;
        }

        static bool TryNumber(string name, string text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || value < 0)
            {
                throw new ArgumentException(name + " must be a number of at least 0, or blank to keep the task file's; '" + text + "' is not.");
            }
            return true;
        }
    }
}
