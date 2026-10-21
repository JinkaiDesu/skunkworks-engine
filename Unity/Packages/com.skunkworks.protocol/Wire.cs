using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public sealed class Value
{
    public ValueKind Kind { get; private set; }

    private readonly object raw;

    public object Raw
    {
        get { return raw; }
    }

    private Value(
        ValueKind kind,
        object raw)
    {
        Kind = kind;
        this.raw = raw;
    }


    public static Value Null()
    {
        return new Value(
            ValueKind.Null,
            null
        );
    }


    public static Value Boolean(bool value)
    {
        return new Value(
            ValueKind.Boolean,
            value
        );
    }


    public static Value Int32(int value)
    {
        return new Value(
            ValueKind.Int32,
            value
        );
    }


    public static Value Int64(long value)
    {
        return new Value(
            ValueKind.Int64,
            value
        );
    }


    public static Value Single(float value)
    {
        return new Value(
            ValueKind.Single,
            value
        );
    }


    public static Value Double(double value)
    {
        return new Value(
            ValueKind.Double,
            value
        );
    }


    public static Value String(string value)
    {
        if (value == null)
        {
            throw new ArgumentNullException(
                nameof(value)
            );
        }

        return new Value(
            ValueKind.String,
            value
        );
    }


    public static Value Object(long reference)
    {
        return new Value(
            ValueKind.Object,
            reference
        );
    }


    public static Value TypeRef(long reference)
    {
        return new Value(
            ValueKind.Type,
            reference
        );
    }


    public static Value Bytes(byte[] value)
    {
        if (value == null)
        {
            throw new ArgumentNullException(
                nameof(value)
            );
        }

        return new Value(
            ValueKind.Bytes,
            value
        );
    }


    public static Value BatchRef(int index)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index)
            );
        }

        return new Value(
            ValueKind.Batch,
            index
        );
    }


    public bool AsBoolean()
    {
        Require(ValueKind.Boolean);
        return (bool)raw;
    }


    public int AsInt32()
    {
        Require(ValueKind.Int32);
        return (int)raw;
    }


    public long AsInt64()
    {
        Require(ValueKind.Int64);
        return (long)raw;
    }


    public float AsSingle()
    {
        Require(ValueKind.Single);
        return (float)raw;
    }


    public double AsDouble()
    {
        Require(ValueKind.Double);
        return (double)raw;
    }


    public string AsString()
    {
        Require(ValueKind.String);
        return (string)raw;
    }


    public long AsObject()
    {
        Require(ValueKind.Object);
        return (long)raw;
    }


    public long AsType()
    {
        Require(ValueKind.Type);
        return (long)raw;
    }


    public byte[] AsBytes()
    {
        Require(ValueKind.Bytes);
        return (byte[])raw;
    }


    public int AsBatch()
    {
        Require(ValueKind.Batch);
        return (int)raw;
    }


    private void Require(
        ValueKind expected)
    {
        if (Kind != expected)
        {
            throw new InvalidDataException(
                "Expected " +
                expected +
                ", got " +
                Kind +
                "."
            );
        }
    }
}


public sealed class Message
{
    public long Id { get; private set; }

    public MessageKind Kind { get; private set; }

    public Value[] Values { get; private set; }


    public Message(
        long id,
        MessageKind kind,
        params Value[] values)
    {
        Id = id;
        Kind = kind;
        Values =
            values ??
            Array.Empty<Value>();
    }
}


public static class Wire
{
    // Reflection traffic should usually be tiny.
    // Bytes/Batches may be large.
    private const int MaxFrame =
        WireSchema.MaxFrame;

    private const int MaxValues =
        WireSchema.MaxValues;


    public static void Write(
        Stream stream,
        Message message)
    {

        byte[] body =
            Pack(message);

        using (
            var writer =
                new BinaryWriter(
                    stream,
                    Encoding.UTF8,
                    true
                )
        )
        {
            writer.Write(
                body.Length
            );

            writer.Write(
                body
            );

            writer.Flush();
        }
    }


    public static Message Read(
        Stream stream)
    {
        using (
            var reader =
                new BinaryReader(
                    stream,
                    Encoding.UTF8,
                    true
                )
        )
        {
            int length =
                reader.ReadInt32();

            if (
                length < 0 ||
                length > MaxFrame
            )
            {
                throw new InvalidDataException(
                    "Invalid wire frame length."
                );
            }


        byte[] body =
                reader.ReadBytes(
                    length
                );

            if (
                body.Length !=
                length
            )
            {
                throw new EndOfStreamException();
            }

            return Unpack(
                body
            );
        }
    }


    public static byte[] Pack(
        Message message)
    {
        if (message == null || message.Values.Length > WireSchema.MaxValues ||
            !Enum.IsDefined(typeof(MessageKind), message.Kind))
            throw new InvalidDataException("Invalid message kind or value count.");

        using (
            var memory =
                new MemoryStream()
        )
        using (
            var writer =
                new BinaryWriter(
                    memory,
                    Encoding.UTF8,
                    true
                )
        )
        {
            writer.Write(
                (byte)message.Kind
            );

            writer.Write(
                message.Id
            );

            writer.Write(
                message.Values.Length
            );

            for (
                int i = 0;
                i < message.Values.Length;
                i++
            )
            {
                WriteValue(
                    writer,
                    message.Values[i]
                );
            }

            writer.Flush();

            if (
                memory.Length >
                MaxFrame
            )
            {
                throw new InvalidDataException(
                    "Wire frame too large."
                );
            }

            return memory.ToArray();
        }
    }


    public static Message Unpack(
        byte[] body)
    {
        if (body == null)
        {
            throw new ArgumentNullException(
                nameof(body)
            );
        }

        if (
            body.Length >
            MaxFrame
        )
        {
            throw new InvalidDataException(
                "Wire frame too large."
            );
        }

        var reader = new BodyReader(body);
        MessageKind kind = (MessageKind)reader.ReadByte();
        if (!Enum.IsDefined(typeof(MessageKind), kind))
            throw new InvalidDataException("Unknown message kind.");
        long id = reader.ReadInt64();
        int count = reader.ReadInt32();
        if (count < 0 || count > MaxValues)
            throw new InvalidDataException("Invalid wire value count.");
        var values = new Value[count];
        for (int i = 0; i < count; i++) values[i] = ReadValue(ref reader);
        if (reader.Position != body.Length)
            throw new InvalidDataException("Trailing bytes in wire frame.");
        return new Message(id, kind, values);
    }


    private static void WriteValue(
        BinaryWriter writer,
        Value value)
    {
        writer.Write(
            (byte)value.Kind
        );

        switch (value.Kind)
        {
            case ValueKind.Null:
                return;

            case ValueKind.Boolean:
                writer.Write(
                    value.AsBoolean()
                );
                return;

            case ValueKind.Int32:
                writer.Write(
                    value.AsInt32()
                );
                return;

            case ValueKind.Int64:
                writer.Write(
                    value.AsInt64()
                );
                return;

            case ValueKind.Single:
                writer.Write(
                    value.AsSingle()
                );
                return;

            case ValueKind.Double:
                writer.Write(
                    value.AsDouble()
                );
                return;

            case ValueKind.String:
                WriteString(
                    writer,
                    value.AsString()
                );
                return;

            case ValueKind.Object:
                writer.Write(
                    value.AsObject()
                );
                return;

            case ValueKind.Type:
                writer.Write(
                    value.AsType()
                );
                return;

            case ValueKind.Bytes:
            {
                byte[] bytes =
                    value.AsBytes();

                writer.Write(
                    bytes.Length
                );

                writer.Write(
                    bytes
                );

                return;
            }

            case ValueKind.Batch:
                writer.Write(
                    value.AsBatch()
                );
                return;

            default:
                throw new InvalidDataException(
                    "Unknown wire value kind."
                );
        }
    }


    private static Value ReadValue(
        ref BodyReader reader)
    {
        ValueKind kind =
            (ValueKind)
                reader.ReadByte();

        switch (kind)
        {
            case ValueKind.Null:
                return Value.Null();

            case ValueKind.Boolean:
                return Value.Boolean(
                    reader.ReadBoolean()
                );

            case ValueKind.Int32:
                return Value.Int32(
                    reader.ReadInt32()
                );

            case ValueKind.Int64:
                return Value.Int64(
                    reader.ReadInt64()
                );

            case ValueKind.Single:
                return Value.Single(
                    reader.ReadSingle()
                );

            case ValueKind.Double:
                return Value.Double(
                    reader.ReadDouble()
                );

            case ValueKind.String:
                return Value.String(
                    reader.ReadString()
                );

            case ValueKind.Object:
                return Value.Object(
                    reader.ReadInt64()
                );

            case ValueKind.Type:
                return Value.TypeRef(
                    reader.ReadInt64()
                );

            case ValueKind.Bytes:
            {
                int length =
                    reader.ReadInt32();

                if (
                    length < 0 ||
                    length > MaxFrame
                )
                {
                    throw new InvalidDataException(
                        "Invalid byte array length."
                    );
                }

                byte[] bytes =
                    reader.ReadBytes(
                        length
                    );

                if (
                    bytes.Length !=
                    length
                )
                {
                    throw new EndOfStreamException();
                }

                return Value.Bytes(
                    bytes
                );
            }

            case ValueKind.Batch:
                return Value.BatchRef(
                    reader.ReadInt32()
                );

            default:
                throw new InvalidDataException(
                    "Unknown wire value kind."
                );
        }
    }


    private static void WriteString(
        BinaryWriter writer,
        string value)
    {
        byte[] bytes =
            Encoding.UTF8.GetBytes(
                value
            );

        if (
            bytes.Length >
            MaxFrame
        )
        {
            throw new InvalidDataException(
                "String too large."
            );
        }

        writer.Write(
            bytes.Length
        );

        writer.Write(
            bytes
        );
    }


    // The body is already a bounded buffer. Decode it directly instead of
    // allocating a stream, reader, and a temporary byte[] for every string.
    private struct BodyReader {
        private readonly byte[] body;
        public int Position { get; private set; }

        public BodyReader(byte[] body) { this.body = body; Position = 0; }

        private void Require(int length) {
            if (length > body.Length - Position) throw new EndOfStreamException();
        }

        private void RequirePrimitive(int length) {
            if (length <= body.Length - Position) return;
            // BinaryReader supplies the platform's original truncated-scalar
            // exception/message. This allocation is only on malformed input.
            using (var memory = new MemoryStream(body, false))
            using (var reader = new BinaryReader(memory, Encoding.UTF8, true)) {
                memory.Position = Position;
                if (length == 1) reader.ReadByte();
                else if (length == 4) reader.ReadInt32();
                else reader.ReadInt64();
            }
        }

        public byte ReadByte() { RequirePrimitive(1); return body[Position++]; }
        public bool ReadBoolean() { return ReadByte() != 0; }

        public int ReadInt32() {
            RequirePrimitive(4);
            int offset = Position; Position += 4;
            return body[offset] | body[offset + 1] << 8 |
                   body[offset + 2] << 16 | body[offset + 3] << 24;
        }

        public long ReadInt64() {
            RequirePrimitive(8);
            uint low = unchecked((uint)ReadInt32()), high = unchecked((uint)ReadInt32());
            return unchecked((long)((ulong)low | (ulong)high << 32));
        }

        public float ReadSingle() { return new SingleBits { Bits = ReadInt32() }.Value; }
        public double ReadDouble() { return BitConverter.Int64BitsToDouble(ReadInt64()); }

        public byte[] ReadBytes(int length) {
            Require(length);
            if (length == 0) return Array.Empty<byte>();
            var bytes = new byte[length];
            Buffer.BlockCopy(body, Position, bytes, 0, length); Position += length;
            return bytes;
        }

        public string ReadString() {
            int length = ReadInt32();
            if (length < 0 || length > MaxFrame)
                throw new InvalidDataException("Invalid string length.");
            Require(length);
            string value = Encoding.UTF8.GetString(body, Position, length);
            Position += length;
            return value;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct SingleBits {
        [FieldOffset(0)] public int Bits;
        [FieldOffset(0)] public float Value;
    }
}
