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
public class CreateStepperMessage
{
    public IObservable<byte[]> Process(IObservable<Tuple<byte, string>> source)
    {
        return source.Select(value =>
        {
            // Convert string to byte array using ASCII encoding
            byte[] stringBytes = Encoding.ASCII.GetBytes(value.Item2);
    
            // Create result array with size = 1 + string byte length
            byte[] bytes = new byte[1 + stringBytes.Length];
    
            // Add the single byte at the beginning
            bytes[0] = value.Item1;
    
            // Copy string bytes to the result array starting at index 1
            Array.Copy(stringBytes, 0, bytes, 1, stringBytes.Length);
    
            return bytes;
        });
    }
}
