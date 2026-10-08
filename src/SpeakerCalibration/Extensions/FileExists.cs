using Bonsai;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Collections;

[Combinator]
[Description("")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class FileExists
{
    public IObservable<bool> Process(IObservable<String> source)
    {
        return source.Select(value => 
        {
            // return false;
            return System.IO.File.Exists(value);
        }
        );
    }
}
