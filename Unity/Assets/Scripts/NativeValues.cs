using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;

// Generic CLR data construction. World algorithms stay in their own capabilities.
// Arrays returned here retain CLR identity through the existing reflection broker.
public sealed class NativeValues
{
    private readonly Func<Value, Type, object> decode;
    public NativeValues(Func<Value, Type, object> decodeValue) { decode = decodeValue; }

    private static Value[] Contents(byte[] frame)
    {
        Message message = Wire.Unpack(frame);
        if (message.Kind != MessageKind.Result) throw new InvalidDataException("Expected an inert value frame.");
        return message.Values;
    }

    public Array CreateArray(Type elementType, byte[] frame)
    {
        if (elementType == null || elementType.IsByRef || elementType.IsPointer || elementType.ContainsGenericParameters)
            throw new ArgumentException("A concrete array element type is required.");
        Value[] values = Contents(frame);
        Array result = Array.CreateInstance(elementType, values.Length);
        for (int i = 0; i < values.Length; ++i) result.SetValue(decode(values[i], elementType), i);
        return result;
    }

    public object CreateRecord(Type type, byte[] frame)
    {
        if (type == null || type.IsAbstract || type.IsInterface || type.IsArray || type.IsPrimitive || type.IsEnum || type.ContainsGenericParameters)
            throw new ArgumentException("A concrete CLR data record type is required.");
        Value[] values = Contents(frame);
        if (values.Length % 2 != 0) throw new InvalidDataException("Record fields need names and values.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var members = new List<MemberInfo>();
        var decoded = new List<object>();
        for (int i = 0; i < values.Length; i += 2)
        {
            string name = values[i].AsString();
            if (!seen.Add(name)) throw new InvalidDataException("Duplicate record field: " + name);
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null && !field.IsInitOnly)
            { members.Add(field); decoded.Add(decode(values[i + 1], field.FieldType)); }
            else if (property != null && property.GetIndexParameters().Length == 0 && property.GetSetMethod() != null)
            { members.Add(property); decoded.Add(decode(values[i + 1], property.PropertyType)); }
            else throw new InvalidDataException("No writable public record field: " + name);
        }
        // Validate all fields before invoking the record's constructor/setters.
        object result = Activator.CreateInstance(type);
        for (int i = 0; i < members.Count; ++i)
            if (members[i] is FieldInfo field) field.SetValue(result, decoded[i]);
            else ((PropertyInfo)members[i]).SetValue(result, decoded[i], null);
        return result;
    }

    // Preparation runs off Unity's thread. The returned Task is an inspectable
    // request; IsCompleted means finished, while Result either yields the retained
    // typed array or reports validation failure. It never activates a world.
    public Task<Array> BeginReadArray(Type elementType, string path, int count, string sha256)
    {
        int width = Width(elementType);
        long length = checked((long)count * width);
        if (count < 0 || length > 256L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(count));
        if (sha256 == null || sha256.Length != 64) throw new ArgumentException("An exact SHA-256 digest is required.");
        foreach (char c in sha256) if (!Uri.IsHexDigit(c)) throw new ArgumentException("Invalid SHA-256 digest.");
        string fullPath = Path.GetFullPath(path);
        return Task.Run(() =>
        {
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("The buffer format is little endian.");
            byte[] bytes = new byte[(int)length];
            using (var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length != length) throw new InvalidDataException("Buffer size does not match count and element type.");
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = input.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw new EndOfStreamException("Incomplete buffer.");
                    offset += read;
                }
                if (input.ReadByte() != -1) throw new InvalidDataException("Buffer changed during preparation.");
            }
            string actual;
            using (var hash = SHA256.Create()) actual = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Buffer SHA-256 mismatch.");
            Array result = Array.CreateInstance(elementType, count);
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        });
    }

    private static int Width(Type type)
    {
        if (type == typeof(byte) || type == typeof(sbyte)) return 1;
        if (type == typeof(short) || type == typeof(ushort)) return 2;
        if (type == typeof(int) || type == typeof(uint) || type == typeof(float)) return 4;
        if (type == typeof(long) || type == typeof(ulong) || type == typeof(double)) return 8;
        throw new ArgumentException("Binary arrays require a fixed-width numeric element type; structured layouts belong to a typed capability.");
    }
}
