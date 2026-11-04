using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Bonsai;

namespace UclOpenAvDome
{
    /// <summary>
    /// Takes one device's settings out of rig.yml by the device's name. rig.yml groups each
    /// kind's devices by name (camera: box:) and binds each to its hardware; the workflow asks
    /// for the names it drives, and the name in the data is the kind and the entry joined by a
    /// hyphen (Camera-Box). An entry the workflow does not ask for is ignored. A name it asks
    /// for and rig.yml does not have stops the workflow, saying which entries the file has,
    /// where Bonsai's Index would say only that the key was not present. The output carries the
    /// name with the settings, so a device's files take their name from the same place as its
    /// lookup and the name is typed once in the workflow.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Takes one device's settings from rig.yml by its name, Kind-Instance: Camera-Box is camera: box:. Outputs the name with them, for naming the device's files. Stops the workflow, naming the entries rig.yml has, if it has none by that name.")]
    public class RigDevice
    {
        [Description("The device's name in the data: its kind and its entry in rig.yml, joined by a hyphen, such as BehaviorBoard-Main.")]
        public string Name { get; set; }

        // Dictionary rather than IDictionary: it is the generated classes' own type, which
        // Bonsai has to match to infer TDevice.
        public IObservable<NamedDevice<TDevice>> Process<TDevice>(IObservable<Dictionary<string, TDevice>> source)
        {
            var name = Name;
            return source.Select(entries => new NamedDevice<TDevice>(name, Find(entries, name)));
        }

        /// <summary>The entry named by Name, or an exception that says what rig.yml has instead.</summary>
        public static TDevice Find<TDevice>(IDictionary<string, TDevice> entries, string name)
        {
            // Each part PascalCase, letters and digits only: the name is the device's folder and
            // file names, and the logging spec reads a device's name up to the first underscore.
            var parts = (name ?? string.Empty).Split('-');
            if (parts.Length != 2 || !NamePart.IsMatch(parts[0]) || !NamePart.IsMatch(parts[1]))
            {
                throw new ArgumentException(string.Format(
                    "RigDevice's Name is '{0}'; it must be Kind-Instance, each part starting with a capital and " +
                    "holding only letters and digits, such as Camera-Box.", name));
            }

            var kind = LowerFirst(parts[0]);
            var entry = LowerFirst(parts[1]);
            TDevice device;
            if (entries != null && entries.TryGetValue(entry, out device))
            {
                return device;
            }

            // string.Format, not $"...": Bonsai compiles Extensions with the framework's csc, which is C# 5.
            var has = entries == null || entries.Count == 0
                ? string.Format("Its {0} section has no entries.", kind)
                : string.Format("Its {0} entries: {1}.", kind, string.Join(", ", entries.Keys.OrderBy(k => k, StringComparer.Ordinal)));
            throw new KeyNotFoundException(string.Format(
                "rig.yml has no {0} named '{1}', which this workflow records as {2}. {3} " +
                "Add {1} under {0}: in this rig's rig.yml, or correct the name there.",
                kind, entry, name, has));
        }

        static readonly Regex NamePart = new Regex("^[A-Z][A-Za-z0-9]*$");

        static string LowerFirst(string text)
        {
            return char.ToLowerInvariant(text[0]) + text.Substring(1);
        }
    }

    /// <summary>A device's name in the data, Kind-Instance, and its settings from rig.yml.</summary>
    public class NamedDevice<TDevice>
    {
        public NamedDevice(string name, TDevice settings)
        {
            Name = name;
            Settings = settings;
        }

        [Description("The device's name in the data, such as Camera-Box: its folder and the start of its files' names.")]
        public string Name { get; private set; }

        [Description("The device's entry in rig.yml.")]
        public TDevice Settings { get; private set; }
    }
}
