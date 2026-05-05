using Bonsai;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using OpenTK;
using Bonsai.Design;

[Combinator]
[DefaultProperty("Locations")]
[Description("Returns the index of the closest location for each position in the input sequence.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class SpatialLookup
{
    [TypeConverter(typeof(NumericRecordArrayConverter))]
    [Description("The spatial locations used to match against positions in the input sequence.")]
    public Vector3[] Locations { get; set; }

    bool TryGetClosestLocation(Vector3 point, out int index)
    {
        int? closestIndex = null;
        float minDistance = float.MaxValue;
        var locations = Locations;
        for (int i = 0; locations != null && i < locations.Length; i++)
        {
            var distance = (locations[i] - point).LengthSquared;
            if (closestIndex == null || distance < minDistance)
            {
                closestIndex = i;
                minDistance = distance;
            }
        }

        index = closestIndex.GetValueOrDefault();
        return closestIndex.HasValue;
    }

    public IObservable<int> Process(IObservable<Vector2> source)
    {
        return source.Select(value =>
        {
            int index;
            TryGetClosestLocation(new Vector3(value.X, value.Y, 0), out index);
            return index;
        });
    }

    public IObservable<int> Process(IObservable<Vector3> source)
    {
        return source.Select(value =>
        {
            int index;
            TryGetClosestLocation(value, out index);
            return index;
        });
    }
    
    class NumericRecordArrayConverter : ArrayConverter
    {
        public override PropertyDescriptorCollection GetProperties(ITypeDescriptorContext context, object value, Attribute[] attributes)
        {
            return new PropertyDescriptorCollection(base.GetProperties(context, value, attributes).Cast<PropertyDescriptor>().Select(descriptor =>
            {
                return new DynamicPropertyDescriptor(
                    descriptor.Name,
                    descriptor.PropertyType,
                    c => descriptor.GetValue(c),
                    (c, v) => descriptor.SetValue(c, v),
                    new TypeConverterAttribute(typeof(NumericRecordConverter)));
            }).ToArray());
        }
    }
}
