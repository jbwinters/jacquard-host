namespace JacquardLang.Host.Tests.Readme;

using System.Collections.Generic;
using JacquardLang.Host.Sdk;

// type Incident = | Incident(id: Text, service: Text, severity: Int, age: Int, visible: Bool, owner: Option Text)
// type Urgency = | PageNow | WorkToday | WatchOnly
// type Triage = | Routed Text Text Urgency (List Text) | InvalidIncident Text
public static class Incidents
{
    public sealed record Incident(string Id, string Service, long Severity, long Age, bool Visible, string? Owner);

    public enum Urgency
    {
        PageNow,
        WorkToday,
        WatchOnly,
    }

    public abstract record Triage;

    public sealed record Routed(string Id, string Team, Urgency Urgency, IReadOnlyList<string> Reasons) : Triage;

    public sealed record InvalidIncident(string Id) : Triage;

    // A record is one constructor: its fields in order, each with a codec and
    // a reader, and a function that builds it from the decoded fields.
    public static readonly RecordCodec<Incident> IncidentCodec = Codecs.Record(
        "Incident",
        Codecs.Field("id", Codecs.Text, (Incident incident) => incident.Id),
        Codecs.Field("service", Codecs.Text, (Incident incident) => incident.Service),
        Codecs.Field("severity", Codecs.Int, (Incident incident) => incident.Severity),
        Codecs.Field("age", Codecs.Int, (Incident incident) => incident.Age),
        Codecs.Field("visible", Codecs.Bool, (Incident incident) => incident.Visible),
        Codecs.Field("owner", Codecs.Nullable(Codecs.Text), (Incident incident) => incident.Owner),
        (id, service, severity, age, visible, owner) => new Incident(id, service, severity, age, visible, owner));

    // An enumeration's members name nullary constructors: PageNow is page-now.
    // A union's cases are records, enumerations, or unions of its subtypes.
    public static readonly UnionCodec<Triage> TriageCodec = Codecs.Union<Triage>(
        Codecs.Record(
            "Routed",
            Codecs.Field("id", Codecs.Text, (Routed routed) => routed.Id),
            Codecs.Field("team", Codecs.Text, (Routed routed) => routed.Team),
            Codecs.Field("urgency", Codecs.Enum<Urgency>(), (Routed routed) => routed.Urgency),
            Codecs.Field("reasons", Codecs.List(Codecs.Text), (Routed routed) => routed.Reasons),
            (id, team, urgency, reasons) => new Routed(id, team, urgency, reasons)),
        Codecs.Record("InvalidIncident", Codecs.Field("id", Codecs.Text, (InvalidIncident invalid) => invalid.Id), id => new InvalidIncident(id)));

    // triage.batch : (TriagePolicy, List Incident) ->{} List Triage, run in
    // explicit batches of eight; each batch is checked before any runs.
    public static IReadOnlyList<Triage> Route(Function batch, Dynamic policy, IReadOnlyList<Incident> incidents) =>
        batch.MapBatches(TriageCodec, IncidentCodec, incidents, 8, new Argument[] { policy });
}
