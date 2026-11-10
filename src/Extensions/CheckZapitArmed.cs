using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

// ArmZapit waits for Zapit's answer to a laser-on trial's StartStimulus before StartLaser's trigger, as the corridor's
// ArmZapit does (Pip, 10 Nov 2026): before, the trigger went 0.1 s after the command whatever Zapit answered, so a
// slow or refused arm left a laser-on trial dark while its row logged the power.
[Combinator]
[Description("Checks Zapit's reply to a laser-on trial's StartStimulus: passes it on when Zapit armed the trial's condition with the laser on, and stops the experiment otherwise. Input is the reply and the trial.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CheckZapitArmed
{
    public IObservable<ZapitReply> Process(IObservable<Tuple<ZapitReply, Trial>> source)
    {
        return source.Select(input =>
        {
            Check(input.Item1, input.Item2);
            return input.Item1;
        });
    }

    public static void Check(ZapitReply reply, Trial trial)
    {
        if (reply == null)
        {
            throw new ArgumentNullException("reply", "CheckZapitArmed needs Zapit's StartStimulus reply.");
        }
        if (trial == null)
        {
            throw new ArgumentNullException("trial", "CheckZapitArmed needs the trial.");
        }
        if (reply.IsError)
        {
            throw new InvalidOperationException(string.Format(
                "Zapit refused trial {0}'s StartStimulus (condition {1}): {2}. Check the MATLAB window: is the conditions file the task file names (zapitConfigPath) loaded, is Zapit calibrated, and is the DAQ working?",
                trial.trialNumber, trial.OptogeneticCondition, reply));
        }
        if (reply.Value != trial.OptogeneticCondition || reply.LaserOn != 1)
        {
            throw new InvalidOperationException(string.Format(
                "Zapit armed condition {0} with laserOn {1}, but trial {2} asked for condition {3} with the laser on.",
                reply.Value, reply.LaserOn, trial.trialNumber, trial.OptogeneticCondition));
        }
    }
}
