package org.jacquardlang.host.examples;

import java.io.IOException;
import java.io.OutputStream;
import java.io.PrintStream;
import java.nio.channels.Channels;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.NoSuchFileException;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.nio.file.StandardOpenOption;
import java.nio.file.attribute.PosixFilePermissions;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashSet;
import java.util.HexFormat;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.Set;
import java.util.TreeMap;
import java.util.concurrent.atomic.AtomicLong;

import org.jacquardlang.host.HandlerException;
import org.jacquardlang.host.ProtocolException;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;
import org.jacquardlang.host.sdk.Codec;
import org.jacquardlang.host.sdk.Function;
import org.jacquardlang.host.sdk.Invocation;
import org.jacquardlang.host.sdk.JacquardException;
import org.jacquardlang.host.sdk.Operations;
import org.jacquardlang.host.sdk.PrepareOptions;
import org.jacquardlang.host.sdk.Program;
import org.jacquardlang.host.sdk.Runtime;
import org.jacquardlang.host.sdk.RuntimeOptions;

/**
 * Runs the repository's example Jacquard projects through the Java SDK:
 * quickstart, planner, triage, and fulfillment. The same rules run through the
 * Python, TypeScript, Go, and Rust SDKs.
 *
 * <pre>
 * java -cp target/classes:target/test-classes org.jacquardlang.host.examples.JacquardExamples \
 *     [--workdir DIR] [--inspect PATH] [--fault NAME] [--deliver-pending] PROJECT [INPUT.json]
 * </pre>
 *
 * <p>Core is found through {@code JACQUARD_BIN} and {@code JACQUARD_PRELUDE}.
 * The rules are read from the repository's {@code examples/} directory (the
 * {@code jacquard.examples} system property, else {@code examples/} or
 * {@code ../examples/} under the working directory). The exit status is 1
 * for a Jacquard or SDK failure and 2 for usage or other errors.
 */
public final class JacquardExamples {
    private JacquardExamples() {}

    /** The example projects in presentation order. */
    public static final List<String> PROJECTS = List.of("quickstart", "planner", "triage", "fulfillment");

    /** A usage, input, or I/O failure: exit status 2. */
    public static final class Other extends Exception {
        private static final long serialVersionUID = 1L;

        Other(String message) {
            super(message);
        }
    }

    /** The repository's example projects. */
    static Path examples() throws Other {
        String configured = System.getProperty("jacquard.examples");
        if (configured != null && !configured.isEmpty()) {
            return Path.of(configured).toAbsolutePath();
        }
        for (Path candidate : List.of(Path.of("examples"), Path.of("..", "examples"))) {
            if (Files.isRegularFile(candidate.resolve("quickstart").resolve("rules.jac"))) {
                return candidate.toAbsolutePath().normalize();
            }
        }
        throw new Other("cannot find the examples directory; run from the repository or java/, or set -Djacquard.examples");
    }

    // JSON input and output.

    private static Json field(Map<String, Json> object, String key) throws Other {
        Json value = object.get(key);
        if (value == null) {
            throw new Other("input: missing " + key);
        }
        return value;
    }

    private static String text(Map<String, Json> object, String key) throws Other {
        String value = field(object, key).asString();
        if (value == null) {
            throw new Other("input: " + key + " must be text");
        }
        return value;
    }

    private static long integer(Map<String, Json> object, String key) throws Other {
        Long value = field(object, key).asInt();
        if (value == null) {
            throw new Other("input: " + key + " must be an integer");
        }
        return value;
    }

    private static boolean bool(Map<String, Json> object, String key) throws Other {
        if (field(object, key) instanceof Json.Bool value) {
            return value.value();
        }
        throw new Other("input: " + key + " must be true or false");
    }

    private static List<Map<String, Json>> objects(Map<String, Json> object, String key) throws Other {
        List<Json> items = field(object, key).asArray();
        if (items == null) {
            throw new Other("input: " + key + " must be a list");
        }
        List<Map<String, Json>> out = new ArrayList<>();
        for (Json item : items) {
            if (item.asObject() == null) {
                throw new Other("input: " + key + " must hold objects");
            }
            out.add(item.asObject());
        }
        return out;
    }

    private static Json tagged(String tag, Object... pairs) {
        TreeMap<String, Json> object = new TreeMap<>(Json.CODE_POINT_ORDER);
        for (int i = 0; i < pairs.length; i += 2) {
            object.put((String) pairs[i], (Json) pairs[i + 1]);
        }
        object.put("$tag", Json.of(tag));
        return Json.of(object);
    }

    private static String compact(Json value) {
        try {
            return JsonCodec.stringify(value, 16);
        } catch (ProtocolException error) {
            throw new IllegalStateException(error);
        }
    }

    private static void pretty(Json value, int indent, StringBuilder out) {
        String pad = "  ".repeat(indent);
        if (value instanceof Json.Arr array && !array.items().isEmpty()) {
            out.append("[\n");
            for (int i = 0; i < array.items().size(); i++) {
                out.append(pad).append("  ");
                pretty(array.items().get(i), indent + 1, out);
                out.append(i + 1 < array.items().size() ? ",\n" : "\n");
            }
            out.append(pad).append(']');
        } else if (value instanceof Json.Obj object && !object.fields().isEmpty()) {
            out.append("{\n");
            int i = 0;
            for (Map.Entry<String, Json> entry : object.fields().entrySet()) {
                out.append(pad).append("  ").append(compact(Json.of(entry.getKey()))).append(": ");
                pretty(entry.getValue(), indent + 1, out);
                out.append(++i < object.fields().size() ? ",\n" : "\n");
            }
            out.append(pad).append('}');
        } else {
            out.append(compact(value));
        }
    }

    private static void printJson(PrintStream out, Json value) {
        StringBuilder text = new StringBuilder();
        pretty(value, 0, text);
        out.println(text);
    }

    // Planner.

    public record Job(String id, long effort, long value, long latest) {}

    public record Assignment(String id, long slot) {}

    public record Plan(long score, List<Assignment> assignments) {}

    public sealed interface Planning {}

    public record Planned(Plan plan) implements Planning {}

    public record InvalidPlan(String reason) implements Planning {}

    static Json planningJson(Planning planning) {
        return switch (planning) {
            case Planned(Plan plan) -> {
                List<Json> assignments = new ArrayList<>();
                for (Assignment assignment : plan.assignments()) {
                    assignments.add(Json.of(Json.object("id", Json.of(assignment.id()), "slot", Json.of(assignment.slot()))));
                }
                yield tagged("Planned", "plan", Json.of(Json.object("assignments", Json.of(assignments), "score", Json.of(plan.score()))));
            }
            case InvalidPlan(String reason) -> tagged("InvalidPlan", "reason", Json.of(reason));
        };
    }

    // Triage.

    public record TriagePolicy(long page, long stale) {}

    public record Incident(String id, String service, long severity, long age, boolean visible, Optional<String> owner) {}

    /** Constants map by convention: {@code PAGE_NOW} is the constructor {@code PageNow}. */
    public enum Urgency {
        PAGE_NOW,
        WORK_TODAY,
        WATCH_ONLY
    }

    public sealed interface Triage {}

    public record Routed(String id, String team, Urgency urgency, List<String> reasons) implements Triage {}

    public record InvalidIncident(String id) implements Triage {}

    static Json triageJson(Triage triage) {
        return switch (triage) {
            case Routed(String id, String team, Urgency urgency, List<String> reasons) -> {
                String label = switch (urgency) {
                    case PAGE_NOW -> "PAGE";
                    case WORK_TODAY -> "TODAY";
                    case WATCH_ONLY -> "WATCH";
                };
                List<Json> items = new ArrayList<>();
                for (String reason : reasons) {
                    items.add(Json.of(reason));
                }
                yield tagged("Routed", "id", Json.of(id), "team", Json.of(team), "urgency", Json.of(label), "reasons", Json.of(items));
            }
            case InvalidIncident(String id) -> tagged("InvalidIncident", "id", Json.of(id));
        };
    }

    // Fulfillment.

    public record Line(String sku, long units) {}

    public record Order(String id, List<Line> lines) {}

    public sealed interface Stock {
        record Available(long available) implements Stock {}

        record UnknownSku() implements Stock {}
    }

    public sealed interface Reservation {
        record BookingPending() implements Reservation {}

        record Booked(String receipt) implements Reservation {}

        record RepeatBooking(String receipt) implements Reservation {}

        record BookingConflict() implements Reservation {}

        record StockChanged() implements Reservation {}
    }

    public sealed interface Fulfillment {
        record Fulfilled(String receipt) implements Fulfillment {}

        record DuplicateOrder(String receipt) implements Fulfillment {}

        record OrderRejected(String reason) implements Fulfillment {}
    }

    static final Codec<Order> ORDER = Codec.of(Order.class);
    /** {@code Stock} is also the type's name, so the Java case is named {@code Available} and mapped explicitly. */
    static final Codec<Stock> STOCK = Codec.union(Stock.class,
            List.of(Codec.record(Stock.Available.class, "Stock"), Codec.record(Stock.UnknownSku.class)));
    static final Codec<Reservation> RESERVATION = Codec.of(Reservation.class);
    static final Codec<Fulfillment> FULFILLMENT = Codec.of(Fulfillment.class);

    static Json fulfillmentJson(Fulfillment fulfillment) {
        return switch (fulfillment) {
            case Fulfillment.Fulfilled(String receipt) -> tagged("Fulfilled", "receipt", Json.of(receipt));
            case Fulfillment.DuplicateOrder(String receipt) -> tagged("DuplicateOrder", "receipt", Json.of(receipt));
            case Fulfillment.OrderRejected(String reason) -> tagged("OrderRejected", "reason", Json.of(reason));
        };
    }

    static final String INVENTORY_SCHEMA = "jacquard-example-inventory-v1";

    record Booking(String id, String payload, String receipt) {}

    record Outbox(String id, String receipt, boolean delivered) {}

    record Inventory(Map<String, Long> stock, List<String> skus, List<Booking> bookings, List<Outbox> outbox, List<String> deliveries) {
        Json toJson() {
            List<Json> stockItems = new ArrayList<>();
            for (String sku : skus) {
                stockItems.add(Json.of(Json.object("sku", Json.of(sku), "available", Json.of(stock.get(sku)))));
            }
            List<Json> bookingItems = new ArrayList<>();
            for (Booking booking : bookings) {
                bookingItems.add(Json.of(Json.object("id", Json.of(booking.id()), "payload", Json.of(booking.payload()), "receipt",
                        Json.of(booking.receipt()))));
            }
            List<Json> outboxItems = new ArrayList<>();
            for (Outbox entry : outbox) {
                outboxItems.add(Json.of(Json.object("id", Json.of(entry.id()), "receipt", Json.of(entry.receipt()), "delivered",
                        Json.of(entry.delivered()))));
            }
            List<Json> deliveryItems = new ArrayList<>();
            for (String delivery : deliveries) {
                deliveryItems.add(Json.of(delivery));
            }
            return Json.of(Json.object("schema", Json.of(INVENTORY_SCHEMA), "stock", Json.of(stockItems), "bookings", Json.of(bookingItems),
                    "outbox", Json.of(outboxItems), "deliveries", Json.of(deliveryItems)));
        }

        static Inventory fromJson(Json value) {
            Map<String, Json> object = value.asObject();
            if (object == null || !Json.of(INVENTORY_SCHEMA).equals(object.get("schema"))) {
                return null;
            }
            try {
                Map<String, Long> stock = new TreeMap<>();
                List<String> skus = new ArrayList<>();
                for (Json item : object.get("stock").asArray()) {
                    String sku = item.asObject().get("sku").asString();
                    stock.put(sku, item.asObject().get("available").asInt());
                    skus.add(sku);
                }
                List<Booking> bookings = new ArrayList<>();
                for (Json item : object.get("bookings").asArray()) {
                    Map<String, Json> fields = item.asObject();
                    bookings.add(new Booking(fields.get("id").asString(), fields.get("payload").asString(), fields.get("receipt").asString()));
                }
                List<Outbox> outbox = new ArrayList<>();
                for (Json item : object.get("outbox").asArray()) {
                    Map<String, Json> fields = item.asObject();
                    outbox.add(new Outbox(fields.get("id").asString(), fields.get("receipt").asString(),
                            ((Json.Bool) fields.get("delivered")).value()));
                }
                List<String> deliveries = new ArrayList<>();
                for (Json item : object.get("deliveries").asArray()) {
                    deliveries.add(item.asString());
                }
                Inventory inventory = new Inventory(stock, skus, bookings, outbox, deliveries);
                return inventory.valid() ? inventory : null;
            } catch (RuntimeException malformed) {
                return null;
            }
        }

        private boolean valid() {
            return skus.stream().allMatch(sku -> sku != null && stock.get(sku) != null)
                    && bookings.stream().allMatch(b -> b.id() != null && b.payload() != null && b.receipt() != null)
                    && outbox.stream().allMatch(o -> o.id() != null && o.receipt() != null) && deliveries.stream().allMatch(d -> d != null);
        }

        Inventory copy() {
            return new Inventory(new TreeMap<>(stock), new ArrayList<>(skus), new ArrayList<>(bookings), new ArrayList<>(outbox),
                    new ArrayList<>(deliveries));
        }
    }

    /** The canonical payload of an order: its lines sorted by SKU. */
    static String payload(Order order) {
        List<Line> lines = new ArrayList<>(order.lines());
        lines.sort(Comparator.comparing(Line::sku));
        List<Json> pairs = new ArrayList<>();
        for (Line line : lines) {
            pairs.add(Json.of(List.of(Json.of(line.sku()), Json.of(line.units()))));
        }
        return compact(Json.of(pairs));
    }

    static String sha256(String text) {
        try {
            return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(text.getBytes(StandardCharsets.UTF_8)));
        } catch (NoSuchAlgorithmException error) {
            throw new IllegalStateException(error);
        }
    }

    /**
     * A single-process synthetic Inventory and Dispatch service. Bookings and
     * outbox entries are committed together as one atomic JSON snapshot;
     * deliveries are deduplicated by receipt. It has no cross-process locking
     * or power-loss durability claim. Its methods are synchronized, because
     * each handler runs on its own thread.
     */
    public static final class Warehouse {
        static final AtomicLong NEXT = new AtomicLong();
        /** Injects {@code publish-failed}, {@code after-commit}, or {@code after-delivery}. */
        final String fault;
        private final Path path;
        private Inventory state;

        private Warehouse(Path path, String fault, Inventory state) {
            this.path = path;
            this.fault = fault;
            this.state = state;
        }

        /** Loads the snapshot at {@code path} or starts synthetic stock. */
        public static Warehouse open(Path path, String fault) throws Other {
            Inventory state;
            try {
                byte[] raw = Files.readAllBytes(path);
                Inventory parsed;
                try {
                    parsed = Inventory.fromJson(JsonCodec.decode(raw, 16));
                } catch (ProtocolException error) {
                    parsed = null;
                }
                if (parsed == null) {
                    throw new Other("inventory snapshot version mismatch");
                }
                state = parsed;
            } catch (NoSuchFileException missing) {
                state = new Inventory(new TreeMap<>(Map.of("sensor", 20L, "cable", 30L, "battery", 15L)), List.of("sensor", "cable", "battery"),
                        List.of(), List.of(), List.of());
            } catch (IOException error) {
                throw new Other(error.toString());
            }
            return new Warehouse(path, fault, state.copy());
        }

        /** Writes the current state again (tests). */
        void save() throws IOException {
            commit(state.copy());
        }

        private void commit(Inventory next) throws IOException {
            Path parent = path.toAbsolutePath().getParent();
            Files.createDirectories(parent);
            String text = compact(next.toJson()) + "\n";
            // A fresh owner-only file, created exclusively, then renamed over the snapshot.
            Path temporary = parent.resolve(path.getFileName() + "." + ProcessHandle.current().pid() + "-" + NEXT.getAndIncrement() + ".tmp");
            // Created exclusively here, so only a file this call made is ever removed.
            OutputStream out = Channels.newOutputStream(Files.newByteChannel(temporary,
                    Set.of(StandardOpenOption.WRITE, StandardOpenOption.CREATE_NEW),
                    PosixFilePermissions.asFileAttribute(PosixFilePermissions.fromString("rw-------"))));
            boolean moved = false;
            try {
                try (out) {
                    out.write(text.getBytes(StandardCharsets.UTF_8));
                }
                Files.move(temporary, path, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
                moved = true;
            } finally {
                if (!moved) {
                    Files.deleteIfExists(temporary);
                }
            }
            state = next;
        }

        private Booking find(String id) {
            return state.bookings().stream().filter(booking -> booking.id().equals(id)).findFirst().orElse(null);
        }

        /** Serves {@code Inventory.booking-status}. */
        public synchronized Reservation bookingStatus(Order order) {
            Booking previous = find(order.id());
            if (previous == null) {
                return new Reservation.BookingPending();
            }
            return previous.payload().equals(payload(order)) ? new Reservation.RepeatBooking(previous.receipt()) : new Reservation.BookingConflict();
        }

        /** Serves {@code Inventory.lookup-stock}. */
        public synchronized Stock lookupStock(String sku) {
            Long available = state.stock().get(sku);
            return available == null ? new Stock.UnknownSku() : new Stock.Available(available);
        }

        /** Serves {@code Inventory.reserve-order}, committing booking and outbox together. */
        public synchronized Reservation reserveOrder(Order order) throws HandlerException, IOException {
            Set<String> seen = new HashSet<>();
            boolean valid = !order.id().isEmpty() && order.lines().size() >= 1 && order.lines().size() <= 20
                    && order.lines().stream().allMatch(line -> seen.add(line.sku()) && line.units() >= 1 && line.units() <= 20);
            if (!valid) {
                throw HandlerException.refused("invalid synthetic reservation");
            }
            if (find(order.id()) != null) {
                return bookingStatus(order);
            }
            Inventory next = state.copy();
            for (Line line : order.lines()) {
                Long available = next.stock().get(line.sku());
                if (available == null || available < line.units()) {
                    return new Reservation.StockChanged();
                }
                next.stock().put(line.sku(), available - line.units());
            }
            String body = payload(order);
            String receipt = sha256(order.id() + "\0" + body);
            next.bookings().add(new Booking(order.id(), body, receipt));
            next.outbox().add(new Outbox(order.id(), receipt, false));
            commit(next);
            if (fault.equals("after-commit")) {
                throw HandlerException.completionUnknown("synthetic fault after the booking committed");
            }
            return new Reservation.Booked(receipt);
        }

        /** Serves {@code Dispatch.publish-booking}. */
        public synchronized Void publishBooking(String id, String receipt) throws HandlerException, IOException {
            if (fault.equals("publish-failed")) {
                throw HandlerException.outsideFailed("synthetic delivery unavailable; outbox retained");
            }
            Inventory next = state.copy();
            int index = -1;
            for (int i = 0; i < next.outbox().size(); i++) {
                Outbox entry = next.outbox().get(i);
                if (entry.receipt().equals(receipt) && entry.id().equals(id)) {
                    index = i;
                    break;
                }
            }
            if (index < 0) {
                throw HandlerException.refused("no matching outbox entry");
            }
            next.outbox().set(index, new Outbox(id, receipt, true));
            if (!next.deliveries().contains(receipt)) {
                next.deliveries().add(receipt);
            }
            commit(next);
            if (fault.equals("after-delivery")) {
                throw HandlerException.completionUnknown("synthetic fault after the delivery committed");
            }
            return null;
        }

        /** Publishes every undelivered outbox entry explicitly, returning how many were delivered. */
        public synchronized int deliverPending() throws Exception {
            List<Outbox> pending = state.outbox().stream().filter(entry -> !entry.delivered()).toList();
            for (Outbox entry : pending) {
                publishBooking(entry.id(), entry.receipt());
            }
            return pending.size();
        }

        /** Counts bookings, pending deliveries, and deliveries. */
        public synchronized Json summary() {
            long pending = state.outbox().stream().filter(entry -> !entry.delivered()).count();
            return Json.of(Json.object("bookings", Json.of(state.bookings().size()), "deliveries", Json.of(state.deliveries().size()),
                    "pendingDeliveries", Json.of(pending)));
        }
    }

    static String describe(Exception error) {
        return error instanceof HandlerException failure ? failure.category().wireName() + ": " + failure.getMessage() : "completion unknown";
    }

    /** Binds {@code fulfillment.place} to a shared warehouse serving both effects. */
    public static Function fulfillment(Program program, Warehouse warehouse) throws JacquardException {
        Operations service = new Operations()
                .on("booking-status", ORDER, RESERVATION, warehouse::bookingStatus)
                .on("lookup-stock", Codec.TEXT, STOCK, warehouse::lookupStock)
                .on("reserve-order", ORDER, RESERVATION, warehouse::reserveOrder)
                .on("publish-booking", Codec.TEXT, Codec.TEXT, Codec.UNIT, warehouse::publishBooking);
        return program.function("fulfillment.place", Map.of("Inventory", service, "Dispatch", service));
    }

    private static void save(Invocation invocation, String path) throws Other {
        if (path != null) {
            try {
                invocation.save(Path.of(path));
            } catch (IOException error) {
                throw new Other(error.toString());
            }
        }
    }

    private static byte[] read(Path input) throws Other {
        try {
            return Files.readAllBytes(input);
        } catch (IOException error) {
            throw new Other(error.toString());
        }
    }

    /** Runs one command line, writing its output to {@code out}. */
    public static void run(List<String> arguments, PrintStream out) throws JacquardException, Other {
        Path workdir = Path.of(".scratch", "java-examples");
        String inspect = null;
        String fault = "";
        boolean deliverPending = false;
        List<String> positional = new ArrayList<>();
        for (int i = 0; i < arguments.size(); i++) {
            String argument = arguments.get(i);
            switch (argument) {
                case "--workdir", "--inspect", "--fault" -> {
                    if (i + 1 >= arguments.size()) {
                        throw new Other(argument + " needs a value");
                    }
                    String value = arguments.get(++i);
                    switch (argument) {
                        case "--workdir" -> workdir = Path.of(value);
                        case "--inspect" -> inspect = value;
                        default -> fault = value;
                    }
                }
                case "--deliver-pending" -> deliverPending = true;
                default -> {
                    if (argument.startsWith("--")) {
                        throw new Other("unknown flag " + argument);
                    }
                    positional.add(argument);
                }
            }
        }
        String project = positional.isEmpty() ? "" : positional.get(0);
        if (!PROJECTS.contains(project) || positional.size() > 2) {
            throw new Other("choose quickstart, triage, planner, or fulfillment");
        }
        if (!List.of("", "publish-failed", "after-commit", "after-delivery").contains(fault)) {
            throw new Other("unknown fault");
        }
        Path root = workdir.toAbsolutePath();
        Path rules = examples().resolve(project).resolve("rules.jac");
        Runtime runtime = new Runtime(RuntimeOptions.defaults());
        Program program = runtime.prepare(List.of(rules), new PrepareOptions(root.resolve("programs")));
        if (project.equals("quickstart")) {
            Function shipping = program.function("shipping-cost");
            for (long total : new long[] {35, 75}) {
                long cost = shipping.call(Codec.INT, total);
                out.println("Order total " + total + ": shipping " + cost);
            }
            return;
        }
        Path inputPath = positional.size() > 1 ? Path.of(positional.get(1)) : examples().resolve(project).resolve("input.json");
        Map<String, Json> input;
        try {
            input = JsonCodec.decode(read(inputPath), 16).asObject();
        } catch (ProtocolException error) {
            throw new Other("input: " + error.getMessage());
        }
        if (input == null) {
            throw new Other("input must be a JSON object");
        }
        switch (project) {
            case "planner" -> {
                List<Job> jobs = new ArrayList<>();
                for (Map<String, Json> job : objects(input, "jobs")) {
                    jobs.add(new Job(text(job, "id"), integer(job, "effort"), integer(job, "value"), integer(job, "latest")));
                }
                List<Json> capacityItems = field(input, "capacities").asArray();
                if (capacityItems == null) {
                    throw new Other("input: capacities must be a list");
                }
                List<Long> capacities = new ArrayList<>();
                for (Json capacity : capacityItems) {
                    if (capacity.asInt() == null) {
                        throw new Other("input: capacities must be integers");
                    }
                    capacities.add(capacity.asInt());
                }
                Invocation invocation = program.function("planner.plan").invoke(Codec.list(Codec.of(Job.class)).arg(jobs), capacities);
                save(invocation, inspect);
                printJson(out, planningJson(invocation.decode(Codec.of(Planning.class))));
                out.println(invocation.render());
            }
            case "triage" -> {
                Map<String, Json> policyObject = field(input, "policy").asObject();
                if (policyObject == null) {
                    throw new Other("input: policy must be an object");
                }
                TriagePolicy policy = new TriagePolicy(integer(policyObject, "page"), integer(policyObject, "stale"));
                List<Incident> incidents = new ArrayList<>();
                for (Map<String, Json> incident : objects(input, "incidents")) {
                    // A missing owner is null, as in the Go command.
                    Json owner = incident.getOrDefault("owner", Json.NULL);
                    Optional<String> ownerValue;
                    if (owner instanceof Json.Null) {
                        ownerValue = Optional.empty();
                    } else if (owner.asString() != null) {
                        ownerValue = Optional.of(owner.asString());
                    } else {
                        throw new Other("input: owner must be text or null");
                    }
                    incidents.add(new Incident(text(incident, "id"), text(incident, "service"), integer(incident, "severity"),
                            integer(incident, "age"), bool(incident, "visible"), ownerValue));
                }
                Function route = program.function("triage.batch");
                Codec<Triage> triage = Codec.of(Triage.class);
                List<Triage> results;
                if (inspect == null) {
                    results = route.mapBatches(triage, incidents, 8, policy);
                } else {
                    results = new ArrayList<>();
                    for (int start = 0, index = 0; start < incidents.size(); start += 8, index++) {
                        List<Incident> chunk = incidents.subList(start, Math.min(incidents.size(), start + 8));
                        Invocation invocation = route.invoke(policy, chunk);
                        save(invocation, inspect + "." + index + ".json");
                        results.addAll(invocation.decode(Codec.list(triage)));
                    }
                }
                List<Json> rows = new ArrayList<>();
                for (Triage result : results) {
                    rows.add(triageJson(result));
                }
                printJson(out, Json.of(rows));
            }
            default -> {
                List<Line> lines = new ArrayList<>();
                for (Map<String, Json> line : objects(input, "lines")) {
                    lines.add(new Line(text(line, "sku"), integer(line, "units")));
                }
                Order order = new Order(text(input, "id"), lines);
                Warehouse warehouse = Warehouse.open(root.resolve("inventory.json"), fault);
                try {
                    if (deliverPending) {
                        int delivered;
                        try {
                            delivered = warehouse.deliverPending();
                        } catch (Exception error) {
                            // Like the Go command, a failed delivery prints no count.
                            throw new Other("delivery failed: " + describe(error));
                        }
                        out.println(compact(Json.of(Json.object("delivered", Json.of(delivered)))));
                        return;
                    }
                    Invocation invocation = fulfillment(program, warehouse).invoke(order);
                    save(invocation, inspect);
                    JacquardException failure = null;
                    try {
                        printJson(out, fulfillmentJson(invocation.decode(FULFILLMENT)));
                    } catch (JacquardException error) {
                        failure = error;
                    }
                    out.println(invocation.render());
                    if (failure != null) {
                        throw failure;
                    }
                } finally {
                    out.println(compact(warehouse.summary()));
                }
            }
        }
    }

    public static void main(String[] args) {
        try {
            run(List.of(args), System.out);
        } catch (JacquardException failure) {
            System.out.flush();
            System.err.println(failure.getMessage());
            System.exit(1);
        } catch (Other | RuntimeException failure) {
            System.out.flush();
            System.err.println(failure.getMessage());
            System.exit(2);
        }
    }
}
