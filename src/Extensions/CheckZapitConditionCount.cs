using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;
using UclOpenAvDomeTask;

// Startup check for optogenetics experiments (audit, 10 Nov 2026, round 2; as coen-dome-navigation's). Bonsai sends Zapit
// condition numbers read from the task's Zapit file (optogenetics.zapitConfigPath), and Zapit presents whatever it has at
// that position in the file it has loaded. If the two differ, the wrong sites would be stimulated with no error, so the
// experiment stops at startup instead.
[Combinator]
[Description("Compares Zapit's GetNumConditions reply with the number of conditions in the task's Zapit file, and stops the experiment if they differ or Zapit answered with an error. Passes the reply through when they match. Input is the GetNumConditions reply and the task.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CheckZapitConditionCount
{
    public IObservable<ZapitReply> Process(IObservable<Tuple<ZapitReply, UclOpenAvDomeTaskLogic>> source)
    {
        return source.Select(input =>
        {
            Check(input.Item1, input.Item2);
            return input.Item1;
        });
    }

    public static void Check(ZapitReply reply, UclOpenAvDomeTaskLogic task)
    {
        if (reply == null)
        {
            throw new ArgumentNullException("reply", "CheckZapitConditionCount needs Zapit's GetNumConditions reply.");
        }
        if (reply.Command != ZapitCommandType.GetNumConditions)
        {
            throw new InvalidOperationException(string.Format(
                "CheckZapitConditionCount expects Zapit's GetNumConditions reply but received {0}.", reply));
        }
        if (task == null || task.TaskParameters == null || task.TaskParameters.Optogenetics == null)
        {
            throw new ArgumentNullException("task", "CheckZapitConditionCount needs the task.");
        }

        var path = task.TaskParameters.Optogenetics.ZapitConfigPath;
        var count = InitialiseTrial.ReadZapitConditions(path).Count;
        if (reply.IsError)
        {
            throw new InvalidOperationException(string.Format(
                "Zapit answered GetNumConditions with an error ({0}), so the number of conditions it has loaded is unknown. " +
                "The task's Zapit file {1} has {2}: load the same file in Zapit and start again.", reply, path, count));
        }
        if (reply.Value != count)
        {
            throw new InvalidOperationException(string.Format(
                "Zapit has {0} conditions loaded but the task's Zapit file {1} has {2}: load the same file in Zapit and start again.",
                reply.Value, path, count));
        }
    }
}
