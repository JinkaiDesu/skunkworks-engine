using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

/// <summary>Only Pump invokes the Unity handler; pipe threads own transport.</summary>
internal sealed class ControlServer : IDisposable
{
    private sealed class Work
    {
        internal readonly Message Request;
        internal readonly string Owner;
        internal readonly TaskCompletionSource<Message> Done = new TaskCompletionSource<Message>();
        internal int State; // 0 queued, 1 executing, 2 completed/cancelled
        internal Work(Message request, string owner) { Request = request; Owner = owner; }
    }
    // Clients choose their own deadline (up to 300 s) and disconnect when it
    // expires; the server notices the disconnect within one probe interval.
    // This ceiling only bounds a client that stays connected but never reads.
    internal const int MaxRequestWaitMs = 300000;
    private const int ProbeIntervalMs = 50;
    private readonly string pipeName;
    private readonly ConcurrentQueue<Work> queue = new ConcurrentQueue<Work>();
    private readonly object gate = new object();
    private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
    private readonly HashSet<NamedPipeServerStream> clients = new HashSet<NamedPipeServerStream>();
    private NamedPipeServerStream waiting;
    private Thread listener;
    private int stopping, queued;
    internal readonly DispatchStatus Status;
    internal bool ListenerStopped => listener == null || !listener.IsAlive;
    internal ControlServer(string pipeName) { this.pipeName = pipeName; Status = new DispatchStatus(this); }
    internal int QueueDepth => Math.Max(0, Volatile.Read(ref queued));
    internal int ClientCount { get { lock (gate) return clients.Count; } }
    internal void Start()
    {
        if (listener != null) throw new InvalidOperationException("IPC already started.");
        listener = new Thread(AcceptLoop) { IsBackground = true, Name = "Skunkworks IPC" };
        listener.Start();
    }
    internal int Pump(Func<Message, Message> handler, int maximum)
        => Pump((owner, request) => handler(request), owner => { }, maximum);
    internal int Pump(Func<string, Message, Message> handler, Action<string> releaseOwner, int maximum,
        Func<Message, string> describe = null)
    {
        var clock = Stopwatch.StartNew();
        int count = 0;
        while (count < maximum && clock.ElapsedMilliseconds < 4 && queue.TryDequeue(out Work work))
        {
            Interlocked.Decrement(ref queued);
            if (Interlocked.CompareExchange(ref work.State, 1, 0) != 0) continue;
            long started = Stopwatch.GetTimestamp();
            try
            {
                if (work.Request == null) { releaseOwner(work.Owner); work.Done.TrySetResult(null); }
                else work.Done.TrySetResult(handler(work.Owner, work.Request));
            }
            catch (Exception error)
            {
                if (work.Request != null) work.Done.TrySetResult(Fault(work.Request, error.Message));
                else { SystemPaths.Log("ipc", "handle cleanup failed: " + error.Message); work.Done.TrySetResult(null); }
            }
            finally { Interlocked.Exchange(ref work.State, 2); }
            if (work.Request != null)
                Status.RecordExecuted(work.Request, work.Done.Task.Result, Stopwatch.GetTimestamp() - started, describe);
            count++;
        }
        Status.RecordPump(clock.Elapsed.TotalMilliseconds);
        return count;
    }
    private static Message Fault(Message request, string error) =>
        new Message(request.Id, MessageKind.Fault, Value.String(error));
    private void AcceptLoop()
    {
        while (Volatile.Read(ref stopping) == 0)
        {
            NamedPipeServerStream server = null;
            try
            {
                server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 8,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                lock (gate)
                {
                    if (stopping != 0) { server.Dispose(); return; }
                    waiting = server;
                }
                // Mono can keep a synchronous ConnectNamedPipe alive after Dispose,
                // preventing player exit until another client connects. Cancellation
                // requires an asynchronous pipe; keep the wait off Unity's thread.
                server.WaitForConnectionAsync(shutdown.Token).GetAwaiter().GetResult();
                lock (gate)
                {
                    waiting = null;
                    if (stopping != 0) { server.Dispose(); return; }
                    if (clients.Count >= 8) { server.Dispose(); continue; }
                    clients.Add(server);
                }
                var connected = server;
                ThreadPool.QueueUserWorkItem(_ => Serve(connected));
                server = null;
            }
            catch (Exception error)
            {
                if (Volatile.Read(ref stopping) != 0) return;
                SystemPaths.Log("ipc", error.Message);
                Thread.Sleep(100);
            }
            finally { server?.Dispose(); }
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(SafePipeHandle pipe, IntPtr buffer, uint size, IntPtr read,
        out uint available, IntPtr left);
    /// <summary>Observe a client hang-up without consuming its bytes.</summary>
    private static bool ClientGone(NamedPipeServerStream stream)
    {
        try
        {
            if (PeekNamedPipe(stream.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero, out _, IntPtr.Zero)) return false;
            int error = Marshal.GetLastWin32Error();
            return error == 109 || error == 232 || error == 233; // broken, closing, not connected
        }
        catch (ObjectDisposedException) { return true; }
        catch (Exception error) when (error is DllNotFoundException || error is EntryPointNotFoundException) { return false; }
    }
    private void Serve(NamedPipeServerStream stream)
    {
        string owner = Guid.NewGuid().ToString("N");
        try
        {
            bool negotiated = false;
            while (Volatile.Read(ref stopping) == 0)
            {
                Message request = Wire.Read(stream);
                if (request.Kind == MessageKind.Hello)
                {
                    if (negotiated || request.Values.Length != 1 ||
                        request.Values[0].Kind != ValueKind.String ||
                        request.Values[0].AsString() != WireSchema.Protocol)
                    {
                        Wire.Write(stream, Fault(request, "Unsupported protocol handshake."));
                        return;
                    }
                    negotiated = true;
                    Wire.Write(stream, new Message(request.Id, MessageKind.Ready,
                        Value.String(WireSchema.Protocol), Value.String(owner)));
                    continue;
                }
                if (!negotiated) { Wire.Write(stream, Fault(request, "Hello handshake required.")); return; }
                if (Interlocked.Increment(ref queued) > 256)
                {
                    Interlocked.Decrement(ref queued);
                    Wire.Write(stream, Fault(request, "IPC queue full."));
                    continue;
                }
                var work = new Work(request, owner);
                queue.Enqueue(work);
                var waited = Stopwatch.StartNew();
                while (!work.Done.Task.Wait(ProbeIntervalMs))
                {
                    if (Volatile.Read(ref stopping) != 0) return;
                    bool gone = ClientGone(stream);
                    if (!gone && waited.ElapsedMilliseconds < MaxRequestWaitMs) continue;
                    // A request that has not started never runs for an absent or
                    // expired client; running CLR work cannot be preempted.
                    bool cancelled = Interlocked.CompareExchange(ref work.State, 2, 0) == 0;
                    Status.RecordUnanswered(request, cancelled, gone);
                    if (!gone) Wire.Write(stream, Fault(request, cancelled
                        ? "Request expired before execution; it will not run."
                        : "Request still executing; do not replay mutations."));
                    return;
                }
                if (Volatile.Read(ref stopping) == 0) Wire.Write(stream, work.Done.Task.Result);
            }
        }
        catch (Exception error)
        {
            if (Volatile.Read(ref stopping) == 0 && !(error is EndOfStreamException) &&
                !(error is IOException)) SystemPaths.Log("ipc", error.Message);
        }
        finally
        {
            if (Volatile.Read(ref stopping) == 0)
            {
                Interlocked.Increment(ref queued);
                queue.Enqueue(new Work(null, owner));
            }
            lock (gate) clients.Remove(stream);
            stream.Dispose();
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        shutdown.Cancel();
        shutdown.Dispose();
        lock (gate)
        {
            try { waiting?.Dispose(); } catch { }
            foreach (var stream in clients) { try { stream.Dispose(); } catch { } }
        }
        while (queue.TryDequeue(out Work work))
        {
            Interlocked.Decrement(ref queued);
            Interlocked.Exchange(ref work.State, 2);
            work.Done.TrySetResult(work.Request == null ? null : Fault(work.Request, "Runtime stopping."));
        }
        // No synchronous native wait on Unity's teardown thread.
    }
}

/// <summary>
/// Read-only dispatch observations, registered as the "dispatch" root. Counts
/// are cumulative for the player session. Reading it goes through the same
/// queue, so a blocked main thread is reported in the log once its call returns.
/// </summary>
public sealed class DispatchStatus
{
    private sealed class Entry
    {
        internal string Request, Utc, Outcome;
        internal double Milliseconds;
    }
    private const int RecentCapacity = 16;
    private readonly ControlServer server;
    private readonly object gate = new object();
    private readonly Queue<Entry> recent = new Queue<Entry>();
    private long executed, faulted, slow, abandoned, orphaned, expired, longPumps;
    private double longestMs, maxPumpMs;
    private string longest = "";
    private int slowThresholdMs = 250;
    internal DispatchStatus(ControlServer server) { this.server = server; }

    public int QueueDepth => server.QueueDepth;
    public int Clients => server.ClientCount;
    /// <summary>Requests executed on Unity's thread (handle releases excluded).</summary>
    public long Executed => Interlocked.Read(ref executed);
    public long Faulted => Interlocked.Read(ref faulted);
    public long SlowRequests => Interlocked.Read(ref slow);
    /// <summary>Queued requests cancelled before execution because the client left or expired.</summary>
    public long Abandoned => Interlocked.Read(ref abandoned);
    /// <summary>Requests already executing when their client left; the result was discarded.</summary>
    public long Orphaned => Interlocked.Read(ref orphaned);
    /// <summary>Requests answered with a server-side expiry fault while the client stayed connected.</summary>
    public long Expired => Interlocked.Read(ref expired);
    /// <summary>Pumps whose dispatch exceeded one 60 Hz frame (16.7 ms).</summary>
    public long LongPumps => Interlocked.Read(ref longPumps);
    public double LongestMs { get { lock (gate) return longestMs; } }
    public string Longest { get { lock (gate) return longest; } }
    public double MaxPumpMs { get { lock (gate) return maxPumpMs; } }
    public int MaxRequestWaitMs => ControlServer.MaxRequestWaitMs;
    public int SlowThresholdMs
    {
        get => Volatile.Read(ref slowThresholdMs);
        set
        {
            if (value < 1 || value > 60000) throw new ArgumentOutOfRangeException(nameof(value), "1..60000 ms");
            Volatile.Write(ref slowThresholdMs, value);
        }
    }
    /// <summary>Most recent slow, abandoned or orphaned requests as JSON, oldest first.</summary>
    public string Recent()
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        var text = new StringBuilder("[");
        lock (gate)
        {
            bool first = true;
            foreach (var row in recent)
            {
                if (!first) text.Append(',');
                first = false;
                text.Append("{\"utc\":").Append(Quote(row.Utc)).Append(",\"outcome\":").Append(Quote(row.Outcome))
                    .Append(",\"ms\":").Append(row.Milliseconds.ToString("0.###", c))
                    .Append(",\"request\":").Append(Quote(row.Request)).Append('}');
            }
        }
        return text.Append(']').ToString();
    }
    public string State()
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        string longestText; double longestValue, pump;
        lock (gate) { longestText = longest; longestValue = longestMs; pump = maxPumpMs; }
        return "{\"queue_depth\":" + QueueDepth + ",\"clients\":" + Clients + ",\"executed\":" + Executed +
            ",\"faulted\":" + Faulted + ",\"slow\":" + SlowRequests + ",\"abandoned\":" + Abandoned +
            ",\"orphaned\":" + Orphaned + ",\"expired\":" + Expired + ",\"long_pumps\":" + LongPumps +
            ",\"max_pump_ms\":" + pump.ToString("0.###", c) + ",\"longest_ms\":" + longestValue.ToString("0.###", c) +
            ",\"longest\":" + Quote(longestText) + ",\"slow_threshold_ms\":" + SlowThresholdMs +
            ",\"max_request_wait_ms\":" + MaxRequestWaitMs + ",\"recent\":" + Recent() + "}";
    }

    internal void RecordExecuted(Message request, Message reply, long ticks, Func<Message, string> describe)
    {
        Interlocked.Increment(ref executed);
        bool fault = reply != null && reply.Kind == MessageKind.Fault;
        if (fault) Interlocked.Increment(ref faulted);
        double ms = ticks * 1000.0 / Stopwatch.Frequency;
        bool isLongest;
        lock (gate) isLongest = ms > longestMs;
        if (ms < SlowThresholdMs && !isLongest) return;
        string text = Describe(request, describe);
        lock (gate) if (ms > longestMs) { longestMs = ms; longest = text; }
        if (ms < SlowThresholdMs) return;
        Interlocked.Increment(ref slow);
        Remember(text, ms, fault ? "slow-fault" : "slow");
        if (ms >= 1000) SystemPaths.Log("ipc", "slow request " + ms.ToString("0") + " ms on Unity's thread: " + text);
    }
    internal void RecordUnanswered(Message request, bool cancelled, bool clientGone)
    {
        if (cancelled) Interlocked.Increment(ref abandoned); else Interlocked.Increment(ref orphaned);
        if (!clientGone) Interlocked.Increment(ref expired);
        // Pipe thread: no broker access, so only the operation kind is known here.
        string outcome = cancelled ? "abandoned" : "orphaned";
        Remember(request.Kind.ToString(), 0, outcome);
        SystemPaths.Log("ipc", request.Kind + " request " + outcome +
            (clientGone ? " after client disconnect" : " after server wait limit"));
    }
    internal void RecordPump(double ms)
    {
        if (ms > 1000.0 / 60) Interlocked.Increment(ref longPumps);
        lock (gate) if (ms > maxPumpMs) maxPumpMs = ms;
    }
    private void Remember(string request, double ms, string outcome)
    {
        lock (gate)
        {
            recent.Enqueue(new Entry { Request = request, Milliseconds = ms, Outcome = outcome, Utc = DateTime.UtcNow.ToString("o") });
            while (recent.Count > RecentCapacity) recent.Dequeue();
        }
    }
    private static string Describe(Message request, Func<Message, string> describe)
    {
        if (describe != null) { try { return describe(request); } catch { } }
        return request.Kind.ToString();
    }
    private static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        foreach (char ch in value ?? "")
        {
            if (ch == '"' || ch == '\\') text.Append('\\').Append(ch);
            else if (ch < ' ') text.Append("\\u").Append(((int)ch).ToString("x4"));
            else text.Append(ch);
        }
        return text.Append('"').ToString();
    }
}
