using Bonsai;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text;

[Combinator]
[Description("")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class ConvertByteArrayToString
{
    public IObservable<string> Process(IObservable<byte[]> source)
    {
        return source.Select(value =>
        {
            string String = Encoding.ASCII.GetString(value);
            return String;
        });
    }
}
