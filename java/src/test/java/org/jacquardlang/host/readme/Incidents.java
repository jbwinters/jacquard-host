package org.jacquardlang.host.readme;

import java.util.List;
import java.util.Optional;

import org.jacquardlang.host.sdk.Codec;

public final class Incidents {
    public record Incident(String id, String service, long severity, long age, boolean visible, Optional<String> owner) {}

    public enum Urgency { PAGE_NOW, WORK_TODAY, WATCH_ONLY }

    public sealed interface Triage {}

    public record Routed(String id, String team, Urgency urgency, List<String> reasons) implements Triage {}

    public record InvalidIncident(String id) implements Triage {}

    public static final Codec<Incident> INCIDENT = Codec.record(Incident.class);
    public static final Codec<Triage> TRIAGE = Codec.union(Triage.class);
}
