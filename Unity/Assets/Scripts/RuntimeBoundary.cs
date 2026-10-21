using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Xml.Linq;

/// <summary>Experimental v0.3-beta activation and coordinated checkpoint boundary.</summary>
public sealed class RuntimeBoundary : IDisposable
{
    public const string ApiVersion = "0.3-beta";
    private readonly object gate = new object();
    private readonly Queue<Work> pending = new Queue<Work>();
    private readonly Dictionary<string, BoundaryReceipt> receipts = new Dictionary<string, BoundaryReceipt>();
    private readonly Dictionary<string, BoundaryReceipt> latest = new Dictionary<string, BoundaryReceipt>();
    private readonly Dictionary<string, Source> sources = new Dictionary<string, Source>();
    private bool stopped;
    public long Tick { get; private set; }
    private sealed class Work { public BoundaryReceipt Receipt; public Action<long> Apply; }
    private sealed class Source { public string Id; public int Version; public Func<byte[]> Capture; }

    public BoundaryReceipt StageCall(string slot, string revision, object target, string method)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        var type = target as Type ?? target.GetType();
        var info = type.GetMethod(method, BindingFlags.Public | (target is Type ? BindingFlags.Static : BindingFlags.Instance), null, Type.EmptyTypes, null);
        if (info == null || info.ContainsGenericParameters || typeof(Task).IsAssignableFrom(info.ReturnType)) throw new ArgumentException("Activation needs a synchronous public parameterless method.");
        return Enqueue(slot, revision, (receipt, tick) => receipt.Applied(tick, info.Invoke(target is Type ? null : target, null)));
    }

    public BoundaryReceipt StageSet(string slot, string revision, object target, string member, object value)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        var type = target as Type ?? target.GetType();
        var flags = BindingFlags.Public | (target is Type ? BindingFlags.Static : BindingFlags.Instance);
        var property = type.GetProperty(member, flags); var field = type.GetField(member, flags);
        Type destination = property != null && property.GetSetMethod() != null && property.GetIndexParameters().Length == 0 ? property.PropertyType :
            field != null && !field.IsInitOnly && !field.IsLiteral ? field.FieldType : null;
        if (destination == null) throw new ArgumentException("Activation needs a writable public member.");
        if (value == null ? destination.IsValueType && Nullable.GetUnderlyingType(destination) == null : !destination.IsInstanceOfType(value))
            throw new ArgumentException("Prepare the exact CLR value type before staging.");
        return Enqueue(slot, revision, (receipt, tick) => {
            if (property != null && property.GetSetMethod() != null) property.SetValue(target is Type ? null : target, value, null);
            else field.SetValue(target is Type ? null : target, value);
            receipt.Applied(tick, value);
        });
    }

    public void RegisterCheckpointSource(string identity, int schemaVersion, object target, string captureMethod)
    {
        Identifier(identity);
        if (schemaVersion < 1 || target == null) throw new ArgumentException("A source needs a positive schema version and live target.");
        var method = target.GetType().GetMethod(captureMethod, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (method == null || method.ReturnType != typeof(byte[])) throw new ArgumentException("Capture must be a public parameterless byte[] method.");
        lock (gate) {
            if (stopped) throw new ObjectDisposedException(nameof(RuntimeBoundary));
            if (sources.ContainsKey(identity) || sources.Count >= 64) throw new InvalidOperationException("Duplicate source or 64-source limit.");
            sources.Add(identity, new Source { Id = identity, Version = schemaVersion, Capture = () => (byte[])method.Invoke(target, null) });
        }
    }
    public void UnregisterCheckpointSource(string identity) { lock (gate) sources.Remove(identity); }

    public BoundaryReceipt Checkpoint(string revision, string directory)
    {
        string root = Path.GetFullPath(directory);
        Source[] capturedSources;
        lock (gate) capturedSources = sources.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        if (capturedSources.Length == 0) throw new InvalidOperationException("Register world-owned checkpoint sources first.");
        return Enqueue("checkpoint:" + root.ToUpperInvariant(), revision, (receipt, tick) => {
            var parts = new List<CheckpointPart>(); long size = 0;
            foreach (var source in capturedSources) {
                byte[] data = source.Capture() ?? throw new InvalidDataException("Capture returned null: " + source.Id);
                size += data.LongLength;
                if (size > 256L * 1024 * 1024) throw new InvalidDataException("Checkpoint exceeds 256 MiB.");
                parts.Add(new CheckpointPart(source.Id, source.Version, (byte[])data.Clone()));
            }
            receipt.Writing(tick);
            // Only immutable copies leave the Unity thread. Publication is guarded
            // against newer revisions and host shutdown; failed writes keep current.
            Task.Run(() => Publish(root, revision, tick, parts, receipt));
        });
    }

    private BoundaryReceipt Enqueue(string slot, string revision, Action<BoundaryReceipt, long> apply)
    {
        if (string.IsNullOrWhiteSpace(slot) || string.IsNullOrWhiteSpace(revision) || slot.Length > 4096 || revision.Length > 256)
            throw new ArgumentException("A bounded slot and revision are required.");
        lock (gate) {
            if (stopped) throw new ObjectDisposedException(nameof(RuntimeBoundary));
            if (pending.Count >= 64 || receipts.Count >= 256) throw new InvalidOperationException("Boundary limit: 64 pending / 256 retained; forget completed receipts.");
            if (latest.TryGetValue(slot, out var old) && !old.IsCompleted) old.Finish("superseded", "A newer request owns this slot.");
            var receipt = new BoundaryReceipt(Guid.NewGuid().ToString("N"), slot, revision);
            latest[slot] = receipt; receipts.Add(receipt.Id, receipt);
            pending.Enqueue(new Work { Receipt = receipt, Apply = tick => apply(receipt, tick) });
            return receipt;
        }
    }
    public BoundaryReceipt GetReceipt(string id) { lock (gate) return receipts[id]; }
    public void Cancel(string id) { lock (gate) receipts[id].Finish("cancelled", "Cancelled by caller."); }
    public void Forget(string id) { lock (gate) { var item = receipts[id]; if (!item.IsCompleted) throw new InvalidOperationException("Receipt is pending."); receipts.Remove(id); if (latest.TryGetValue(item.Slot, out var current) && current == item) latest.Remove(item.Slot); } }

    // Owner calls once per Unity FixedUpdate. Work queued by callbacks waits for
    // the next boundary. A callback is not preemptible and must remain short.
    public void Pump()
    {
        Work[] work;
        lock (gate) {
            if (stopped) return;
            Tick = checked(Tick + 1);
            int count = Math.Min(16, pending.Count); work = new Work[count];
            for (int i = 0; i < count; i++) work[i] = pending.Dequeue();
        }
        foreach (var item in work) {
            if (item.Receipt.IsCompleted) continue;
            try { item.Receipt.Begin(Tick); item.Apply(Tick); }
            catch (Exception error) { item.Receipt.Finish("failed", error.GetBaseException().Message); }
        }
    }

    private void Publish(string root, string revision, long tick, List<CheckpointPart> parts, BoundaryReceipt receipt)
    {
        string stage = Path.Combine(root, "." + receipt.Id + ".tmp");
        try {
            Directory.CreateDirectory(stage);
            var manifest = new XElement("checkpoint", new XAttribute("format", 1), new XAttribute("api", ApiVersion),
                new XAttribute("revision", revision), new XAttribute("tick", tick));
            foreach (var part in parts) {
                WriteDurable(Path.Combine(stage, part.Id + ".bin"), part.Data);
                manifest.Add(new XElement("source", new XAttribute("id", part.Id), new XAttribute("schema", part.Version),
                    new XAttribute("bytes", part.Data.Length), new XAttribute("sha256", Hash(part.Data))));
            }
            WriteDurable(Path.Combine(stage, "checkpoint.xml"), System.Text.Encoding.UTF8.GetBytes(manifest.ToString()));
            lock (gate) {
                if (stopped || receipt.IsCompleted || !latest.TryGetValue(receipt.Slot, out var current) || current != receipt) {
                    receipt.Finish("superseded", "Publication cancelled."); return;
                }
                string revisions = Path.Combine(root, "revisions"); Directory.CreateDirectory(revisions);
                string destination = Path.Combine(revisions, receipt.Id);
                Directory.Move(stage, destination);
                string pointer = Path.Combine(root, "current"), temporary = Path.Combine(root, receipt.Id + ".pointer.tmp");
                WriteDurable(temporary, System.Text.Encoding.ASCII.GetBytes(receipt.Id));
                if (File.Exists(pointer)) File.Replace(temporary, pointer, null); else File.Move(temporary, pointer);
                receipt.Published(destination);
            }
        }
        catch (Exception error) { receipt.Finish("failed", error.GetBaseException().Message); }
        finally { try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { /* Private incomplete generation; never current. */ } }
    }
    internal static void WriteDurable(string path, byte[] data) { using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(data, 0, data.Length); file.Flush(true); } }
    internal static string Hash(byte[] data) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
    internal static void Identifier(string id) { if (string.IsNullOrEmpty(id) || id.Length > 96 || id.Any(c => !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '-'))) throw new ArgumentException("Use a portable source identity of letters, digits, underscores or hyphens."); }
    public Task<CheckpointImage> ReadCheckpoint(string directory) => Task.Run(() => CheckpointImage.Read(Path.GetFullPath(directory)));
    public void Dispose() { lock (gate) { stopped = true; foreach (var receipt in receipts.Values) if (!receipt.IsCompleted) receipt.Finish("cancelled", "Host stopped."); pending.Clear(); sources.Clear(); } }
}

public sealed class BoundaryReceipt
{
    private readonly object gate = new object();
    private string state = "accepted", error = "", publishedPath = ""; private long tick; private object result;
    public string Id { get; }
    public string Slot { get; }
    public string Revision { get; }
    public string State { get { lock (gate) return state; } }
    public string Error { get { lock (gate) return error; } }
    public long Tick { get { lock (gate) return tick; } }
    public object Result { get { lock (gate) return result; } }
    public string PublishedPath { get { lock (gate) return publishedPath; } }
    public bool IsCompleted { get { lock (gate) return state != "accepted" && state != "writing"; } }
    internal BoundaryReceipt(string id, string slot, string revision) { Id = id; Slot = slot; Revision = revision; }
    internal void Begin(long at) { lock (gate) tick = at; }
    internal void Applied(long at, object value) { lock (gate) { tick = at; result = value; state = "applied"; } }
    internal void Writing(long at) { lock (gate) { tick = at; state = "writing"; } }
    internal void Published(string path) { lock (gate) { publishedPath = path; state = "published"; } }
    internal void Finish(string final, string reason) { lock (gate) { if (IsCompleted) return; state = final; error = reason; } }
    public object EnsureSuccess() { lock (gate) { if (state == "applied") return result; if (state == "published") return publishedPath; throw new InvalidOperationException(state + ": " + error); } }
}
internal sealed class CheckpointPart
{
    public readonly string Id; public readonly int Version; public readonly byte[] Data;
    public CheckpointPart(string id, int version, byte[] data) { Id = id; Version = version; Data = data; }
}
public sealed class CheckpointImage
{
    public string Revision { get; private set; }
    public long Tick { get; private set; }
    private readonly Dictionary<string, CheckpointPart> parts = new Dictionary<string, CheckpointPart>();
    public byte[] GetBytes(string identity, int schemaVersion) { var part = parts[identity]; if (part.Version != schemaVersion) throw new InvalidDataException("Checkpoint schema version differs: " + identity); return (byte[])part.Data.Clone(); }
    internal static CheckpointImage Read(string root)
    {
        string generation = File.ReadAllText(Path.Combine(root, "current"));
        if (!Guid.TryParseExact(generation, "N", out var ignored)) throw new InvalidDataException("Invalid checkpoint pointer.");
        string directory = Path.Combine(root, "revisions", generation);
        string manifestPath = Path.Combine(directory, "checkpoint.xml");
        if (new FileInfo(manifestPath).Length > 128 * 1024) throw new InvalidDataException("Checkpoint manifest exceeds limit.");
        var manifest = XElement.Load(manifestPath);
        if (manifest.Name != "checkpoint" || (int)manifest.Attribute("format") != 1) throw new InvalidDataException("Unsupported checkpoint format.");
        var image = new CheckpointImage { Revision = (string)manifest.Attribute("revision"), Tick = (long)manifest.Attribute("tick") };
        long total = 0;
        foreach (var source in manifest.Elements("source")) {
            string id = (string)source.Attribute("id"); RuntimeBoundary.Identifier(id);
            int schema = (int)source.Attribute("schema"); long length = (long)source.Attribute("bytes"); total += length;
            if (schema < 1 || length < 0 || total > 256L * 1024 * 1024 || image.parts.Count >= 64 || image.parts.ContainsKey(id)) throw new InvalidDataException("Invalid checkpoint source inventory.");
            string path = Path.Combine(directory, id + ".bin");
            if (new FileInfo(path).Length != length) throw new InvalidDataException("Checkpoint source length differs: " + id);
            byte[] bytes = File.ReadAllBytes(path);
            if (RuntimeBoundary.Hash(bytes) != (string)source.Attribute("sha256")) throw new InvalidDataException("Checkpoint source hash differs: " + id);
            image.parts.Add(id, new CheckpointPart(id, schema, bytes));
        }
        if (image.parts.Count == 0) throw new InvalidDataException("Checkpoint has no sources.");
        return image;
    }
}
