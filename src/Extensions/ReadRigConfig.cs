using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

[Combinator]
[Description("Reads the rig configuration YAML for the current machine from ConfigDirectory. The file must be named <MachineName>.yml. Emits the file text once.")]
[WorkflowElementCategory(ElementCategory.Source)]
public class ReadRigConfig
{
    private string configDirectory = @"C:\RigConfigs";

    [Description("Directory containing per-hostname rig configuration YAML files.")]
    public string ConfigDirectory
    {
        get { return configDirectory; }
        set { configDirectory = value; }
    }

    public IObservable<string> Process()
    {
        return Observable.Defer(delegate
        {
            string path = System.IO.Path.Combine(configDirectory,
                System.Environment.MachineName + ".yml");
            if (!System.IO.File.Exists(path))
            {
                throw new InvalidOperationException(
                    "No rig configuration found for machine '" + System.Environment.MachineName +
                    "'. Expected file: " + System.IO.Path.GetFullPath(path));
            }
            return Observable.Return(System.IO.File.ReadAllText(path));
        });
    }
}
