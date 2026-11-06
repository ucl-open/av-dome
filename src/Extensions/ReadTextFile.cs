using System;
using System.ComponentModel;
using System.IO;
using System.Reactive.Linq;
using Bonsai;

namespace UclOpenAvDome
{
    /// <summary>
    /// Reads the whole of each file named by the input. A transform, where Bonsai.IO's
    /// ReadAllText is a source: the path comes from a sequence (TaskPath, at Go)
    /// and the file is read when that path arrives, not when the workflow starts. A relative
    /// path is from the workflow's folder, as Bonsai sets the working directory.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Reads the whole of each file named by the input, when the name arrives. A relative path is from the workflow's folder.")]
    public class ReadTextFile
    {
        public IObservable<string> Process(IObservable<string> source)
        {
            return source.Select(path => File.ReadAllText(Path.GetFullPath(path)));
        }
    }
}
