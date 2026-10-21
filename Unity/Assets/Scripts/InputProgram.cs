using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

// Unity-independent contract. No terminal, Bash, binding, or game semantics.
public enum InputKind
{
    KeyDown, KeyUp, Text, Composition,
    PointerDown, PointerUp, PointerMove, Scroll,
    Focus, Device, Reset, Signal
}

[Flags]
public enum InputModifiers
{
    None = 0, Shift = 1, Control = 2, Alt = 4, Meta = 8, RightAlt = 16
}

public sealed class InputSample
{
    public long Sequence { get; internal set; }
    public InputKind Kind { get; }
    public double Time { get; }
    public int Device { get; }
    public string Control { get; }
    public string DisplayName { get; }
    public string Text { get; }
    public InputModifiers Modifiers { get; }
    public bool Focused { get; }
    public float X { get; }
    public float Y { get; }
    public float DeltaX { get; }
    public float DeltaY { get; }

    internal InputSample(InputKind kind, double time, int device = 0,
        string control = "", string displayName = "", string text = "",
        InputModifiers modifiers = InputModifiers.None, bool focused = true,
        float x = 0, float y = 0, float deltaX = 0, float deltaY = 0)
    {
        Kind = kind; Time = time; Device = device;
        Control = control ?? ""; DisplayName = displayName ?? "";
        Text = text ?? ""; Modifiers = modifiers; Focused = focused;
        X = x; Y = y; DeltaX = deltaX; DeltaY = deltaY;
    }

    // Serialization is only for external inspection, never the in-process hot path.
    public string ToJson()
    {
        return "{\"sequence\":" + Sequence.ToString(CultureInfo.InvariantCulture) +
            ",\"kind\":" + Quote(Kind.ToString()) +
            ",\"time\":" + Number(Time) + ",\"device\":" + Device.ToString(CultureInfo.InvariantCulture) +
            ",\"control\":" + Quote(Control) + ",\"displayName\":" + Quote(DisplayName) +
            ",\"text\":" + Quote(Text) + ",\"modifiers\":" + ((int)Modifiers).ToString(CultureInfo.InvariantCulture) +
            ",\"focused\":" + (Focused ? "true" : "false") +
            ",\"x\":" + Number(X) + ",\"y\":" + Number(Y) +
            ",\"dx\":" + Number(DeltaX) + ",\"dy\":" + Number(DeltaY) + "}";
    }

    private static string Number(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? "null" : value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string Quote(string value)
    {
        var b = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                default:
                    if (c < 32 || char.IsSurrogate(c))
                        b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else b.Append(c);
                    break;
            }
        }
        return b.Append('"').ToString();
    }
}

public sealed class InputProgram
{
    private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
    private readonly int capacity;
    private readonly Queue<InputSample> pending = new Queue<InputSample>();
    private readonly Queue<InputSample> history = new Queue<InputSample>();
    private readonly Dictionary<string, Listener> listeners =
        new Dictionary<string, Listener>(StringComparer.Ordinal);
    private readonly Dictionary<int, HashSet<string>> held =
        new Dictionary<int, HashSet<string>>();
    private long legacyReadSequence;

    public bool Capturing { get; internal set; }
    public bool Focused { get; private set; }
    public int KeyboardCount { get; internal set; }
    public long Sequence { get; private set; }
    public long Dropped { get; private set; }
    public long HistoryEvictions { get; private set; }
    public int Count => pending.Count;
    public int SubscriberCount => listeners.Count;
    public long OldestSequence => history.Count == 0 ? Sequence + 1 : history.Peek().Sequence;
    public string LastSubscriberError { get; private set; } = "";

    public InputProgram(int capacity = 4096)
    {
        if (capacity < 8) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    // Named subscriptions support replacing a hot-loaded consumer without duplicate delivery.
    // Disposing an OLD token cannot unsubscribe its replacement.
    public IDisposable Observe(string owner, Action<InputSample> receive)
    {
        CheckThread();
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Owner is required.", nameof(owner));
        if (receive == null) throw new ArgumentNullException(nameof(receive));
        if (listeners.TryGetValue(owner, out Listener old)) old.Dispose();
        var item = new Listener(this, owner, Sequence + 1, receive);
        listeners.Add(owner, item);
        return item;
    }

    public bool IsHeld(int device, string control)
    {
        CheckThread();
        return held.TryGetValue(device, out HashSet<string> keys) && keys.Contains(control);
    }

    public string Describe()
    {
        CheckThread();
        return "input backend=Unity.InputSystem capturing=" + Capturing +
            " focused=" + Focused + " keyboards=" + KeyboardCount +
            " subscribers=" + SubscriberCount + " queued=" + Count +
            " sequence=" + Sequence + " dropped=" + Dropped;
    }

    // Non-destructive, sequence-addressed history. Inspection cannot steal console input.
    // Check OldestSequence to detect that a reader has fallen behind the bounded journal.
    public string Read(long afterSequence, int maximum)
    {
        CheckThread();
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        var b = new StringBuilder();
        int count = 0;
        foreach (InputSample item in history)
        {
            if (item.Sequence <= afterSequence) continue;
            if (count++ != 0) b.Append('\n');
            b.Append(item.ToJson());
            if (count >= maximum) break;
        }
        return b.ToString();
    }

    // Compatibility inspection cursor; it does not drain the delivery queue.
    public string Drain(int maximum)
    {
        CheckThread();
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        var b = new StringBuilder();
        int count = 0;
        foreach (InputSample item in history)
        {
            if (item.Sequence <= legacyReadSequence) continue;
            if (count++ != 0) b.Append('\n');
            b.Append(item.ToJson());
            legacyReadSequence = item.Sequence;
            if (count >= maximum) break;
        }
        return b.ToString();
    }

    internal void Post(InputSample item)
    {
        CheckThread();
        if (pending.Count >= capacity)
        {
            // Losing a key-up must not leave consumers repeating forever.
            Dropped += pending.Count;
            pending.Clear();
            held.Clear();
            Append(new InputSample(InputKind.Reset, item.Time, text: "queue overflow", focused: Focused));
        }

        if (item.Kind == InputKind.Focus) Focused = item.Focused;
        if (item.Kind == InputKind.Reset || (item.Kind == InputKind.Focus && !item.Focused)) held.Clear();
        if (item.Kind == InputKind.KeyDown)
        {
            if (!held.TryGetValue(item.Device, out HashSet<string> downKeys))
            {
                downKeys = new HashSet<string>(StringComparer.Ordinal);
                held.Add(item.Device, downKeys);
            }
            downKeys.Add(item.Control);
        }
        else if (item.Kind == InputKind.KeyUp && held.TryGetValue(item.Device, out HashSet<string> upKeys))
        {
            upKeys.Remove(item.Control);
        }
        Append(item);
    }

    internal void Reset(double time, string reason)
    {
        CheckThread();
        Dropped += pending.Count;
        pending.Clear();
        Post(new InputSample(InputKind.Reset, time, text: reason, focused: Focused));
    }

    // Main.Update calls this. No consumer runs inside InputSystem.onEvent.
    public int Pump(int maximum = 4096)
    {
        CheckThread();
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (pending.Count == 0) return 0;
        var snapshot = new List<Listener>(listeners.Values);
        int processed = 0;
        while (processed < maximum && pending.Count != 0)
        {
            InputSample item = pending.Dequeue();
            foreach (Listener listener in snapshot)
            {
                if (!listener.Active || item.Sequence < listener.FirstSequence) continue;
                try { listener.Receive(item); }
                catch (Exception failure)
                {
                    LastSubscriberError = listener.Owner + ": " + failure;
                    listener.Dispose(); // isolate a broken extension; do not kill raw input
                }
            }
            processed++;
        }
        return processed;
    }

    private void Append(InputSample item)
    {
        item.Sequence = ++Sequence;
        pending.Enqueue(item);
        if (history.Count == capacity) { history.Dequeue(); HistoryEvictions++; }
        history.Enqueue(item);
    }

    private void CheckThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != ownerThread)
            throw new InvalidOperationException("InputProgram is owned by Unity's main thread.");
    }

    private sealed class Listener : IDisposable
    {
        private readonly InputProgram program;
        public readonly string Owner;
        public readonly long FirstSequence;
        public readonly Action<InputSample> Receive;
        public bool Active = true;
        public Listener(InputProgram program, string owner, long firstSequence, Action<InputSample> receive)
        { this.program = program; Owner = owner; FirstSequence = firstSequence; Receive = receive; }
        public void Dispose()
        {
            program.CheckThread();
            if (!Active) return;
            Active = false;
            if (program.listeners.TryGetValue(Owner, out Listener current) && ReferenceEquals(current, this))
                program.listeners.Remove(Owner);
        }
    }
}

// Keyboard.onTextInput supplies UTF-16 code units. Never UTF-8 encode half a pair.
internal sealed class InputTextAccumulator
{
    private char high;
    public void Clear() { high = '\0'; }
    public string Push(char value)
    {
        if (char.IsHighSurrogate(value))
        {
            string replacement = high == '\0' ? "" : "\uFFFD";
            high = value;
            return replacement;
        }
        if (char.IsLowSurrogate(value))
        {
            if (high == '\0') return "\uFFFD";
            string pair = new string(new[] { high, value });
            high = '\0';
            return pair;
        }
        string prefix = high == '\0' ? "" : "\uFFFD";
        high = '\0';
        return prefix + value;
    }
}
