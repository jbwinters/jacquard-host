using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using JacquardLang.Host;
using JacquardLang.Host.Protocol;
using JacquardLang.Host.Sdk;

namespace JacquardLang.Host.Examples;

/// <summary>
/// Runs the repository's example Jacquard projects through the .NET SDK:
/// quickstart, planner, triage, and fulfillment. The same rules run through
/// the Python, TypeScript, Go, Rust, and Java SDKs.
/// </summary>
/// <remarks>
/// <code>
/// dotnet examples/JacquardExamples/bin/Debug/net8.0/JacquardExamples.dll \
///     [--workdir DIR] [--inspect PATH] [--fault NAME] [--deliver-pending] PROJECT [INPUT.json]
/// </code>
/// Core is found through <c>JACQUARD_BIN</c> and <c>JACQUARD_PRELUDE</c>. The
/// rules are read from the repository's <c>examples/</c> directory
/// (<c>JACQUARD_EXAMPLES</c>, else <c>examples/</c> or <c>../examples/</c>
/// under the working directory). The exit status is 1 for a Jacquard or SDK
/// failure and 2 for usage or other errors.
/// </remarks>
public static class Examples
{
    /// <summary>The example projects in presentation order.</summary>
    public static IReadOnlyList<string> Projects { get; } = new[] { "quickstart", "planner", "triage", "fulfillment" };

    /// <summary>The faults the fulfillment store can inject.</summary>
    private static readonly string[] Faults = { string.Empty, "publish-failed", "after-commit", "after-delivery" };

    /// <summary>Runs one command line and exits with its status.</summary>
    public static int Main(string[] args)
    {
        try
        {
            Run(args, Console.Out);
            return 0;
        }
        catch (JacquardException failure)
        {
            Console.Out.Flush();
            Console.Error.WriteLine(failure.Message);
            return 1;
        }
        catch (Exception failure) when (failure is UsageException or IOException or UnauthorizedAccessException)
        {
            Console.Out.Flush();
            Console.Error.WriteLine(failure.Message);
            return 2;
        }
    }

    /// <summary>The repository's example projects.</summary>
    internal static string ExamplesDirectory(string? configured)
    {
        configured ??= Environment.GetEnvironmentVariable("JACQUARD_EXAMPLES");
        if (!string.IsNullOrEmpty(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (string candidate in new[] { "examples", Path.Combine("..", "examples") })
        {
            if (File.Exists(Path.Combine(candidate, "quickstart", "rules.jac")))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new UsageException("cannot find the examples directory; run from the repository or dotnet/, or set JACQUARD_EXAMPLES");
    }

    // JSON input and output.
    private static Json Field(JsonObject json, string key) => json.Get(key) ?? throw new UsageException("input: missing " + key);

    private static string TextOf(JsonObject json, string key) => Field(json, key).AsString() ?? throw new UsageException("input: " + key + " must be text");

    private static long IntegerOf(JsonObject json, string key) => Field(json, key).AsInt() ?? throw new UsageException("input: " + key + " must be an integer");

    private static bool BoolOf(JsonObject json, string key) =>
        Field(json, key) is JsonBool value ? value.Value : throw new UsageException("input: " + key + " must be true or false");

    private static List<JsonObject> Objects(JsonObject json, string key)
    {
        IReadOnlyList<Json> items = Field(json, key).AsArray() ?? throw new UsageException("input: " + key + " must be a list");
        return items.Select(item => item.AsObject() ?? throw new UsageException("input: " + key + " must hold objects")).ToList();
    }

    private static JsonObject Tagged(string tag, params (string Key, Json Value)[] pairs) =>
        Json.Object(pairs.Append(("$tag", Json.Of(tag))).ToArray());

    internal static string Compact(Json value) => JsonCodec.Stringify(value, 16);

    private static void Pretty(Json value, int indent, StringBuilder output)
    {
        string pad = new(' ', indent * 2);
        if (value is JsonArray array && array.Items.Count > 0)
        {
            output.Append("[\n");
            for (int i = 0; i < array.Items.Count; i++)
            {
                output.Append(pad).Append("  ");
                Pretty(array.Items[i], indent + 1, output);
                output.Append(i + 1 < array.Items.Count ? ",\n" : "\n");
            }

            output.Append(pad).Append(']');
        }
        else if (value is JsonObject json && json.Count > 0)
        {
            output.Append("{\n");
            int i = 0;
            foreach (KeyValuePair<string, Json> field in json)
            {
                output.Append(pad).Append("  ").Append(Compact(Json.Of(field.Key))).Append(": ");
                Pretty(field.Value, indent + 1, output);
                output.Append(++i < json.Count ? ",\n" : "\n");
            }

            output.Append(pad).Append('}');
        }
        else
        {
            output.Append(Compact(value));
        }
    }

    private static void PrintJson(TextWriter output, Json value)
    {
        var text = new StringBuilder();
        Pretty(value, 0, text);
        output.Write(text.Append('\n').ToString());
    }

    // Planner.

    /// <summary>A job to place.</summary>
    public sealed record Job(string Id, long Effort, long Value, long Latest);

    /// <summary>A job placed in a slot.</summary>
    public sealed record Assignment(string Id, long Slot);

    /// <summary>A plan and its score.</summary>
    public sealed record Plan(long Score, IReadOnlyList<Assignment> Assignments);

    /// <summary>The planner's result.</summary>
    public abstract record Planning;

    /// <summary>A valid plan.</summary>
    public sealed record Planned(Plan Plan) : Planning;

    /// <summary>Invalid input.</summary>
    public sealed record InvalidPlan(string Reason) : Planning;

    internal static readonly RecordCodec<Job> JobCodec = Codecs.Record(
        "Job",
        Codecs.Field("id", Codecs.Text, (Job job) => job.Id),
        Codecs.Field("effort", Codecs.Int, (Job job) => job.Effort),
        Codecs.Field("value", Codecs.Int, (Job job) => job.Value),
        Codecs.Field("latest", Codecs.Int, (Job job) => job.Latest),
        (id, effort, value, latest) => new Job(id, effort, value, latest));

    private static readonly RecordCodec<Assignment> AssignmentCodec = Codecs.Record(
        "Assignment",
        Codecs.Field("id", Codecs.Text, (Assignment assignment) => assignment.Id),
        Codecs.Field("slot", Codecs.Int, (Assignment assignment) => assignment.Slot),
        (id, slot) => new Assignment(id, slot));

    private static readonly RecordCodec<Plan> PlanCodec = Codecs.Record(
        "Plan",
        Codecs.Field("score", Codecs.Int, (Plan plan) => plan.Score),
        Codecs.Field("assignments", Codecs.List(AssignmentCodec), (Plan plan) => plan.Assignments),
        (score, assignments) => new Plan(score, assignments));

    internal static readonly UnionCodec<Planning> PlanningCodec = Codecs.Union<Planning>(
        Codecs.Record("Planned", Codecs.Field("plan", PlanCodec, (Planned planned) => planned.Plan), plan => new Planned(plan)),
        Codecs.Record("InvalidPlan", Codecs.Field("reason", Codecs.Text, (InvalidPlan invalid) => invalid.Reason), reason => new InvalidPlan(reason)));

    private static JsonObject PlanningJson(Planning planning) => planning switch
    {
        Planned(Plan plan) => Tagged("Planned", ("plan", Json.Object(
            ("assignments", Json.Of(plan.Assignments.Select(assignment => (Json)Json.Object(("id", Json.Of(assignment.Id)), ("slot", Json.Of(assignment.Slot)))))),
            ("score", Json.Of(plan.Score))))),
        InvalidPlan(string reason) => Tagged("InvalidPlan", ("reason", Json.Of(reason))),
        _ => throw new InvalidOperationException("unknown planning result"),
    };

    // Triage.

    /// <summary>When an incident pages, and when it is stale.</summary>
    public sealed record TriagePolicy(long Page, long Stale);

    /// <summary>One incident; a missing owner is null.</summary>
    public sealed record Incident(string Id, string Service, long Severity, long Age, bool Visible, string? Owner);

    /// <summary>Members map by convention: <c>PageNow</c> is the constructor <c>PageNow</c>.</summary>
    public enum Urgency
    {
        /// <summary>Page someone now.</summary>
        PageNow,

        /// <summary>Work on it today.</summary>
        WorkToday,

        /// <summary>Only watch it.</summary>
        WatchOnly,
    }

    /// <summary>The triage result.</summary>
    public abstract record Triage;

    /// <summary>A routed incident.</summary>
    public sealed record Routed(string Id, string Team, Urgency Urgency, IReadOnlyList<string> Reasons) : Triage;

    /// <summary>An invalid incident.</summary>
    public sealed record InvalidIncident(string Id) : Triage;

    internal static readonly RecordCodec<TriagePolicy> PolicyCodec = Codecs.Record(
        "TriagePolicy",
        Codecs.Field("page", Codecs.Int, (TriagePolicy policy) => policy.Page),
        Codecs.Field("stale", Codecs.Int, (TriagePolicy policy) => policy.Stale),
        (page, stale) => new TriagePolicy(page, stale));

    internal static readonly RecordCodec<Incident> IncidentCodec = Codecs.Record(
        "Incident",
        Codecs.Field("id", Codecs.Text, (Incident incident) => incident.Id),
        Codecs.Field("service", Codecs.Text, (Incident incident) => incident.Service),
        Codecs.Field("severity", Codecs.Int, (Incident incident) => incident.Severity),
        Codecs.Field("age", Codecs.Int, (Incident incident) => incident.Age),
        Codecs.Field("visible", Codecs.Bool, (Incident incident) => incident.Visible),
        Codecs.Field("owner", Codecs.Nullable(Codecs.Text), (Incident incident) => incident.Owner),
        (id, service, severity, age, visible, owner) => new Incident(id, service, severity, age, visible, owner));

    internal static readonly UnionCodec<Triage> TriageCodec = Codecs.Union<Triage>(
        Codecs.Record(
            "Routed",
            Codecs.Field("id", Codecs.Text, (Routed routed) => routed.Id),
            Codecs.Field("team", Codecs.Text, (Routed routed) => routed.Team),
            Codecs.Field("urgency", Codecs.Enum<Urgency>(), (Routed routed) => routed.Urgency),
            Codecs.Field("reasons", Codecs.List(Codecs.Text), (Routed routed) => routed.Reasons),
            (id, team, urgency, reasons) => new Routed(id, team, urgency, reasons)),
        Codecs.Record("InvalidIncident", Codecs.Field("id", Codecs.Text, (InvalidIncident invalid) => invalid.Id), id => new InvalidIncident(id)));

    private static JsonObject TriageJson(Triage triage) => triage switch
    {
        Routed routed => Tagged(
            "Routed",
            ("id", Json.Of(routed.Id)),
            ("team", Json.Of(routed.Team)),
            ("urgency", Json.Of(routed.Urgency switch
            {
                Urgency.PageNow => "PAGE",
                Urgency.WorkToday => "TODAY",
                _ => "WATCH",
            })),
            ("reasons", Json.Of(routed.Reasons.Select(reason => (Json)Json.Of(reason))))),
        InvalidIncident(string id) => Tagged("InvalidIncident", ("id", Json.Of(id))),
        _ => throw new InvalidOperationException("unknown triage result"),
    };

    // Fulfillment.

    /// <summary>One order line.</summary>
    public sealed record Line(string Sku, long Units);

    /// <summary>An order.</summary>
    public sealed record Order(string Id, IReadOnlyList<Line> Lines);

    /// <summary>A stock lookup: <c>Stock</c> is also the type's name, so the .NET case is <see cref="Available"/>, mapped explicitly.</summary>
    public abstract record Stock;

    /// <summary>Units available.</summary>
    public sealed record Available(long Units) : Stock;

    /// <summary>An unknown SKU.</summary>
    public sealed record UnknownSku : Stock;

    /// <summary>A reservation.</summary>
    public abstract record Reservation;

    /// <summary>No booking yet.</summary>
    public sealed record BookingPending : Reservation;

    /// <summary>Booked, with its receipt.</summary>
    public sealed record Booked(string Receipt) : Reservation;

    /// <summary>The same order was booked before.</summary>
    public sealed record RepeatBooking(string Receipt) : Reservation;

    /// <summary>Another order was booked under this id.</summary>
    public sealed record BookingConflict : Reservation;

    /// <summary>Stock changed before the commit.</summary>
    public sealed record StockChanged : Reservation;

    /// <summary>The fulfillment result.</summary>
    public abstract record Fulfillment;

    /// <summary>Fulfilled, with its receipt.</summary>
    public sealed record Fulfilled(string Receipt) : Fulfillment;

    /// <summary>A repeated order.</summary>
    public sealed record DuplicateOrder(string Receipt) : Fulfillment;

    /// <summary>A rejected order.</summary>
    public sealed record OrderRejected(string Reason) : Fulfillment;

    private static readonly RecordCodec<Line> LineCodec = Codecs.Record(
        "Line",
        Codecs.Field("sku", Codecs.Text, (Line line) => line.Sku),
        Codecs.Field("units", Codecs.Int, (Line line) => line.Units),
        (sku, units) => new Line(sku, units));

    internal static readonly RecordCodec<Order> OrderCodec = Codecs.Record(
        "Order",
        Codecs.Field("id", Codecs.Text, (Order order) => order.Id),
        Codecs.Field("lines", Codecs.List(LineCodec), (Order order) => order.Lines),
        (id, lines) => new Order(id, lines));

    private static readonly UnionCodec<Stock> StockCodec = Codecs.Union<Stock>(
        Codecs.Record("Stock", Codecs.Field("units", Codecs.Int, (Available available) => available.Units), units => new Available(units)),
        Codecs.Record("UnknownSku", () => new UnknownSku()));

    private static readonly UnionCodec<Reservation> ReservationCodec = Codecs.Union<Reservation>(
        Codecs.Record("BookingPending", () => new BookingPending()),
        Codecs.Record("Booked", Codecs.Field("receipt", Codecs.Text, (Booked booked) => booked.Receipt), receipt => new Booked(receipt)),
        Codecs.Record("RepeatBooking", Codecs.Field("receipt", Codecs.Text, (RepeatBooking repeat) => repeat.Receipt), receipt => new RepeatBooking(receipt)),
        Codecs.Record("BookingConflict", () => new BookingConflict()),
        Codecs.Record("StockChanged", () => new StockChanged()));

    internal static readonly UnionCodec<Fulfillment> FulfillmentCodec = Codecs.Union<Fulfillment>(
        Codecs.Record("Fulfilled", Codecs.Field("receipt", Codecs.Text, (Fulfilled done) => done.Receipt), receipt => new Fulfilled(receipt)),
        Codecs.Record("DuplicateOrder", Codecs.Field("receipt", Codecs.Text, (DuplicateOrder repeat) => repeat.Receipt), receipt => new DuplicateOrder(receipt)),
        Codecs.Record("OrderRejected", Codecs.Field("reason", Codecs.Text, (OrderRejected rejected) => rejected.Reason), reason => new OrderRejected(reason)));

    private static JsonObject FulfillmentJson(Fulfillment fulfillment) => fulfillment switch
    {
        Fulfilled(string receipt) => Tagged("Fulfilled", ("receipt", Json.Of(receipt))),
        DuplicateOrder(string receipt) => Tagged("DuplicateOrder", ("receipt", Json.Of(receipt))),
        OrderRejected(string reason) => Tagged("OrderRejected", ("reason", Json.Of(reason))),
        _ => throw new InvalidOperationException("unknown fulfillment result"),
    };

    /// <summary>Binds <c>fulfillment.place</c> to a shared warehouse serving both effects.</summary>
    public static Function BindFulfillment(PreparedProgram program, Warehouse warehouse)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(warehouse);
        Operations service = new Operations()
            .On("booking-status", OrderCodec, ReservationCodec, warehouse.BookingStatus)
            .On("lookup-stock", Codecs.Text, StockCodec, warehouse.LookupStock)
            .On("reserve-order", OrderCodec, ReservationCodec, warehouse.ReserveOrder)
            .On("publish-booking", Codecs.Text, Codecs.Text, Codecs.Unit, warehouse.PublishBooking);
        return program.Function("fulfillment.place", new Dictionary<string, Operations> { ["Inventory"] = service, ["Dispatch"] = service });
    }

    private static void Save(Invocation invocation, string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            invocation.Save(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new UsageException(error.Message);
        }
    }

    private static byte[] Read(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new UsageException(error.Message);
        }
    }

    /// <summary>Runs one command line, writing its output to <paramref name="output"/>.</summary>
    /// <exception cref="JacquardException">A Jacquard or SDK failure (exit status 1).</exception>
    /// <exception cref="UsageException">A usage, input, or I/O failure (exit status 2).</exception>
    [SuppressMessage("Maintainability", "CA1502", Justification = "One command line, one case per project, as in the other SDKs' commands.")]
    public static void Run(IReadOnlyList<string> arguments, TextWriter output, string? examples = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        string workdir = Path.Combine(".scratch", "dotnet-examples");
        string? inspect = null;
        string fault = string.Empty;
        bool deliverPending = false;
        var positional = new List<string>();
        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];
            switch (argument)
            {
                case "--workdir" or "--inspect" or "--fault":
                    if (i + 1 >= arguments.Count)
                    {
                        throw new UsageException(argument + " needs a value");
                    }

                    string value = arguments[++i];
                    if (argument == "--workdir")
                    {
                        workdir = value;
                    }
                    else if (argument == "--inspect")
                    {
                        inspect = value;
                    }
                    else
                    {
                        fault = value;
                    }

                    break;
                case "--deliver-pending":
                    deliverPending = true;
                    break;
                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new UsageException("unknown flag " + argument);
                    }

                    positional.Add(argument);
                    break;
            }
        }

        string project = positional.Count == 0 ? string.Empty : positional[0];
        if (!Projects.Contains(project) || positional.Count > 2)
        {
            throw new UsageException("choose quickstart, triage, planner, or fulfillment");
        }

        if (!Faults.Contains(fault))
        {
            throw new UsageException("unknown fault");
        }

        string root = Path.GetFullPath(workdir);
        string directory = ExamplesDirectory(examples);
        string rules = Path.Combine(directory, project, "rules.jac");
        var runtime = new Runtime();
        PreparedProgram program = runtime.Prepare(new[] { rules }, new PrepareOptions { CacheDir = Path.Combine(root, "programs") });
        if (project == "quickstart")
        {
            Function shipping = program.Function("shipping-cost");
            foreach (long total in new long[] { 35, 75 })
            {
                long cost = shipping.Call(Codecs.Int, total);
                output.Write("Order total " + total.ToString(CultureInfo.InvariantCulture) + ": shipping " + cost.ToString(CultureInfo.InvariantCulture) + "\n");
            }

            return;
        }

        string inputPath = positional.Count > 1 ? positional[1] : Path.Combine(directory, project, "input.json");
        JsonObject input;
        try
        {
            input = JsonCodec.Decode(Read(inputPath), 16).AsObject() ?? throw new UsageException("input must be a JSON object");
        }
        catch (ProtocolException error)
        {
            throw new UsageException("input: " + error.Message);
        }

        switch (project)
        {
            case "planner":
            {
                List<Job> jobs = Objects(input, "jobs")
                    .Select(job => new Job(TextOf(job, "id"), IntegerOf(job, "effort"), IntegerOf(job, "value"), IntegerOf(job, "latest")))
                    .ToList();
                IReadOnlyList<Json> capacityItems = Field(input, "capacities").AsArray() ?? throw new UsageException("input: capacities must be a list");
                List<long> capacities = capacityItems.Select(capacity => capacity.AsInt() ?? throw new UsageException("input: capacities must be integers")).ToList();
                Invocation invocation = program.Function("planner.plan").Invoke(Codecs.List(JobCodec).Arg(jobs), Codecs.List(Codecs.Int).Arg(capacities));
                Save(invocation, inspect);
                PrintJson(output, PlanningJson(invocation.Decode(PlanningCodec)));
                output.Write(invocation.Render() + "\n");
                break;
            }

            case "triage":
            {
                JsonObject policyObject = Field(input, "policy").AsObject() ?? throw new UsageException("input: policy must be an object");
                var policy = new TriagePolicy(IntegerOf(policyObject, "page"), IntegerOf(policyObject, "stale"));
                var incidents = new List<Incident>();
                foreach (JsonObject incident in Objects(input, "incidents"))
                {
                    // A missing owner is null, as in the Go command.
                    Json owner = incident.Get("owner") ?? Json.Null;
                    string? ownerValue = owner is JsonNull ? null : owner.AsString() ?? throw new UsageException("input: owner must be text or null");
                    incidents.Add(new Incident(
                        TextOf(incident, "id"), TextOf(incident, "service"), IntegerOf(incident, "severity"), IntegerOf(incident, "age"), BoolOf(incident, "visible"), ownerValue));
                }

                Function route = program.Function("triage.batch");
                IReadOnlyList<Triage> results;
                if (inspect is null)
                {
                    results = route.MapBatches(TriageCodec, IncidentCodec, incidents, 8, new[] { PolicyCodec.Arg(policy) });
                }
                else
                {
                    var collected = new List<Triage>();
                    for (int start = 0, index = 0; start < incidents.Count; start += 8, index++)
                    {
                        List<Incident> chunk = incidents.GetRange(start, Math.Min(8, incidents.Count - start));
                        Invocation invocation = route.Invoke(PolicyCodec.Arg(policy), Codecs.List(IncidentCodec).Arg(chunk));
                        Save(invocation, inspect + "." + index.ToString(CultureInfo.InvariantCulture) + ".json");
                        collected.AddRange(invocation.Decode(Codecs.List(TriageCodec)));
                    }

                    results = collected;
                }

                PrintJson(output, Json.Of(results.Select(result => (Json)TriageJson(result))));
                break;
            }

            default:
            {
                List<Line> lines = Objects(input, "lines").Select(line => new Line(TextOf(line, "sku"), IntegerOf(line, "units"))).ToList();
                var order = new Order(TextOf(input, "id"), lines);
                Warehouse warehouse = Warehouse.Open(Path.Combine(root, "inventory.json"), fault);
                try
                {
                    if (deliverPending)
                    {
                        int delivered;
                        try
                        {
                            delivered = warehouse.DeliverPending();
                        }
                        catch (Exception error) when (error is HandlerException or IOException or UnauthorizedAccessException)
                        {
                            // Like the Go command, a failed delivery prints no count.
                            throw new UsageException("delivery failed: " + Describe(error));
                        }

                        output.Write(Compact(Json.Object(("delivered", Json.Of(delivered)))) + "\n");
                        return;
                    }

                    Invocation invocation = BindFulfillment(program, warehouse).Invoke(OrderCodec.Arg(order));
                    Save(invocation, inspect);
                    JacquardException? failure = null;
                    try
                    {
                        PrintJson(output, FulfillmentJson(invocation.Decode(FulfillmentCodec)));
                    }
                    catch (JacquardException error)
                    {
                        failure = error;
                    }

                    output.Write(invocation.Render() + "\n");
                    if (failure is not null)
                    {
                        throw failure;
                    }
                }
                finally
                {
                    output.Write(Compact(warehouse.Summary()) + "\n");
                }

                break;
            }
        }
    }

    private static string Describe(Exception error) =>
        error is HandlerException failure ? failure.Category.WireName() + ": " + failure.Message : "completion unknown";

    /// <summary>The canonical payload of an order: its lines sorted by SKU.</summary>
    internal static string Payload(Order order) =>
        Compact(Json.Of(order.Lines.OrderBy(line => line.Sku, StringComparer.Ordinal)
            .Select(line => (Json)Json.Of(new Json[] { Json.Of(line.Sku), Json.Of(line.Units) }))));

    internal static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// A single-process synthetic Inventory and Dispatch service. Bookings and
    /// outbox entries are committed together as one atomic JSON snapshot;
    /// deliveries are deduplicated by receipt. It has no cross-process
    /// locking or power-loss durability claim. Its methods are synchronized,
    /// because each handler runs on its own thread.
    /// </summary>
    public sealed class Warehouse
    {
        internal const string InventorySchema = "jacquard-example-inventory-v1";
        private static long next;
        private readonly object sync = new();
        private readonly string path;
        private readonly string fault;
        private Inventory state;

        private Warehouse(string path, string fault, Inventory state)
        {
            this.path = path;
            this.fault = fault;
            this.state = state;
        }

        private sealed record Booking(string Id, string Payload, string Receipt);

        private sealed record Outbox(string Id, string Receipt, bool Delivered);

        private sealed record Inventory(SortedDictionary<string, long> Stock, List<string> Skus, List<Booking> Bookings, List<Outbox> Outbox, List<string> Deliveries)
        {
            internal JsonObject ToJson() => Json.Object(
                ("schema", Json.Of(InventorySchema)),
                ("stock", Json.Of(Skus.Select(sku => (Json)Json.Object(("sku", Json.Of(sku)), ("available", Json.Of(Stock[sku])))))),
                ("bookings", Json.Of(Bookings.Select(booking => (Json)Json.Object(
                    ("id", Json.Of(booking.Id)), ("payload", Json.Of(booking.Payload)), ("receipt", Json.Of(booking.Receipt)))))),
                ("outbox", Json.Of(Outbox.Select(entry => (Json)Json.Object(
                    ("id", Json.Of(entry.Id)), ("receipt", Json.Of(entry.Receipt)), ("delivered", Json.Of(entry.Delivered)))))),
                ("deliveries", Json.Of(Deliveries.Select(delivery => (Json)Json.Of(delivery)))));

            [SuppressMessage("Design", "CA1031", Justification = "Any malformed snapshot is reported as a version mismatch.")]
            internal static Inventory? FromJson(Json value)
            {
                if (value.AsObject() is not { } json || !Json.Of(InventorySchema).Equals(json.Get("schema")))
                {
                    return null;
                }

                try
                {
                    var stock = new SortedDictionary<string, long>(StringComparer.Ordinal);
                    var skus = new List<string>();
                    foreach (Json item in json["stock"].AsArray()!)
                    {
                        string sku = item.AsObject()!["sku"].AsString()!;
                        stock[sku] = item.AsObject()!["available"].AsInt()!.Value;
                        skus.Add(sku);
                    }

                    List<Booking> bookings = json["bookings"].AsArray()!.Select(item => item.AsObject()!)
                        .Select(fields => new Booking(fields["id"].AsString()!, fields["payload"].AsString()!, fields["receipt"].AsString()!)).ToList();
                    List<Outbox> outbox = json["outbox"].AsArray()!.Select(item => item.AsObject()!)
                        .Select(fields => new Outbox(fields["id"].AsString()!, fields["receipt"].AsString()!, ((JsonBool)fields["delivered"]).Value)).ToList();
                    List<string> deliveries = json["deliveries"].AsArray()!.Select(item => item.AsString()!).ToList();
                    return new Inventory(stock, skus, bookings, outbox, deliveries);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            internal Inventory Copy() => new(
                new SortedDictionary<string, long>(Stock, StringComparer.Ordinal), new List<string>(Skus), new List<Booking>(Bookings), new List<Outbox>(Outbox), new List<string>(Deliveries));
        }

        /// <summary>Loads the snapshot at <paramref name="path"/> or starts synthetic stock.</summary>
        /// <exception cref="UsageException">The snapshot cannot be read or is not this version.</exception>
        public static Warehouse Open(string path, string fault)
        {
            ArgumentNullException.ThrowIfNull(path);
            ArgumentNullException.ThrowIfNull(fault);
            Inventory state;
            try
            {
                byte[] raw = File.ReadAllBytes(path);
                Inventory? parsed;
                try
                {
                    parsed = Inventory.FromJson(JsonCodec.Decode(raw, 16));
                }
                catch (ProtocolException)
                {
                    parsed = null;
                }

                state = parsed ?? throw new UsageException("inventory snapshot version mismatch");
            }
            catch (FileNotFoundException)
            {
                state = new Inventory(
                    new SortedDictionary<string, long>(StringComparer.Ordinal) { ["sensor"] = 20, ["cable"] = 30, ["battery"] = 15 },
                    new List<string> { "sensor", "cable", "battery" },
                    new List<Booking>(),
                    new List<Outbox>(),
                    new List<string>());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new UsageException(error.Message);
            }

            return new Warehouse(path, fault, state.Copy());
        }

        /// <summary>
        /// Commits <paramref name="next"/>: a fresh owner-only file, created
        /// exclusively, then renamed over the snapshot. Only a file this call
        /// created is ever removed, and not once it was renamed.
        /// </summary>
        private void Commit(Inventory nextState)
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(parent);
            byte[] text = Encoding.UTF8.GetBytes(Compact(nextState.ToJson()) + "\n");
            string temporary = Path.Combine(parent, Path.GetFileName(path) + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-"
                + Interlocked.Increment(ref next).ToString(CultureInfo.InvariantCulture) + ".tmp");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            // Throws if the name exists, so nothing below runs for a file this call did not make.
            var stream = new FileStream(temporary, options);
            bool moved = false;
            try
            {
                using (stream)
                {
                    stream.Write(text);
                }

                File.Move(temporary, path, overwrite: true);
                moved = true;
            }
            finally
            {
                if (!moved)
                {
                    File.Delete(temporary);
                }
            }

            state = nextState;
        }

        private Booking? Find(string id) => state.Bookings.FirstOrDefault(booking => booking.Id == id);

        /// <summary>Serves <c>Inventory.booking-status</c>.</summary>
        public Reservation BookingStatus(Order order)
        {
            ArgumentNullException.ThrowIfNull(order);
            lock (sync)
            {
                Booking? previous = Find(order.Id);
                if (previous is null)
                {
                    return new BookingPending();
                }

                return previous.Payload == Payload(order) ? new RepeatBooking(previous.Receipt) : new BookingConflict();
            }
        }

        /// <summary>Serves <c>Inventory.lookup-stock</c>.</summary>
        public Stock LookupStock(string sku)
        {
            lock (sync)
            {
                return state.Stock.TryGetValue(sku, out long available) ? new Available(available) : new UnknownSku();
            }
        }

        /// <summary>Serves <c>Inventory.reserve-order</c>, committing booking and outbox together.</summary>
        /// <exception cref="HandlerException">An invalid reservation, or the injected fault after the commit.</exception>
        public Reservation ReserveOrder(Order order)
        {
            ArgumentNullException.ThrowIfNull(order);
            lock (sync)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                bool valid = order.Id.Length > 0 && order.Lines.Count >= 1 && order.Lines.Count <= 20
                    && order.Lines.All(line => seen.Add(line.Sku) && line.Units >= 1 && line.Units <= 20);
                if (!valid)
                {
                    throw HandlerException.Refused("invalid synthetic reservation");
                }

                if (Find(order.Id) is not null)
                {
                    return BookingStatus(order);
                }

                Inventory nextState = state.Copy();
                foreach (Line line in order.Lines)
                {
                    if (!nextState.Stock.TryGetValue(line.Sku, out long available) || available < line.Units)
                    {
                        return new StockChanged();
                    }

                    nextState.Stock[line.Sku] = available - line.Units;
                }

                string body = Payload(order);
                string receipt = Sha256(order.Id + "\0" + body);
                nextState.Bookings.Add(new Booking(order.Id, body, receipt));
                nextState.Outbox.Add(new Outbox(order.Id, receipt, false));
                Commit(nextState);
                if (fault == "after-commit")
                {
                    throw HandlerException.CompletionUnknown("synthetic fault after the booking committed");
                }

                return new Booked(receipt);
            }
        }

        /// <summary>Serves <c>Dispatch.publish-booking</c>.</summary>
        /// <exception cref="HandlerException">The injected delivery failure, no matching entry, or the fault after the delivery.</exception>
        public Unit PublishBooking(string id, string receipt)
        {
            lock (sync)
            {
                if (fault == "publish-failed")
                {
                    throw HandlerException.OutsideFailed("synthetic delivery unavailable; outbox retained");
                }

                Inventory nextState = state.Copy();
                int index = nextState.Outbox.FindIndex(entry => entry.Receipt == receipt && entry.Id == id);
                if (index < 0)
                {
                    throw HandlerException.Refused("no matching outbox entry");
                }

                nextState.Outbox[index] = new Outbox(id, receipt, true);
                if (!nextState.Deliveries.Contains(receipt))
                {
                    nextState.Deliveries.Add(receipt);
                }

                Commit(nextState);
                if (fault == "after-delivery")
                {
                    throw HandlerException.CompletionUnknown("synthetic fault after the delivery committed");
                }

                return Unit.Value;
            }
        }

        /// <summary>Publishes every undelivered outbox entry explicitly, returning how many were delivered.</summary>
        public int DeliverPending()
        {
            lock (sync)
            {
                List<Outbox> pending = state.Outbox.Where(entry => !entry.Delivered).ToList();
                foreach (Outbox entry in pending)
                {
                    PublishBooking(entry.Id, entry.Receipt);
                }

                return pending.Count;
            }
        }

        /// <summary>Counts bookings, pending deliveries, and deliveries.</summary>
        public JsonObject Summary()
        {
            lock (sync)
            {
                return Json.Object(
                    ("bookings", Json.Of(state.Bookings.Count)),
                    ("deliveries", Json.Of(state.Deliveries.Count)),
                    ("pendingDeliveries", Json.Of(state.Outbox.Count(entry => !entry.Delivered))));
            }
        }
    }
}

/// <summary>A usage, input, or I/O failure: exit status 2.</summary>
public sealed class UsageException : Exception
{
    /// <summary>A failure with a message.</summary>
    public UsageException()
    {
    }

    /// <summary>A failure with a message.</summary>
    public UsageException(string message)
        : base(message)
    {
    }

    /// <summary>A failure with a message and its cause.</summary>
    public UsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
