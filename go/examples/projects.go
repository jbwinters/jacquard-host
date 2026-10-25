// Package examples maps the repository's example Jacquard projects to Go
// types and services. The same rules run through the Python and TypeScript
// SDKs; cmd/jacquard-examples is the command-line front end.
package examples

import (
	"crypto/rand"
	"crypto/sha256"
	"embed"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"slices"
	"strings"

	"github.com/jbwinters/jacquard-host/go/host"
	"github.com/jbwinters/jacquard-host/go/jacquard"
)

// Files holds a copy of each project's rules and synthetic input, checked
// against ../examples by the package tests.
//
//go:embed data
var Files embed.FS

// Projects lists the example projects in presentation order.
var Projects = []string{"quickstart", "planner", "triage", "fulfillment"}

// Extract writes one project's files to directory and returns the rules path.
func Extract(project, directory string) (string, error) {
	entries, err := fs.ReadDir(Files, "data/"+project)
	if err != nil {
		return "", fmt.Errorf("unknown project %s", project)
	}
	if err := os.MkdirAll(directory, 0o755); err != nil {
		return "", err
	}
	for _, entry := range entries {
		data, err := Files.ReadFile("data/" + project + "/" + entry.Name())
		if err != nil {
			return "", err
		}
		if err := os.WriteFile(filepath.Join(directory, entry.Name()), data, 0o644); err != nil {
			return "", err
		}
	}
	return filepath.Join(directory, "rules.jac"), nil
}

func tagged(tag string, fields any) ([]byte, error) {
	data, err := json.Marshal(fields)
	if err != nil {
		return nil, err
	}
	return append([]byte(`{"$tag":`+fmt.Sprintf("%q", tag)+`,`), data[1:]...), nil
}

// Planner types.
type (
	Job struct {
		ID     string `json:"id"`
		Effort int    `json:"effort"`
		Value  int    `json:"value"`
		Latest int    `json:"latest"`
	}
	Assignment struct {
		ID   string `json:"id"`
		Slot int    `json:"slot"`
	}
	Plan struct {
		Score       int          `json:"score"`
		Assignments []Assignment `json:"assignments"`
	}
	Planned struct {
		Plan Plan `json:"plan"`
	}
	InvalidPlan struct {
		Reason string `json:"reason"`
	}
	// Planning is Planned or InvalidPlan.
	Planning interface{ planning() }
)

func (Planned) planning()     {}
func (InvalidPlan) planning() {}

func (p Planned) MarshalJSON() ([]byte, error) {
	type plain Planned
	return tagged("Planned", plain(p))
}

func (p InvalidPlan) MarshalJSON() ([]byte, error) {
	type plain InvalidPlan
	return tagged("InvalidPlan", plain(p))
}

// Planner registers the planner types and binds planner.plan.
func Planner(program *jacquard.Program) (*jacquard.Function, error) {
	d := program.Data
	for name, prototype := range map[string]any{"Job": Job{}, "Assignment": Assignment{}, "Plan": Plan{}, "Planned": Planned{}, "InvalidPlan": InvalidPlan{}} {
		if err := d.Record(name, prototype); err != nil {
			return nil, err
		}
	}
	if err := jacquard.RegisterUnion[Planning](d, Planned{}, InvalidPlan{}); err != nil {
		return nil, err
	}
	return program.Function("planner.plan", jacquard.FunctionOptions{})
}

// Triage types.
type (
	TriagePolicy struct {
		Page  int `json:"page"`
		Stale int `json:"stale"`
	}
	Incident struct {
		ID       string  `json:"id"`
		Service  string  `json:"service"`
		Severity int     `json:"severity"`
		Age      int     `json:"age"`
		Visible  bool    `json:"visible"`
		Owner    *string `json:"owner"`
	}
	// Urgency is the application's name for each urgency constructor.
	Urgency string
	Routed  struct {
		ID      string   `json:"id"`
		Team    string   `json:"team"`
		Urgency Urgency  `json:"urgency"`
		Reasons []string `json:"reasons"`
	}
	InvalidIncident struct {
		ID string `json:"id"`
	}
	// Triage is Routed or InvalidIncident.
	Triage interface{ triage() }
)

// Urgency values.
const (
	Page  Urgency = "PAGE"
	Today Urgency = "TODAY"
	Watch Urgency = "WATCH"
)

func (Routed) triage()          {}
func (InvalidIncident) triage() {}

func (r Routed) MarshalJSON() ([]byte, error) {
	type plain Routed
	return tagged("Routed", plain(r))
}

func (r InvalidIncident) MarshalJSON() ([]byte, error) {
	type plain InvalidIncident
	return tagged("InvalidIncident", plain(r))
}

// TriageProject registers the triage types and binds triage.batch.
func TriageProject(program *jacquard.Program) (*jacquard.Function, error) {
	d := program.Data
	for name, prototype := range map[string]any{"TriagePolicy": TriagePolicy{}, "Incident": Incident{}, "Routed": Routed{}, "InvalidIncident": InvalidIncident{}} {
		if err := d.Record(name, prototype); err != nil {
			return nil, err
		}
	}
	if err := jacquard.RegisterEnum(d, map[Urgency]string{Page: "PageNow", Today: "WorkToday", Watch: "WatchOnly"}); err != nil {
		return nil, err
	}
	if err := jacquard.RegisterUnion[Triage](d, Routed{}, InvalidIncident{}); err != nil {
		return nil, err
	}
	return program.Function("triage.batch", jacquard.FunctionOptions{})
}

// Fulfillment types.
type (
	Line struct {
		SKU   string `json:"sku"`
		Units int    `json:"units"`
	}
	Order struct {
		ID    string `json:"id"`
		Lines []Line `json:"lines"`
	}
	Fulfilled struct {
		Receipt string `json:"receipt"`
	}
	DuplicateOrder struct {
		Receipt string `json:"receipt"`
	}
	OrderRejected struct {
		Reason string `json:"reason"`
	}
	// Fulfillment is Fulfilled, DuplicateOrder, or OrderRejected.
	Fulfillment interface{ fulfillment() }
)

func (Fulfilled) fulfillment()      {}
func (DuplicateOrder) fulfillment() {}
func (OrderRejected) fulfillment()  {}

func (f Fulfilled) MarshalJSON() ([]byte, error) {
	type plain Fulfilled
	return tagged("Fulfilled", plain(f))
}

func (f DuplicateOrder) MarshalJSON() ([]byte, error) {
	type plain DuplicateOrder
	return tagged("DuplicateOrder", plain(f))
}

func (f OrderRejected) MarshalJSON() ([]byte, error) {
	type plain OrderRejected
	return tagged("OrderRejected", plain(f))
}

// FulfillmentProject registers the fulfillment types, opens the warehouse
// snapshot at path, and binds fulfillment.place to it.
func FulfillmentProject(program *jacquard.Program, path string) (*jacquard.Function, *Warehouse, error) {
	d := program.Data
	for name, prototype := range map[string]any{"Line": Line{}, "Order": Order{}, "Fulfilled": Fulfilled{}, "DuplicateOrder": DuplicateOrder{}, "OrderRejected": OrderRejected{}} {
		if err := d.Record(name, prototype); err != nil {
			return nil, nil, err
		}
	}
	if err := jacquard.RegisterUnion[Fulfillment](d, Fulfilled{}, DuplicateOrder{}, OrderRejected{}); err != nil {
		return nil, nil, err
	}
	warehouse, err := OpenWarehouse(path, d)
	if err != nil {
		return nil, nil, err
	}
	place, err := program.Function("fulfillment.place", jacquard.FunctionOptions{Effects: map[string]any{"Inventory": warehouse, "Dispatch": warehouse}})
	return place, warehouse, err
}

const inventorySchema = "jacquard-example-inventory-v1"

type stock struct {
	SKU       string `json:"sku"`
	Available int    `json:"available"`
}

type booking struct {
	ID      string `json:"id"`
	Payload string `json:"payload"`
	Receipt string `json:"receipt"`
}

type outboxEntry struct {
	ID        string `json:"id"`
	Receipt   string `json:"receipt"`
	Delivered bool   `json:"delivered"`
}

type inventory struct {
	Schema     string        `json:"schema"`
	Stock      []stock       `json:"stock"`
	Bookings   []booking     `json:"bookings"`
	Outbox     []outboxEntry `json:"outbox"`
	Deliveries []string      `json:"deliveries"`
}

// Warehouse is a single-process synthetic Inventory and Dispatch service.
// Bookings and outbox entries are committed together as one atomic JSON
// snapshot; deliveries are deduplicated by receipt. It has no cross-process
// locking or power-loss durability claim.
type Warehouse struct {
	// Fault injects "publish-failed", "after-commit", or "after-delivery".
	Fault string
	path  string
	data  *jacquard.Data
	state inventory
}

// OpenWarehouse loads the snapshot at path or starts synthetic stock.
func OpenWarehouse(path string, data *jacquard.Data) (*Warehouse, error) {
	w := &Warehouse{path: path, data: data}
	raw, err := os.ReadFile(path)
	switch {
	case errors.Is(err, fs.ErrNotExist):
		w.state = inventory{Schema: inventorySchema, Stock: []stock{{"sensor", 20}, {"cable", 30}, {"battery", 15}},
			Bookings: []booking{}, Outbox: []outboxEntry{}, Deliveries: []string{}}
	case err != nil:
		return nil, err
	default:
		if err := json.Unmarshal(raw, &w.state); err != nil || w.state.Schema != inventorySchema {
			return nil, errors.New("inventory snapshot version mismatch")
		}
	}
	return w, nil
}

func (w *Warehouse) commit(next inventory) error {
	if err := os.MkdirAll(filepath.Dir(w.path), 0o755); err != nil {
		return err
	}
	suffix := make([]byte, 8)
	_, _ = rand.Read(suffix)
	temporary := fmt.Sprintf("%s.%x.tmp", w.path, suffix)
	defer os.Remove(temporary)
	encoded, err := json.Marshal(next)
	if err != nil {
		return err
	}
	file, err := os.OpenFile(temporary, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		return err
	}
	_, err = file.Write(append(encoded, '\n'))
	if closeErr := file.Close(); err == nil {
		err = closeErr
	}
	if err == nil {
		err = os.Rename(temporary, w.path)
	}
	if err == nil {
		w.state = next
	}
	return err
}

func (w *Warehouse) clone() inventory {
	var next inventory
	encoded, _ := json.Marshal(w.state)
	_ = json.Unmarshal(encoded, &next)
	return next
}

func payload(order Order) string {
	lines := slices.Clone(order.Lines)
	slices.SortFunc(lines, func(a, b Line) int { return strings.Compare(a.SKU, b.SKU) })
	pairs := make([][]any, len(lines))
	for i, line := range lines {
		pairs[i] = []any{line.SKU, line.Units}
	}
	encoded, _ := json.Marshal(pairs)
	return string(encoded)
}

func (w *Warehouse) make(name string, fields ...any) jacquard.Variant {
	variant, err := w.data.Make(name, fields...)
	if err != nil {
		panic(err) // the example's own constructor names; reported as completion unknown
	}
	return variant
}

func (w *Warehouse) find(id string) *booking {
	for i := range w.state.Bookings {
		if w.state.Bookings[i].ID == id {
			return &w.state.Bookings[i]
		}
	}
	return nil
}

// BookingStatus serves Inventory.booking-status.
func (w *Warehouse) BookingStatus(order Order) jacquard.Variant {
	previous := w.find(order.ID)
	switch {
	case previous == nil:
		return w.make("BookingPending")
	case previous.Payload == payload(order):
		return w.make("RepeatBooking", previous.Receipt)
	}
	return w.make("BookingConflict")
}

// LookupStock serves Inventory.lookup-stock.
func (w *Warehouse) LookupStock(sku string) jacquard.Variant {
	for _, item := range w.state.Stock {
		if item.SKU == sku {
			return w.make("Stock", item.Available)
		}
	}
	return w.make("UnknownSku")
}

// ReserveOrder serves Inventory.reserve-order, committing booking and outbox together.
func (w *Warehouse) ReserveOrder(order Order) (jacquard.Variant, error) {
	seen := map[string]bool{}
	valid := order.ID != "" && len(order.Lines) > 0 && len(order.Lines) <= 20
	for _, line := range order.Lines {
		valid = valid && !seen[line.SKU] && line.Units >= 1 && line.Units <= 20
		seen[line.SKU] = true
	}
	if !valid {
		return jacquard.Variant{}, host.Refused("invalid synthetic reservation")
	}
	if previous := w.find(order.ID); previous != nil {
		if previous.Payload == payload(order) {
			return w.make("RepeatBooking", previous.Receipt), nil
		}
		return w.make("BookingConflict"), nil
	}
	next := w.clone()
	for _, line := range order.Lines {
		index := slices.IndexFunc(next.Stock, func(item stock) bool { return item.SKU == line.SKU })
		if index < 0 || next.Stock[index].Available < line.Units {
			return w.make("StockChanged"), nil
		}
		next.Stock[index].Available -= line.Units
	}
	body := payload(order)
	sum := sha256.Sum256([]byte(order.ID + "\x00" + body))
	receipt := hex.EncodeToString(sum[:])
	next.Bookings = append(next.Bookings, booking{order.ID, body, receipt})
	next.Outbox = append(next.Outbox, outboxEntry{order.ID, receipt, false})
	if err := w.commit(next); err != nil {
		return jacquard.Variant{}, err
	}
	if w.Fault == "after-commit" {
		return jacquard.Variant{}, errors.New("synthetic fault after booking and outbox commit")
	}
	return w.make("Booked", receipt), nil
}

// PublishBooking serves Dispatch.publish-booking.
func (w *Warehouse) PublishBooking(id, receipt string) error {
	if w.Fault == "publish-failed" {
		return host.OutsideFailed("synthetic delivery unavailable; outbox retained")
	}
	next := w.clone()
	index := slices.IndexFunc(next.Outbox, func(entry outboxEntry) bool { return entry.Receipt == receipt && entry.ID == id })
	if index < 0 {
		return host.Refused("no matching outbox entry")
	}
	if !slices.Contains(next.Deliveries, receipt) {
		next.Deliveries = append(next.Deliveries, receipt)
	}
	next.Outbox[index].Delivered = true
	if err := w.commit(next); err != nil {
		return err
	}
	if w.Fault == "after-delivery" {
		return errors.New("synthetic fault after delivery commit")
	}
	return nil
}

// DeliverPending publishes every undelivered outbox entry explicitly.
func (w *Warehouse) DeliverPending() (int, error) {
	var pending []outboxEntry
	for _, entry := range w.state.Outbox {
		if !entry.Delivered {
			pending = append(pending, entry)
		}
	}
	for _, entry := range pending {
		if err := w.PublishBooking(entry.ID, entry.Receipt); err != nil {
			return 0, err
		}
	}
	return len(pending), nil
}

// Summary counts bookings, pending deliveries, and deliveries.
type Summary struct {
	Bookings          int `json:"bookings"`
	PendingDeliveries int `json:"pendingDeliveries"`
	Deliveries        int `json:"deliveries"`
}

// Summary reports the warehouse counts.
func (w *Warehouse) Summary() Summary {
	pending := 0
	for _, entry := range w.state.Outbox {
		if !entry.Delivered {
			pending++
		}
	}
	return Summary{len(w.state.Bookings), pending, len(w.state.Deliveries)}
}
