using Bonsai;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using Harp.Behavior;

[Combinator]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CreateDigitalOutputs
{
    
    public IObservable<DigitalOutputs> Process(IObservable<List<string>> source)
{
    return source.Select(list =>
    {
        DigitalOutputs outputs = DigitalOutputs.None;

        foreach (var token in list)
        {
            outputs |= (DigitalOutputs)Enum.Parse(
                typeof(DigitalOutputs),
                token.Trim()
            );
        }

        return outputs;
    });
}
}
