using System;
using System.Collections.Generic;
using System.Threading;

// Unity-independent. Device facts only: no bindings, no game semantics, no consumer.
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
}

// The host's foreign-input boundary: a bounded journal of what the devices did, in the order it happened.
// Nothing is delivered. A reader keeps the last sequence it saw and asks for what came after, at its own
// pace; OldestSequence tells it when the journal has moved past it. A Reset sample marks a discontinuity
// (focus change, device loss, capture stop): keys held before it may never report their release.
public sealed class InputProgram
{
    private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
    private readonly int capacity;
    private readonly Queue<InputSample> history = new Queue<InputSample>();

    public bool Capturing { get; internal set; }
    public bool Focused { get; private set; }
    public int KeyboardCount { get; internal set; }
    public long Sequence { get; private set; }
    public long HistoryEvictions { get; private set; }
    public long OldestSequence => history.Count == 0 ? Sequence + 1 : history.Peek().Sequence;

    public InputProgram(int capacity = 4096)
    {
        if (capacity < 8) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public string Describe()
    {
        CheckThread();
        return "input backend=Unity.InputSystem capturing=" + Capturing +
            " focused=" + Focused + " keyboards=" + KeyboardCount +
            " sequence=" + Sequence + " oldest=" + OldestSequence +
            " evicted=" + HistoryEvictions;
    }

    // Appends the samples after afterSequence to destination, oldest first, and returns how many.
    // Reading takes nothing away: every reader sees the same journal.
    public int Read(long afterSequence, List<InputSample> destination, int maximum)
    {
        CheckThread();
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        int count = 0;
        foreach (InputSample item in history)
        {
            if (item.Sequence <= afterSequence) continue;
            destination.Add(item);
            if (++count >= maximum) break;
        }
        return count;
    }

    internal void Post(InputSample item)
    {
        CheckThread();
        if (item.Kind == InputKind.Focus) Focused = item.Focused;
        item.Sequence = ++Sequence;
        if (history.Count == capacity) { history.Dequeue(); HistoryEvictions++; }
        history.Enqueue(item);
    }

    internal void Reset(double time, string reason)
    {
        Post(new InputSample(InputKind.Reset, time, text: reason, focused: Focused));
    }

    private void CheckThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != ownerThread)
            throw new InvalidOperationException("InputProgram is owned by Unity's main thread.");
    }
}

// Keyboard.onTextInput supplies UTF-16 code units. Never pass on half a pair.
internal sealed class InputTextAccumulator
{
    private char high;
    public void Clear() { high = '\0'; }
    public string Push(char value)
    {
        if (char.IsHighSurrogate(value))
        {
            string replacement = high == '\0' ? "" : "�";
            high = value;
            return replacement;
        }
        if (char.IsLowSurrogate(value))
        {
            if (high == '\0') return "�";
            string pair = new string(new[] { high, value });
            high = '\0';
            return pair;
        }
        string prefix = high == '\0' ? "" : "�";
        high = '\0';
        return prefix + value;
    }
}
