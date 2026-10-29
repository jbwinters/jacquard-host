using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JacquardLang.Host.Protocol;
using JacquardLang.Host.Sdk;
using JacquardLang.Host.Tests.Readme;
using NUnit.Framework;

namespace JacquardLang.Host.Tests.Sdk;

/// <summary>
/// Live SDK tests against the real Core named by <c>JACQUARD_BIN</c> and
/// <c>JACQUARD_PRELUDE</c>. Each is ignored without them, and fails instead
/// when <c>JACQUARD_REQUIRE_LIVE</c> is set, as CI does.
/// </summary>
[TestFixture]
[Platform("Linux,MacOsX")]
public sealed class SdkLiveTests
{
    private string root = string.Empty;
    private Runtime runtime = null!;

    internal static void RequireLive()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JACQUARD_BIN")) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JACQUARD_PRELUDE")))
        {
            if (Environment.GetEnvironmentVariable("JACQUARD_REQUIRE_LIVE") is not null)
            {
                Assert.Fail("live tests need JACQUARD_BIN and JACQUARD_PRELUDE");
            }

            Assert.Ignore("live tests need JACQUARD_BIN and JACQUARD_PRELUDE");
        }
    }

    [SetUp]
    public void Live()
    {
        RequireLive();
        root = SdkSupport.Scratch("live");
        runtime = new Runtime();
    }

    [TearDown]
    public void CleanUp()
    {
        Operations.AfterDecodeHook = null;
        if (root.Length > 0)
        {
            SdkSupport.RemoveScratch(root);
        }
    }

    /// <summary>The <c>dotnet/</c> directory this test assembly was built from.</summary>
    internal static string DotnetRoot() =>
        typeof(SdkLiveTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "DotnetRoot").Value!;

    internal static string Example(string project) => Path.GetFullPath(Path.Combine(DotnetRoot(), "..", "examples", project, "rules.jac"));

    private PrepareOptions Options => new() { CacheDir = Path.Combine(root, "cache") };

    private PreparedProgram Prepare(string source) => runtime.Prepare(new[] { source }, Options);

    private string Source(string name, string text)
    {
        string path = Path.Combine(root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static Dynamic Policy(PreparedProgram program) => program.Data.Make("TriagePolicy", Dynamic.Of(4L), Dynamic.Of(60L));

    [Test]
    public void QuickstartCallsInvocationsAndSavedFrames()
    {
        Function shipping = Prepare(Example("quickstart")).Function("shipping-cost");
        Assert.That(shipping.Call(Codecs.Int, 35), Is.EqualTo(5L));
        Assert.That(shipping.Call(Codecs.Int, 75L), Is.EqualTo(0L));
        Invocation invocation = shipping.Invoke(35);
        Assert.That(invocation.ToDynamic(), Is.EqualTo(Dynamic.Of(5L)));
        Assert.That(invocation.Render(), Is.EqualTo("shipping-cost: ok"));
        Assert.That(invocation.Error, Is.Null);
        string path = Path.Combine(root, "inspection.json");
        invocation.Save(path);
        byte[] raw = File.ReadAllBytes(path);
        Assert.That(raw[^1], Is.EqualTo((byte)'\n'));
        JsonObject saved = JsonCodec.DecodeObject(raw.AsSpan(0, raw.Length - 1), 128);
        Assert.That(saved["schema"], Is.EqualTo(Json.Of("jacquard-host-inspection-v1")));
        Assert.That(saved["core_frames"].AsArray()!, Has.Count.EqualTo(invocation.Raw.CoreFrames.Count));
        Assert.That(File.GetUnixFileMode(path), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
    }

    /// <summary>The README's SDK examples, compiled from <c>Readme/</c>, against Core.</summary>
    [Test]
    public void TheReadmeExamplesRun()
    {
        string saved = Path.Combine(root, "shipping.json");
        Assert.That(Shipping.Cost(Example("quickstart"), Path.Combine(root, "cache"), saved), Is.EqualTo(5L));
        Assert.That(File.Exists(saved), Is.True);
        PreparedProgram documents = Prepare(Source(
            "documents.jac",
            "once effect Documents where {\n  read-document : (Text) -> Text\n}\n\nsummarize : (Text) ->{Documents} Text\nsummarize(key) = read-document(key)\n"));
        Function summarize = Documents.Summarize(documents);
        Assert.That(summarize.Call(Codecs.Text, "example"), Is.EqualTo("A synthetic document supplied by .NET."));
        JacquardException refused = Support.Throws<JacquardException>(() => summarize.Call(Codecs.Text, "other"));
        Assert.That(refused.Kind, Is.EqualTo(JacquardErrorKind.Invocation));
        Assert.That(refused.Failure, Is.EqualTo(Failure.OperationFailed), refused.Message);
        PreparedProgram triage = Prepare(Example("triage"));
        IReadOnlyList<Incidents.Triage> routed = Incidents.Route(
            triage.Function("triage.batch"), Policy(triage), new[] { new Incidents.Incident("inc-1", "checkout", 5, 3, true, null) });
        Assert.That(routed, Is.EqualTo(new[] { new Incidents.Routed("inc-1", "checkout-oncall", Incidents.Urgency.PageNow, new[] { "unowned", "customer-impact" }) }).Using(new RoutedComparer()));
    }

    /// <summary>Compares results by value, with their reason lists by items.</summary>
    private sealed class RoutedComparer : IEqualityComparer<Incidents.Triage>
    {
        public bool Equals(Incidents.Triage? x, Incidents.Triage? y) =>
            (x, y) switch
            {
                (Incidents.Routed a, Incidents.Routed b) => a.Id == b.Id && a.Team == b.Team && a.Urgency == b.Urgency && a.Reasons.SequenceEqual(b.Reasons),
                _ => Equals(x, y),
            };

        public int GetHashCode(Incidents.Triage obj) => 0;
    }

    private static Incidents.Incident Incident(long i) =>
        new(i.ToString(CultureInfo.InvariantCulture), "api", (i % 5) + 1, i * 3, i % 2 == 0, i % 3 != 0 ? "ops" : null);

    [Test]
    public void RecordsEnumsOptionsAndExplicitBatches()
    {
        PreparedProgram program = Prepare(Example("triage"));
        Function route = program.Function("triage.batch");
        Argument[] policy = { Policy(program) };
        List<Incidents.Incident> rows = Enumerable.Range(0, 64).Select(i => Incident(i)).ToList();
        IReadOnlyList<Incidents.Triage> results = route.MapBatches(Incidents.TriageCodec, Incidents.IncidentCodec, rows, 8, policy);
        Assert.That(results, Has.Count.EqualTo(rows.Count));

        // An item codec that throws on the second batch: the failure names that batch, keeps the first, and keeps the codec's exception.
        int decoded = 0;
        Codec<Incidents.Triage> failing = Codecs.Create<Incidents.Triage>(
            (encoder, value) => Incidents.TriageCodec.Encode(encoder, value),
            (decoder, value) => ++decoded > 8 ? throw new InvalidOperationException("second batch") : Incidents.TriageCodec.Decode(decoder, value));
        JacquardException batch = Support.Throws<JacquardException>(() => route.MapBatches(failing, Incidents.IncidentCodec, rows.GetRange(0, 16), 8, policy));
        Assert.That(batch.BatchIndex, Is.EqualTo(1));
        Assert.That(batch.Completed, Has.Count.EqualTo(8));
        Assert.That(batch.InnerException, Is.InstanceOf<InvalidOperationException>(), "the codec's exception is not kept");
        for (int i = 0; i < rows.Count; i++)
        {
            Incidents.Incident row = rows[i];
            bool page = row.Visible && row.Severity >= 4;
            var reasons = new List<string>();
            if (row.Owner is null)
            {
                reasons.Add("unowned");
            }

            if (row.Age >= 60)
            {
                reasons.Add("ageing");
            }

            if (page)
            {
                reasons.Add("customer-impact");
            }

            Incidents.Urgency urgency = page ? Incidents.Urgency.PageNow : row.Age >= 60 || row.Severity == 5 ? Incidents.Urgency.WorkToday : Incidents.Urgency.WatchOnly;
            Assert.That(results[i], Is.EqualTo(new Incidents.Routed(row.Id, row.Owner ?? "api-oncall", urgency, reasons)).Using(new RoutedComparer()), "row " + row.Id);
        }

        Assert.That(route.MapBatches(Incidents.TriageCodec, Incidents.IncidentCodec, Array.Empty<Incidents.Incident>(), 8, policy), Is.Empty);
    }

    [Test]
    public void InvalidInputIsRefusedBeforeAnyWorker()
    {
        PreparedProgram program = Prepare(Example("triage"));

        // A spawn would now fail as worker_spawn_failed, not as a local refusal.
        program.SetWorkerCommand(new[] { "/no/such/jacquard" });
        Function route = program.Function("triage.batch");
        Argument[] policy = { Policy(program) };
        List<Incidents.Incident> rows = Enumerable.Range(0, 17).Select(i => Incident(i)).ToList();
        rows[16] = rows[16] with { Severity = Json.IntMax + 1 };
        JacquardException batch = Support.Throws<JacquardException>(() => route.MapBatches(Incidents.TriageCodec, Incidents.IncidentCodec, rows, 8, policy));
        Assert.That(batch.BatchIndex, Is.EqualTo(-1));
        Assert.That(batch.Completed, Is.Empty);
        Assert.That(batch.Kind, Is.EqualTo(JacquardErrorKind.InvalidArgument));
        Assert.That(batch.Message, Does.Contain("argument[1][0].severity"));
        Incidents.Incident[] many = Enumerable.Repeat(Incident(1), 64).ToArray();
        Codec<IReadOnlyList<Incidents.Triage>> results = Codecs.List(Incidents.TriageCodec);
        Assert.That(
            Support.Throws<JacquardException>(() => route.Call(results, policy[0], Codecs.List(Incidents.IncidentCodec).Arg(many))).Kind,
            Is.EqualTo(JacquardErrorKind.InvalidArgument),
            "over-deep single call");
        JacquardException notList = Support.Throws<JacquardException>(() => route.Call(results, policy[0], "not a list"));
        Assert.That(notList.Kind, Is.EqualTo(JacquardErrorKind.InvalidArgument));
        Assert.That(notList.Message, Does.Contain("argument[1]"));
        Assert.That(Support.Throws<JacquardException>(() => route.Call(results, policy[0])).Message, Does.Contain("expected 2 arguments"));
    }

    [Test]
    public void MissingHandlersRefuseBindingAndPureCallsNeedNoServices()
    {
        PreparedProgram fulfillment = Prepare(Example("fulfillment"));
        int calls = 0;
        Operations inventory = new Operations().On("booking-status", Codecs.Dynamic, Codecs.Dynamic, order =>
        {
            calls++;
            return Dynamic.Unit;
        });
        JacquardException missing = Support.Throws<JacquardException>(() => fulfillment.Function("fulfillment.place", new Dictionary<string, Operations> { ["Inventory"] = inventory }));
        Assert.That(missing.Kind, Is.EqualTo(JacquardErrorKind.Binding));
        Assert.That(missing.Message, Does.Contain("inventory.lookup-stock").And.Contain("dispatch.publish-booking"));
        Assert.That(calls, Is.EqualTo(0), "a handler ran during binding");
        Operations typo = new Operations().On("lookup_stok", Codecs.Text, Codecs.Unit, sku => Unit.Value);
        Assert.That(
            Support.Throws<JacquardException>(() => fulfillment.Function("fulfillment.place", new Dictionary<string, Operations> { ["Inventory"] = typo })).Message,
            Does.Contain("inventory has no operation lookup-stok"));

        // A service binding more names than the program has operations is refused from its count.
        var wide = new Operations();
        for (int i = 0; i < 100; i++)
        {
            wide.On("not-an-operation-" + i.ToString(CultureInfo.InvariantCulture), Codecs.Text, Codecs.Unit, sku => Unit.Value);
        }

        Assert.That(
            Support.Throws<JacquardException>(() => fulfillment.Function("fulfillment.place", new Dictionary<string, Operations> { ["Inventory"] = wide })).Message,
            Does.Contain("the service for inventory binds more operations than the program's "));
        fulfillment.Provide("Inventory", inventory);
        Function valid = fulfillment.Function("order.valid-lines");
        Assert.That(valid.Call(Codecs.Bool, Codecs.List(Codecs.Dynamic).Arg(Array.Empty<Dynamic>()), Codecs.List(Codecs.Text).Arg(Array.Empty<string>())), Is.True);
        Assert.That(Support.Throws<JacquardException>(() => fulfillment.Provide("Unknown", new Operations())).Kind, Is.EqualTo(JacquardErrorKind.Binding));
        Assert.That(Support.Throws<JacquardException>(() => fulfillment.Function("no-such-function")).Kind, Is.EqualTo(JacquardErrorKind.Binding));
        Assert.That(fulfillment.Functions, Does.Contain("fulfillment.place").And.Not.Contain("app.any"));
    }

    private static Operations Warehouse(Action reserved) => new Operations()
        .On("booking-status", Codecs.Dynamic, Codecs.Dynamic, order => Dynamic.VariantOf("BookingPending"))
        .On("lookup_stock", Codecs.Text, Codecs.Dynamic, sku => Dynamic.VariantOf("Stock", Dynamic.Of(20L)))
        .On("reserve-order", Codecs.Dynamic, Codecs.Dynamic, (context, order) =>
        {
            Assert.That(context.IsCancelled, Is.False);
            reserved();
            return Dynamic.VariantOf("Booked", Dynamic.Of("receipt-1"));
        })
        .On("publish-booking", Codecs.Text, Codecs.Text, Codecs.Unit, (string id, string receipt) => throw new IOException("synthetic after commit"));

    private static Dynamic Order(ProgramData data) =>
        data.Make("Order", Dynamic.Of("one"), Dynamic.ListOf(data.Make("Line", Dynamic.Of("sensor"), Dynamic.Of(2L))));

    private static Dictionary<string, Operations> Both(Operations service) => new() { ["Inventory"] = service, ["Dispatch"] = service };

    [Test]
    public void UnknownCompletionHappensOnceWithRawEvidence()
    {
        PreparedProgram fulfillment = Prepare(Example("fulfillment"));
        int reservations = 0;
        Function place = fulfillment.Function("fulfillment.place", Both(Warehouse(() => Interlocked.Increment(ref reservations))));
        Dynamic order = Order(fulfillment.Data);
        JacquardException error = Support.Throws<JacquardException>(() => place.Call(Codecs.Dynamic, order));
        Assert.That(error.Failure, Is.EqualTo(Failure.OperationFailed), error.Message);
        InvocationResult result = error.Result!;
        Assert.That(result.Code, Is.EqualTo("E1612"));
        Assert.That(result.HostFrames.Any(frame => Json.Of("effect_ok").Equals(frame.Get("kind"))), Is.True);
        Assert.That(reservations, Is.EqualTo(1));
        string rendered = place.Invoke(order).Render();
        Assert.That(rendered, Does.StartWith("fulfillment.place: E1612").And.Contain(". publish-booking"));
    }

    /// <summary>
    /// In a real invocation, a cancellation that lands after a request's
    /// arguments are decoded (through the test seam) keeps the handler from
    /// starting, and the invocation reports the abort.
    /// </summary>
    [Test]
    public void NoHandlerStartsOnceTheInvocationStoppedDuringDecoding()
    {
        PreparedProgram fulfillment = Prepare(Example("fulfillment"));
        int started = 0;
        Operations service = new Operations()
            .On("booking-status", Codecs.Dynamic, Codecs.Dynamic, order =>
            {
                started++;
                return Dynamic.VariantOf("BookingPending");
            })
            .On("lookup-stock", Codecs.Text, Codecs.Dynamic, sku => Dynamic.VariantOf("Stock", Dynamic.Of(20L)))
            .On("reserve-order", Codecs.Dynamic, Codecs.Dynamic, order => Dynamic.VariantOf("Booked", Dynamic.Of("r")))
            .On("publish-booking", Codecs.Text, Codecs.Text, Codecs.Unit, (id, receipt) => Unit.Value);
        Function place = fulfillment.Function("fulfillment.place", Both(service));
        using var cancel = new CancellationTokenSource();
        Operations.AfterDecodeHook = cancel.Cancel;
        Invocation invocation = place.Invoke(new Argument[] { Order(fulfillment.Data) }, cancel.Token);
        Operations.AfterDecodeHook = null;
        Assert.That(started, Is.EqualTo(0), "the handler started after the invocation stopped");
        Assert.That(Support.Reason(invocation.Raw), Is.EqualTo("aborted"), invocation.ToString());
        Assert.That(invocation.Raw.Requests, Has.Count.EqualTo(1));
    }

    /// <summary>A handler's context token is cancelled when the caller cancels while it runs; the handler sees it.</summary>
    [Test]
    public void AHandlersTokenIsCancelledWhenTheCallerCancels()
    {
        PreparedProgram fulfillment = Prepare(Example("fulfillment"));
        using var cancel = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        bool sawCancellation = false;
        Operations service = new Operations()
            .On("booking-status", Codecs.Dynamic, Codecs.Dynamic, (context, order) =>
            {
                entered.Set();
                sawCancellation = context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromMinutes(1));
                return Dynamic.VariantOf("BookingPending");
            })
            .On("lookup-stock", Codecs.Text, Codecs.Dynamic, sku => Dynamic.VariantOf("Stock", Dynamic.Of(20L)))
            .On("reserve-order", Codecs.Dynamic, Codecs.Dynamic, order => Dynamic.VariantOf("Booked", Dynamic.Of("r")))
            .On("publish-booking", Codecs.Text, Codecs.Text, Codecs.Unit, (id, receipt) => Unit.Value);
        Function place = fulfillment.Function("fulfillment.place", Both(service));
        Thread canceller = Support.After(TimeSpan.Zero, () =>
        {
            entered.Wait(TimeSpan.FromMinutes(1));
            cancel.Cancel();
        });
        Invocation invocation = place.Invoke(new Argument[] { Order(fulfillment.Data) }, cancel.Token);
        canceller.Join();
        Assert.That(Support.Reason(invocation.Raw), Is.EqualTo("aborted"));
        Assert.That(Support.Eventually(() => sawCancellation, TimeSpan.FromSeconds(10)), Is.True, "the handler's token was not cancelled");
    }

    internal sealed record Job(string Id, long Effort, long Value, long Latest);

    internal sealed record Assignment(string Id, long Slot);

    internal sealed record Plan(long Score, IReadOnlyList<Assignment> Assignments);

    internal abstract record Planning;

    internal sealed record Planned(Plan Plan) : Planning;

    internal sealed record InvalidPlan(string Reason) : Planning;

    private static readonly RecordCodec<Job> JobCodec = Codecs.Record(
        "Job",
        Codecs.Field("id", Codecs.Text, (Job job) => job.Id),
        Codecs.Field("effort", Codecs.Int, (Job job) => job.Effort),
        Codecs.Field("value", Codecs.Int, (Job job) => job.Value),
        Codecs.Field("latest", Codecs.Int, (Job job) => job.Latest),
        (id, effort, value, latest) => new Job(id, effort, value, latest));

    private static readonly UnionCodec<Planning> PlanningCodec = Codecs.Union<Planning>(
        Codecs.Record(
            "Planned",
            Codecs.Field(
                "plan",
                Codecs.Record(
                    "Plan",
                    Codecs.Field("score", Codecs.Int, (Plan plan) => plan.Score),
                    Codecs.Field(
                        "assignments",
                        Codecs.List(Codecs.Record("Assignment", Codecs.Field("id", Codecs.Text, (Assignment a) => a.Id), Codecs.Field("slot", Codecs.Int, (Assignment a) => a.Slot), (id, slot) => new Assignment(id, slot))),
                        (Plan plan) => plan.Assignments),
                    (score, assignments) => new Plan(score, assignments)),
                (Planned planned) => planned.Plan),
            plan => new Planned(plan)),
        Codecs.Record("InvalidPlan", Codecs.Field("reason", Codecs.Text, (InvalidPlan invalid) => invalid.Reason), reason => new InvalidPlan(reason)));

    private static long Optimum(IReadOnlyList<Job> jobs, long[] caps)
    {
        if (jobs.Count == 0)
        {
            return 0;
        }

        Job job = jobs[0];
        List<Job> rest = jobs.Skip(1).ToList();
        long best = Optimum(rest, caps);
        for (int slot = 0; slot < Math.Min(job.Latest, caps.Length); slot++)
        {
            if (caps[slot] >= job.Effort)
            {
                long[] next = (long[])caps.Clone();
                next[slot] -= job.Effort;
                best = Math.Max(best, job.Value + Optimum(rest, next));
            }
        }

        return best;
    }

    [Test]
    public void PlannerMatchesAnIndependentExhaustiveOracle()
    {
        Function solve = Prepare(Example("planner")).Function("planner.plan");
        for (long seed = 0; seed < 25; seed++)
        {
            var jobs = new List<Job>();
            for (long i = 0; i < (seed % 6) + 1; i++)
            {
                jobs.Add(new Job("job-" + i.ToString(CultureInfo.InvariantCulture), ((seed + (i * 3)) % 4) + 1, ((seed * 7) + (i * 11)) % 15, ((seed + i) % 3) + 1));
            }

            long[] all = { (seed % 5) + 1, (seed % 3) + 2, 3 };
            long[] caps = all.Take((int)(seed % 3) + 1).ToArray();
            if (solve.Call(PlanningCodec, Codecs.List(JobCodec).Arg(jobs), Codecs.List(Codecs.Int).Arg(caps)) is not Planned(Plan plan))
            {
                Assert.Fail("seed " + seed.ToString(CultureInfo.InvariantCulture) + ": not planned");
                return;
            }

            Assert.That(plan.Score, Is.EqualTo(Optimum(jobs, caps)), "seed " + seed.ToString(CultureInfo.InvariantCulture));
            long[] loads = new long[caps.Length];
            var seen = new HashSet<string>();
            long score = 0;
            foreach (Assignment assignment in plan.Assignments)
            {
                Job job = jobs.Single(candidate => candidate.Id == assignment.Id);
                Assert.That(seen.Add(assignment.Id) && assignment.Slot >= 1 && assignment.Slot <= Math.Min(job.Latest, caps.Length), Is.True, assignment.ToString());
                loads[assignment.Slot - 1] += job.Effort;
                score += job.Value;
            }

            for (int slot = 0; slot < caps.Length; slot++)
            {
                Assert.That(loads[slot], Is.LessThanOrEqualTo(caps[slot]), "seed " + seed.ToString(CultureInfo.InvariantCulture) + ": overloaded");
            }

            Assert.That(score, Is.EqualTo(plan.Score));
        }
    }

    [Test]
    public void CacheReuseInvalidationAndCorruption()
    {
        PreparedProgram quick = Prepare(Example("quickstart"));
        int commands = runtime.CoreCommands;
        PreparedProgram repeated = Prepare(Example("quickstart"));
        Assert.That(runtime.CoreCommands, Is.EqualTo(commands), "a warm preparation ran Core");
        Assert.That(repeated.Directory, Is.EqualTo(quick.Directory));
        string original = File.ReadAllText(Example("quickstart"));
        string changed = Source("changed.jac", original.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n-- changed\r\n");
        PreparedProgram program = Prepare(changed);
        string snapshot = File.ReadAllText(Path.Combine(program.Directory, "source.jac"));
        Assert.That(program.Directory != quick.Directory && snapshot.Contains("\r\n", StringComparison.Ordinal), Is.True, "not a byte-exact new entry");
        File.WriteAllText(Path.Combine(program.Directory, "store", "unexpected"), "corrupt");
        Assert.That(Support.Throws<JacquardException>(() => Prepare(changed), "a corrupt cache was accepted").Kind, Is.EqualTo(JacquardErrorKind.Preparation));
        Assert.That(Support.Throws<JacquardException>(() => Prepare(Source("rules.txt", string.Empty))).Kind, Is.EqualTo(JacquardErrorKind.Preparation));
    }

    /// <summary>
    /// Core accepts a parameter nesting 2,500 <c>Option</c>s. That function is
    /// unavailable, refused with a binding failure that names the depth
    /// limit, while <c>safe</c> works on both the cold and warm paths.
    /// </summary>
    [Test]
    public void ASignatureTooDeepToBindLeavesTheOtherFunctionsWorking()
    {
        const int depth = 2500;
        string source = Source("deep.jac", "deep : (" + string.Concat(Enumerable.Repeat("Option (", depth)) + "Int" + new string(')', depth) + ") ->{} Int\ndeep(x) = 0\nsafe : () ->{} Int\nsafe() = 1\n");
        int afterCold = -1;
        foreach (string path in new[] { "cold", "warm" })
        {
            PreparedProgram program = Prepare(source);
            if (afterCold < 0)
            {
                afterCold = runtime.CoreCommands;
            }
            else
            {
                Assert.That(runtime.CoreCommands, Is.EqualTo(afterCold), "the warm path ran Core");
            }

            Assert.That(program.Function("safe").Call(Codecs.Int), Is.EqualTo(1L), path);
            JacquardException refused = Support.Throws<JacquardException>(() => program.Function("deep"), path);
            Assert.That(refused.Kind, Is.EqualTo(JacquardErrorKind.Binding), path);
            Assert.That(refused.Message, Does.Contain("deeper than 64 levels, the protocol's value-depth limit"));
        }
    }

    /// <summary>Pointing <c>first</c> at <c>second</c>'s identity in program.json would make it return 2.</summary>
    [Test]
    public void ARedirectedNameInProgramJsonIsRefused()
    {
        string source = Source("pair.jac", "first : () ->{} Int\nfirst() = 1\n\nsecond : () ->{} Int\nsecond() = 2\n");
        PreparedProgram program = Prepare(source);
        Assert.That(program.Function("first").Call(Codecs.Int), Is.EqualTo(1L));
        string metadata = Path.Combine(program.Directory, "program.json");
        string original = File.ReadAllText(metadata);
        string from = "[\"first\",\"" + program.Data.Identity("first").Hex + "\"]";
        Assert.That(original, Does.Contain(from));
        File.WriteAllText(metadata, original.Replace(from, "[\"first\",\"" + program.Data.Identity("second").Hex + "\"]", StringComparison.Ordinal));
        JacquardException error = Support.Throws<JacquardException>(() => Prepare(source).Function("first").Call(Codecs.Int));
        Assert.That(error.Kind, Is.EqualTo(JacquardErrorKind.Preparation), error.Message);
        Assert.That(error.Message, Does.Contain("program.json bindings differ"));
        File.WriteAllText(metadata, original);
        Assert.That(Prepare(source).Function("first").Call(Codecs.Int), Is.EqualTo(1L));
    }

    [Test]
    public void FullIntRangeRealsAndNestedOptions()
    {
        PreparedProgram program = Prepare(Source(
            "values.jac",
            "value.int : (Int) ->{} Int\nvalue.int(x) = x\nvalue.real : (Real) ->{} Real\nvalue.real(x) = x\n"
            + "value.option : (Option (Option Int)) ->{} Option (Option Int)\nvalue.option(x) = x\n"));
        Function integer = program.Function("value.int");
        foreach (long value in new[] { Json.IntMin, Json.IntMax })
        {
            Assert.That(integer.Call(Codecs.Int, value), Is.EqualTo(value));
        }

        Assert.That(Support.Throws<JacquardException>(() => integer.Call(Codecs.Int, Json.IntMax + 1)).Kind, Is.EqualTo(JacquardErrorKind.InvalidArgument));
        Assert.That(Support.Throws<JacquardException>(() => integer.Call(Codecs.Int, 1.5)).Kind, Is.EqualTo(JacquardErrorKind.InvalidArgument));
        JacquardException narrow = Support.Throws<JacquardException>(() => integer.Call(Codecs.Int32, 1L << 40));
        Assert.That(narrow.Kind, Is.EqualTo(JacquardErrorKind.Binding));
        Assert.That(narrow.Result, Is.Not.Null);
        Function real = program.Function("value.real");
        foreach (double value in new[] { 1.0, -0.0, double.PositiveInfinity, double.NaN })
        {
            Assert.That(BitConverter.DoubleToInt64Bits(real.Call(Codecs.Real, value)), Is.EqualTo(BitConverter.DoubleToInt64Bits(value)));
        }

        Function option = program.Function("value.option");
        Codec<Option<Option<long>>> nested = Codecs.Option(Codecs.Option(Codecs.Int));
        foreach (Option<Option<long>> value in new[] { Option<Option<long>>.None, Option.Some(Option<long>.None), Option.Some(Option.Some(3L)) })
        {
            Assert.That(option.Call(nested, nested.Arg(value)), Is.EqualTo(value));
        }

        Dynamic inner = Dynamic.Some(Dynamic.None());
        Assert.That(option.Call(Codecs.Dynamic, inner), Is.EqualTo(inner));
    }

    [Test]
    public void SpawnFailuresAndCancellation()
    {
        PreparedProgram quick = Prepare(Example("quickstart"));
        Function shipping = quick.Function("shipping-cost");
        quick.SetWorkerCommand(new[] { "/no/such/jacquard" });
        JacquardException spawn = Support.Throws<JacquardException>(() => quick.Function("shipping-cost").Call(Codecs.Int, 10));
        Assert.That(spawn.Failure, Is.EqualTo(Failure.WorkerLost), spawn.Message);
        Assert.That(Support.Reason(spawn.Result!), Is.EqualTo("worker_spawn_failed"));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Invocation invocation = shipping.Invoke(new Argument[] { 10 }, cancel.Token);
        Assert.That(Support.Reason(invocation.Raw), Is.EqualTo("aborted"));
        Assert.That(invocation.Error, Is.Not.Null);
        Assert.That(Support.Throws<JacquardException>(() => shipping.Call(Codecs.Int, new Argument[] { 10 }, cancel.Token)).Failure, Is.EqualTo(Failure.WorkerLost));
    }

    [Test]
    public void OutputOverflowFailsOnceWithoutAdaptiveBatching()
    {
        PreparedProgram program = Prepare(Example("triage"));
        Function route = program.Function("triage.batch");
        List<Incidents.Incident> rows = Enumerable.Range(0, 29).Select(i => new Incidents.Incident(i.ToString(CultureInfo.InvariantCulture), "api", 5, 90, true, null)).ToList();
        JacquardException error = Support.Throws<JacquardException>(() => route.MapBatches(Incidents.TriageCodec, Incidents.IncidentCodec, rows, 29, new Argument[] { Policy(program) }));
        Assert.That(error.BatchIndex, Is.EqualTo(0), error.Message);
        Assert.That(error.Completed, Is.Empty);
        Assert.That(error.Result!.Code, Is.EqualTo("E1602"));
    }

    /// <summary>Batch 0 decodes to one unit; batch 1's result exceeds the limits (E1602), and the failure keeps batch 0's result.</summary>
    [Test]
    public void ALaterBatchFailureKeepsEarlierUnitResults()
    {
        string many = string.Join(",", Enumerable.Repeat("()", 40));
        Function units = Prepare(Source(
            "units.jac",
            "units : (List Int) ->{} List ()\nunits(xs) = match xs { | Cons(n, _) -> if eq(n, 1) then [()] else [" + many + "] | Nil -> [] }\n")).Function("units");
        Assert.That(units.MapBatches(Codecs.Unit, Codecs.Int, new[] { 1L }, 1), Is.EqualTo(new[] { Unit.Value }));
        JacquardException error = Support.Throws<JacquardException>(() => units.MapBatches(Codecs.Unit, Codecs.Int, new[] { 1L, 2L }, 1));
        Assert.That(error.BatchIndex, Is.EqualTo(1), error.Message);
        Assert.That(error.Completed, Is.EqualTo(new object[] { Unit.Value }));
        Assert.That(error.Kind, Is.EqualTo(JacquardErrorKind.Invocation));
        Assert.That(error.Result!.Code, Is.EqualTo("E1602"));
    }

    /// <summary>A dictionary that reports a huge count and fails when it is read.</summary>
    private sealed class HugeBindings : IReadOnlyDictionary<string, Operations>
    {
        public int Count => 1_000_000;

        public IEnumerable<string> Keys => throw new InvalidOperationException("a binding was read");

        public IEnumerable<Operations> Values => throw new InvalidOperationException("a binding was read");

        public Operations this[string key] => throw new InvalidOperationException("a binding was read");

        public bool ContainsKey(string key) => throw new InvalidOperationException("a binding was read");

        public bool TryGetValue(string key, out Operations value) => throw new InvalidOperationException("a binding was read");

        public IEnumerator<KeyValuePair<string, Operations>> GetEnumerator() => throw new InvalidOperationException("a binding was read");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>More bindings than the function has effects are refused from the count, before any binding is read.</summary>
    [Test]
    public void TooManyBindingsAreRefusedBeforeAnyIsRead()
    {
        PreparedProgram program = Prepare(Example("quickstart"));
        JacquardException error = Support.Throws<JacquardException>(() => program.Function("shipping-cost", new HugeBindings()));
        Assert.That(error.Kind, Is.EqualTo(JacquardErrorKind.Binding));
        Assert.That(error.Message, Is.EqualTo("shipping-cost: 1000000 bindings given, but it uses 0 effects"));
    }

    /// <summary>A result codec that throws with a huge message gives a short binding failure that keeps the raw result and the exception.</summary>
    [Test]
    public void AThrowingResultCodecKeepsTheResultAndQuotesAnExcerpt()
    {
        Function shipping = Prepare(Example("quickstart")).Function("shipping-cost");
        Invocation invocation = shipping.Invoke(35);
        Assert.That(invocation.Ok, Is.True);
        Codec<long> throwing = Codecs.Create<long>((encoder, value) => Codecs.Int.Encode(encoder, value), (decoder, value) => throw new InvalidOperationException(new string('X', 1_000_000)));
        JacquardException error = Support.Throws<JacquardException>(() => invocation.Decode(throwing));
        Assert.That(error.Kind, Is.EqualTo(JacquardErrorKind.Binding));
        Assert.That(error.Message, Has.Length.LessThan(400));
        Assert.That(error.Message, Does.EndWith("... (1000000 chars)"));
        Assert.That(error.Result!.Ok, Is.True, "the raw result was not kept");
        Assert.That(error.InnerException, Is.InstanceOf<InvalidOperationException>(), "the codec's exception is not kept");
        Assert.That(Support.Throws<JacquardException>(() => shipping.Call(throwing, 35)).InnerException, Is.InstanceOf<InvalidOperationException>());

        // A mapping failure with a null message is still a binding failure keeping the result.
        Codec<long> nullMessage = Codecs.Create<long>((encoder, value) => Codecs.Int.Encode(encoder, value), (decoder, value) => throw decoder.Mapping(null));
        JacquardException nulled = Support.Throws<JacquardException>(() => invocation.Decode(nullMessage));
        Assert.That(nulled.Kind, Is.EqualTo(JacquardErrorKind.Binding));
        Assert.That(nulled.Result!.Ok, Is.True);

        // A save path longer than any path is refused before the file system sees it.
        IOException save = Support.Throws<IOException>(() => invocation.Save("/" + new string('v', 1_000_000)));
        Assert.That(save.Message, Has.Length.LessThan(200));
    }

    [Test]
    public void ResultMappingFailureRetainsTheSuccessfulResult()
    {
        Invocation invocation = Prepare(Example("quickstart")).Function("shipping-cost").Invoke(35);
        Assert.That(invocation.Ok, Is.True);
        Assert.That(invocation.Error, Is.Null);
        JacquardException error = Support.Throws<JacquardException>(() => invocation.Decode(Codecs.Text));
        Assert.That(error.Kind, Is.EqualTo(JacquardErrorKind.Binding));
        Assert.That(error.Result!.Ok, Is.True, error.Message);
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string entry in Directory.EnumerateFileSystemEntries(from))
        {
            string target = Path.Combine(to, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                CopyTree(entry, target);
            }
            else
            {
                File.Copy(entry, target);
            }
        }
    }

    [Test]
    public void PreludeContentsKeyTheCacheAndConcurrentPreparationPublishesOnce()
    {
        PreparedProgram quick = Prepare(Example("quickstart"));
        string copy = Path.Combine(root, "prelude");
        CopyTree(Environment.GetEnvironmentVariable("JACQUARD_PRELUDE")!, copy);
        var copied = new Runtime(new RuntimeOptions { Prelude = copy });
        PreparedProgram before = copied.Prepare(new[] { Example("quickstart") }, Options);
        Assert.That(before.Directory, Is.EqualTo(quick.Directory), "an identical prelude copy should share the entry");
        string license = Path.Combine(copy, "LICENSE.md");
        File.AppendAllText(license, "\n");
        PreparedProgram after = copied.Prepare(new[] { Example("quickstart") }, Options);
        Assert.That(after.Directory, Is.Not.EqualTo(before.Directory), "a prelude change did not change the cache key");
        string source = Source("concurrent.jac", "shipping-cost : (Int) ->{} Int\nshipping-cost(total) = if lt(total, 50) then 5 else 0\n-- concurrent\n");
        Task<PreparedProgram>[] workers = Enumerable.Range(0, 2)
            .Select(_ => Task.Factory.StartNew(() => runtime.Prepare(new[] { source }, Options), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();
        Assert.That(Task.WaitAll(workers, TimeSpan.FromMinutes(2)), Is.True);
        Assert.That(workers[1].Result.Directory, Is.EqualTo(workers[0].Result.Directory));
        Assert.That(workers[1].Result.Function("shipping-cost").Call(Codecs.Int, 35), Is.EqualTo(5L));
        foreach (string entry in Directory.EnumerateFileSystemEntries(Path.Combine(root, "cache")))
        {
            Assert.That(Path.GetFileName(entry), Does.Not.StartWith("prepare-"), "staging directory left behind: " + entry);
        }
    }
}
