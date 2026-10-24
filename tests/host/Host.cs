// A headless Skunkworks: the player's ControlServer and ReflectionBroker, pumped on
// one thread the way Main.Update pumps them, and a client speaking skunkworks.wire/1
// with the player's own codec. Unity-only roots (window, terminal, input, boundary)
// are absent from the headless host; the protocol, handles and roots are the player's.
//
//   SkunkworksHost selftest     serve a private pipe and check the protocol's behavior against it
//   SkunkworksHost measure      time Wait against polling on a host pumped at 60 Hz
//   SkunkworksHost probe PIPE   ask a running player its Unity version, generic roots and terminal status
//   SkunkworksHost quit PIPE    ask a running player to quit
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Threading;

// The player's SystemPaths writes its log; the host writes to stderr.
public static class SystemPaths
{
    public static void Log(string source, string message) { Console.Error.WriteLine(source + ": " + message); }
}

// Each instance's identity, registered as the "host" root.
public sealed class HostInfo
{
    public HostInfo(string pipe) { Pipe = pipe; }
    public string Pipe { get; }
}

// A CLR type with unambiguous members, standing in for a loaded assembly's types.
public sealed class HostFixture
{
    public HostFixture(string name) { Name = name; }
    public string Name { get; set; }
    public int Add(int a, int b) => a + b;
    public string Echo(string text) => text;
    public double Half(double x) => x / 2;
    public int Length(byte[] bytes) => bytes.Length;
}

// One connection: the Hello handshake, then one request and its reply at a time.
internal sealed class Client : IDisposable
{
    private readonly NamedPipeClientStream pipe;
    private long next = 1;

    public Client(string name, bool hello = true, int timeoutMs = 10000)
    {
        pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        pipe.Connect(timeoutMs);
        if (!hello) return;
        Message ready = Send(new Message(next++, MessageKind.Hello, Value.String(WireSchema.Protocol)));
        if (ready.Kind != MessageKind.Ready) throw new InvalidOperationException("handshake refused: " + Text(ready));
        Owner = ready.Values[1].AsString();
    }

    public string Owner { get; }

    public Message Send(Message message) { Wire.Write(pipe, message); return Wire.Read(pipe); }
    public void Post(Message message) => Wire.Write(pipe, message);
    public Message Ask(MessageKind kind, params Value[] values) => Send(new Message(next++, kind, values));

    // Steps reference earlier results with Value.BatchRef(N); the reply holds each step's result.
    public Message Batch(params Message[] steps)
    {
        var packed = new Value[steps.Length];
        for (int i = 0; i < steps.Length; i++) packed[i] = Value.Bytes(Wire.Pack(new Message(i + 1, steps[i].Kind, steps[i].Values)));
        return Ask(MessageKind.Batch, packed);
    }

    public Value[] Answer(params Message[] steps)
    {
        Message reply = Batch(steps);
        if (reply.Kind != MessageKind.Result) throw new InvalidOperationException("request failed: " + Text(reply));
        return reply.Values;
    }

    public static Message Step(MessageKind kind, params Value[] values) => new Message(0, kind, values);
    public static string Text(Message message) => message.Kind + (message.Values.Length > 0 && message.Values[0].Kind == ValueKind.String ? " " + message.Values[0].AsString() : "");

    public void Dispose() => pipe.Dispose();
}

internal static class Host
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "selftest") return SelfTest();
            if (args.Length == 1 && args[0] == "measure") return Measure();
            if (args.Length == 2 && args[0] == "probe") return Probe(args[1]);
            if (args.Length == 2 && args[0] == "quit") return Quit(args[1]);
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.Message); return 1; }
        Console.Error.WriteLine("usage: SkunkworksHost selftest | measure | probe PIPE | quit PIPE");
        return 2;
    }

    // The headless player on its own thread, standing in for Main.Update.
    private sealed class Instance : IDisposable
    {
        private readonly ManualResetEventSlim stop = new ManualResetEventSlim();
        private readonly Thread pump;

        public Instance(string pipe, int pumpMs = 2)
        {
            Pipe = pipe;
            var ready = new ManualResetEventSlim();
            Exception failure = null;
            pump = new Thread(() =>
            {
                try
                {
                    var broker = new ReflectionBroker();
                    broker.RegisterRoot("reflection", broker);
                    broker.RegisterRoot("values", new NativeValues(broker.DecodeValue));
                    broker.RegisterRoot("host", new HostInfo(pipe));
                    broker.RegisterRoot("fixture", new HostFixture("start"));
                    using (var server = new ControlServer(pipe))
                    {
                        server.Start();
                        broker.RegisterRoot("dispatch", server.Status);
                        ready.Set();
                        while (!stop.IsSet) { server.Pump(broker.HandleOwned, broker.ReleaseOwner, 64, broker.DescribeRequest, broker.WaitOwned); Thread.Sleep(pumpMs); }
                    }
                }
                catch (Exception error) { failure = error; ready.Set(); }
            }) { IsBackground = true };
            pump.Start();
            ready.Wait();
            if (failure != null) throw failure;
        }

        public string Pipe { get; }
        public void Dispose() { stop.Set(); pump.Join(10000); }
    }

    private static int passed;
    private static void Test(string name, Action body) { body(); passed++; Console.Out.WriteLine("PASS " + name); }
    private static void Require(bool okay, string message) { if (!okay) throw new InvalidOperationException(message); }

    private static Message Step(MessageKind kind, params Value[] values) => Client.Step(kind, values);
    private static Value Ref(int index) => Value.BatchRef(index);
    private static Value S(string text) => Value.String(text);
    private static Value I(int number) => Value.Int32(number);

    private static string Statistics(string pipe)
    {
        using (var client = new Client(pipe))
            return client.Answer(Step(MessageKind.Find, S("reflection")), Step(MessageKind.Call, Ref(0), S("Statistics"), I(0), I(-1)))[1].AsString();
    }

    private static int SelfTest()
    {
        string tag = Guid.NewGuid().ToString("N").Substring(0, 8);
        using (var a = new Instance("skunkworks.unity.selftest-" + tag + "-a"))
        using (var b = new Instance("skunkworks.unity.selftest-" + tag + "-b"))
        {
            Test("the handshake names the protocol and gives each connection its own owner", () =>
            {
                using (var one = new Client(a.Pipe))
                using (var two = new Client(a.Pipe))
                    Require(one.Owner.Length > 0 && one.Owner != two.Owner, "owners: " + one.Owner + " / " + two.Owner);
            });

            Test("a request before Hello, or another protocol, is refused", () =>
            {
                using (var bare = new Client(a.Pipe, hello: false))
                    Require(bare.Ask(MessageKind.Find, S("reflection")).Kind == MessageKind.Fault, "a request without Hello was served");
                using (var other = new Client(a.Pipe, hello: false))
                    Require(other.Ask(MessageKind.Hello, S("skunkworks.wire/0")).Kind == MessageKind.Fault, "another protocol was accepted");
            });

            Test("each instance answers on its own pipe", () =>
            {
                foreach (var instance in new[] { a, b })
                    using (var client = new Client(instance.Pipe))
                    {
                        string pipe = client.Answer(Step(MessageKind.Find, S("host")), Step(MessageKind.Get, Ref(0), S("Pipe")))[1].AsString();
                        Require(pipe == instance.Pipe, instance.Pipe + " answered as " + pipe);
                    }
            });

            Test("a batch threads each result into later steps", () =>
            {
                using (var client = new Client(a.Pipe))
                {
                    Value[] v = client.Answer(
                        Step(MessageKind.Type, S("HostFixture")),
                        Step(MessageKind.New, Ref(0), I(-1), S("batch")),
                        Step(MessageKind.Call, Ref(1), S("Add"), I(0), I(-1), I(2), I(3)),
                        Step(MessageKind.Get, Ref(1), S("Name")),
                        Step(MessageKind.Call, Ref(1), S("Echo"), I(0), I(-1), S("λ😀")),
                        Step(MessageKind.Call, Ref(1), S("Length"), I(0), I(-1), Value.Bytes(new byte[] { 1, 2, 3 })),
                        Step(MessageKind.Call, Ref(1), S("Half"), I(0), I(-1), Value.Double(0.5)));
                    Require(v[0].Kind == ValueKind.Type && v[1].Kind == ValueKind.Object, "type and object handles: " + v[0].Kind + ", " + v[1].Kind);
                    Require(v[2].AsInt32() == 5 && v[3].AsString() == "batch" && v[4].AsString() == "λ😀" && v[5].AsInt32() == 3 && v[6].AsDouble() == 0.25,
                        "batch values differ");
                }
            });

            Test("a failing step names its batch index and later steps do not run", () =>
            {
                using (var client = new Client(a.Pipe))
                {
                    client.Answer(
                        Step(MessageKind.Type, S("HostFixture")), Step(MessageKind.New, Ref(0), I(-1), S("before")),
                        Step(MessageKind.Find, S("reflection")), Step(MessageKind.Call, Ref(2), S("RegisterRoot"), I(0), I(-1), S("selftest.order"), Ref(1)));
                    Message reply = client.Batch(
                        Step(MessageKind.Type, S("No.Such.Type")),
                        Step(MessageKind.Find, S("selftest.order")), Step(MessageKind.Set, Ref(1), S("Name"), S("ran")));
                    Require(reply.Kind == MessageKind.Fault && reply.Values[0].AsString().StartsWith("Batch[0]", StringComparison.Ordinal), "fault: " + Client.Text(reply));
                    string name = client.Answer(Step(MessageKind.Find, S("selftest.order")), Step(MessageKind.Get, Ref(0), S("Name")))[1].AsString();
                    Require(name == "before", "a step after the failure ran");
                }
            });

            Test("a registered name outlives the connection that made it", () =>
            {
                using (var client = new Client(a.Pipe))
                    client.Answer(
                        Step(MessageKind.Type, S("HostFixture")), Step(MessageKind.New, Ref(0), I(-1), S("kept")),
                        Step(MessageKind.Find, S("reflection")), Step(MessageKind.Call, Ref(2), S("RegisterRoot"), I(0), I(-1), S("selftest.kept"), Ref(1)));
                Message[] read = { Step(MessageKind.Find, S("selftest.kept")), Step(MessageKind.Get, Ref(0), S("Name")) };
                using (var client = new Client(a.Pipe)) Require(client.Answer(read)[1].AsString() == "kept", "a new connection does not read the registered object");
                using (var client = new Client(a.Pipe)) client.Answer(Step(MessageKind.Find, S("selftest.kept")), Step(MessageKind.Set, Ref(0), S("Name"), S("changed")));
                using (var client = new Client(a.Pipe)) Require(client.Answer(read)[1].AsString() == "changed", "a change made by one connection is not seen by the next");
            });

            Test("instances keep their own names", () =>
            {
                using (var client = new Client(b.Pipe))
                {
                    Message reply = client.Ask(MessageKind.Find, S("selftest.kept"));
                    Require(reply.Kind == MessageKind.Fault && Client.Text(reply).Contains("not found"), "B saw A's root: " + Client.Text(reply));
                }
            });

            Test("a closed connection releases its handles", () =>
            {
                string before = Statistics(a.Pipe), otherBefore = Statistics(b.Pipe);
                using (var client = new Client(a.Pipe))
                {
                    var made = new List<Message> { Step(MessageKind.Type, S("HostFixture")) };
                    for (int n = 1; n <= 24; n++) made.Add(Step(MessageKind.New, Ref(0), I(-1), S("temporary " + n)));
                    for (int n = 1; n <= 8; n++) made.Add(Step(MessageKind.Type, S("System.String")));
                    Require(client.Answer(made.ToArray()).Length == 33, "the batch did not make 33 handles");
                }
                string after = Statistics(a.Pipe);
                for (var deadline = DateTime.UtcNow.AddSeconds(3); after != before && DateTime.UtcNow < deadline; after = Statistics(a.Pipe)) Thread.Sleep(20);
                Require(after == before, "handles left behind: before '" + before + "', after '" + after + "'");
                Require(Statistics(b.Pipe) == otherBefore, "A's handles touched B's table");
            });

            Test("a handle from an earlier connection is refused", () =>
            {
                long held;
                using (var client = new Client(a.Pipe)) held = client.Ask(MessageKind.Find, S("host")).Values[0].AsObject();
                using (var client = new Client(a.Pipe))
                    Require(client.Ask(MessageKind.Get, Value.Object(held), S("Pipe")).Kind == MessageKind.Fault, "a released handle still resolved");
            });

            using (var client = new Client(a.Pipe))
                client.Answer(
                    Step(MessageKind.Type, S("HostFixture")), Step(MessageKind.New, Ref(0), I(-1), S("idle")),
                    Step(MessageKind.Find, S("reflection")), Step(MessageKind.Call, Ref(2), S("RegisterRoot"), I(0), I(-1), S("selftest.wait"), Ref(1)));

            Test("a wait answers when its member changes, while other requests keep answering", () =>
            {
                using (var waiter = new Client(a.Pipe))
                using (var other = new Client(a.Pipe))
                {
                    Value target = waiter.Ask(MessageKind.Find, S("selftest.wait")).Values[0];
                    var waiting = System.Threading.Tasks.Task.Run(() => waiter.Ask(MessageKind.Wait, target, S("Name"), I(0), S("idle"), I(10000)));
                    Thread.Sleep(100);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    for (int n = 0; n < 20; n++) other.Answer(Step(MessageKind.Find, S("selftest.wait")), Step(MessageKind.Get, Ref(0), S("Name")));
                    Require(clock.ElapsedMilliseconds < 2000 && !waiting.IsCompleted, "other requests waited behind the wait (" + clock.ElapsedMilliseconds + " ms)");
                    other.Answer(Step(MessageKind.Find, S("selftest.wait")), Step(MessageKind.Set, Ref(0), S("Name"), S("go")));
                    Require(waiting.Wait(2000), "the wait did not answer the change");
                    Message reply = waiting.Result;
                    Require(reply.Kind == MessageKind.Result && reply.Values[0].AsBoolean() && reply.Values[1].AsString() == "go", "reply: " + Client.Text(reply));
                }
            });

            Test("a wait already met answers at once, and an unmet one answers at its deadline", () =>
            {
                using (var client = new Client(a.Pipe))
                {
                    Value target = client.Ask(MessageKind.Find, S("selftest.wait")).Values[0];
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    Message met = client.Ask(MessageKind.Wait, target, S("Name"), I(1), S("go"), I(10000));
                    Require(met.Values[0].AsBoolean() && clock.ElapsedMilliseconds < 1000, "a met wait took " + clock.ElapsedMilliseconds + " ms");
                    clock.Restart();
                    Message unmet = client.Ask(MessageKind.Wait, target, S("Name"), I(1), S("never"), I(300));
                    Require(unmet.Kind == MessageKind.Result && !unmet.Values[0].AsBoolean() && unmet.Values[1].AsString() == "go", "reply: " + Client.Text(unmet));
                    Require(clock.ElapsedMilliseconds >= 300 && clock.ElapsedMilliseconds < 2000, "the deadline answered after " + clock.ElapsedMilliseconds + " ms");
                }
            });

            Test("a wait inside a batch, past the timeout cap, or on a non-scalar member is refused", () =>
            {
                using (var client = new Client(a.Pipe))
                {
                    Value target = client.Ask(MessageKind.Find, S("selftest.wait")).Values[0];
                    Require(client.Batch(Step(MessageKind.Wait, target, S("Name"), I(0), S("go"), I(100))).Kind == MessageKind.Fault, "a batched wait ran");
                    Require(client.Ask(MessageKind.Wait, target, S("Name"), I(0), S("go"), I(600000)).Kind == MessageKind.Fault, "a 600 s wait was accepted");
                    Value encoding = client.Ask(MessageKind.Type, S("System.Text.Encoding")).Values[0];
                    Message reply = client.Ask(MessageKind.Wait, encoding, S("UTF8"), I(0), Value.Null(), I(100));
                    Require(reply.Kind == MessageKind.Fault && Client.Text(reply).Contains("scalar"), "reply: " + Client.Text(reply));
                }
            });

            Test("a wait ends when its client leaves", () =>
            {
                var waiter = new Client(a.Pipe);
                Value target = waiter.Ask(MessageKind.Find, S("selftest.wait")).Values[0];
                // A read blocked on another thread keeps a Windows pipe handle open: send, then leave.
                waiter.Post(new Message(99, MessageKind.Wait, target, S("Name"), I(1), S("never"), I(30000)));
                Message[] count = { Step(MessageKind.Find, S("dispatch")), Step(MessageKind.Get, Ref(0), S("Waiters")) };
                using (var other = new Client(a.Pipe))
                {
                    int parked = 0;
                    for (var deadline = DateTime.UtcNow.AddSeconds(2); parked == 0 && DateTime.UtcNow < deadline; Thread.Sleep(10)) parked = other.Answer(count)[1].AsInt32();
                    Require(parked == 1, "the wait never parked");
                    waiter.Dispose();
                    for (var deadline = DateTime.UtcNow.AddSeconds(2); parked != 0 && DateTime.UtcNow < deadline; Thread.Sleep(10)) parked = other.Answer(count)[1].AsInt32();
                    Require(parked == 0, "the wait outlived its client");
                }
            });
        }
        Console.Out.WriteLine("SKUNKWORKS_HOST_OK tests=" + passed);
        return 0;
    }

    // Wait against polling on a host pumped like a 60 Hz player: how long a change takes to be
    // seen, how many requests Unity's thread serves meanwhile, and what one waiter costs per pump.
    private static int Measure()
    {
        const int rounds = 40, pollMs = 250;
        using (var host = new Instance("skunkworks.unity.measure-" + Guid.NewGuid().ToString("N").Substring(0, 8), 16))
        using (var setter = new Client(host.Pipe))
        using (var watcher = new Client(host.Pipe))
        {
            Value target = watcher.Ask(MessageKind.Find, S("fixture")).Values[0];
            Message[] set(int n) => new[] { Step(MessageKind.Find, S("fixture")), Step(MessageKind.Set, Ref(0), S("Name"), S("v" + n)) };
            Message[] executed = { Step(MessageKind.Find, S("dispatch")), Step(MessageKind.Get, Ref(0), S("Executed")) };
            var random = new Random(7);
            var waited = new List<double>(); var polled = new List<double>();
            long before = setter.Answer(executed)[1].AsInt64();
            for (int n = 0; n < rounds; n++)
            {
                string old = watcher.Ask(MessageKind.Get, target, S("Name")).Values[0].AsString();
                var waiting = System.Threading.Tasks.Task.Run(() => watcher.Ask(MessageKind.Wait, target, S("Name"), I(0), S(old), I(10000)));
                Thread.Sleep(random.Next(20, 300));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                setter.Answer(set(n));
                waiting.Wait();
                waited.Add(clock.Elapsed.TotalMilliseconds);
            }
            long waitRequests = setter.Answer(executed)[1].AsInt64() - before - rounds;
            before = setter.Answer(executed)[1].AsInt64();
            for (int n = 0; n < rounds; n++)
            {
                string old = watcher.Ask(MessageKind.Get, target, S("Name")).Values[0].AsString();
                var clock = new System.Diagnostics.Stopwatch();
                var setting = System.Threading.Tasks.Task.Run(() => { Thread.Sleep(random.Next(20, 300)); clock.Start(); setter.Answer(set(rounds + n)); });
                while (watcher.Ask(MessageKind.Get, target, S("Name")).Values[0].AsString() == old) Thread.Sleep(pollMs);
                polled.Add(clock.Elapsed.TotalMilliseconds);
                setting.Wait();
            }
            long pollRequests = setter.Answer(executed)[1].AsInt64() - before - rounds;
            Console.Out.WriteLine("wake  " + Summary(waited) + " requests/change=" + (waitRequests / (double)rounds).ToString("0.0"));
            Console.Out.WriteLine("poll" + pollMs + " " + Summary(polled) + " requests/change=" + (pollRequests / (double)rounds).ToString("0.0"));
        }
        var broker = new ReflectionBroker();
        broker.RegisterRoot("fixture", new HostFixture("still"));
        Value fixture = broker.HandleOwned("bench", new Message(1, MessageKind.Find, S("fixture"))).Values[0];
        var unmet = new Message(2, MessageKind.Wait, fixture, S("Name"), I(0), S("still"), I(1000));
        for (int n = 0; n < 1000; n++) broker.WaitOwned("bench", unmet, false);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        const int evaluations = 200000;
        for (int n = 0; n < evaluations; n++) broker.WaitOwned("bench", unmet, false);
        Console.Out.WriteLine("evaluate " + (timer.Elapsed.TotalMilliseconds * 1000000 / evaluations).ToString("0") + " ns per waiter per pump");
        return 0;
    }
    private static string Summary(List<double> samples)
    {
        samples.Sort();
        double sum = 0; foreach (double sample in samples) sum += sample;
        return "ms median=" + samples[samples.Count / 2].ToString("0.0") + " p95=" + samples[(int)(samples.Count * .95)].ToString("0.0") +
            " mean=" + (sum / samples.Count).ToString("0.0") + " max=" + samples[samples.Count - 1].ToString("0.0");
    }

    // A running player: its Unity version, its generic roots and its terminal, one line each.
    private static int Probe(string pipe)
    {
        using (var client = new Client(pipe))
        {
            Value[] v = client.Answer(
                Step(MessageKind.Type, S("UnityEngine.Application")), Step(MessageKind.Get, Ref(0), S("unityVersion")),
                Step(MessageKind.Find, S("reflection")), Step(MessageKind.Find, S("window")),
                Step(MessageKind.Find, S("terminal")), Step(MessageKind.Call, Ref(4), S("Status"), I(0), I(-1)));
            Console.Out.WriteLine("unity " + v[1].AsString());
            Console.Out.WriteLine("roots reflection window terminal");
            Console.Out.WriteLine("terminal " + v[5].AsString());
        }
        return 0;
    }

    private static int Quit(string pipe)
    {
        using (var client = new Client(pipe))
            client.Answer(Step(MessageKind.Type, S("UnityEngine.Application")), Step(MessageKind.Call, Ref(0), S("Quit"), I(0), I(0)));
        return 0;
    }
}
