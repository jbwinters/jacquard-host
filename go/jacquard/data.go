package jacquard

import (
	"errors"
	"fmt"
	"reflect"
	"unicode/utf8"

	"github.com/jbwinters/jacquard-host/go/protocol"
)

// Some marks a present Option value where nil alone would be ambiguous, such
// as Some(None) in a nested option decoded without a Go target type.
type Some struct{ Value any }

// Variant is a constructor value by public name, for data without a
// registered Go type. Fields are native values.
type Variant struct {
	Tag    string
	Fields []any
}

// Unit is Jacquard's empty tuple.
type Unit = struct{}

var builtins = map[string]bool{"true": true, "false": true, "some": true, "none": true, "cons": true, "nil": true}

type record struct {
	tag      string
	identity protocol.Hash
	typ      reflect.Type
}

type enumCase struct {
	typ   reflect.Type
	value reflect.Value
}

// Data maps native Go values to the prepared program's Jacquard data. Types
// with no Go counterpart are registered explicitly: Record for constructors,
// RegisterEnum for nullary constructor sets, and RegisterUnion for interface
// types whose members are records. Register types before invoking functions;
// registration is not synchronized with concurrent calls.
type Data struct {
	names        map[string]protocol.Hash
	constructors map[string]protocol.Hash
	labels       map[protocol.Hash]string
	primitives   map[protocol.Hash]string
	limits       protocol.Limits

	records   map[reflect.Type]*record
	byID      map[protocol.Hash]*record
	enums     map[reflect.Type]map[string]protocol.Hash
	enumByID  map[protocol.Hash]enumCase
	unions    map[reflect.Type][]*record
	usedNames map[string]bool
}

func newData(names, constructors map[string]protocol.Hash, limits protocol.Limits) *Data {
	d := &Data{
		names: names, constructors: constructors, labels: map[protocol.Hash]string{}, primitives: map[protocol.Hash]string{},
		limits: limits, records: map[reflect.Type]*record{}, byID: map[protocol.Hash]*record{},
		enums: map[reflect.Type]map[string]protocol.Hash{}, enumByID: map[protocol.Hash]enumCase{},
		unions: map[reflect.Type][]*record{}, usedNames: map[string]bool{},
	}
	for name, id := range constructors {
		d.labels[id] = name
	}
	for _, name := range []string{"int", "real", "text", "bool", "hash", "list", "option"} {
		if id, ok := names[name]; ok {
			d.primitives[id] = name
		}
	}
	return d
}

func (d *Data) id(name string) (protocol.Hash, error) {
	if id, ok := d.constructors[displayToStore(name)]; ok {
		return id, nil
	}
	return "", &BindingError{Message: "unknown constructor " + name}
}

// Identity returns the exact identity of a public type, constructor, effect,
// or term name in the prepared program and its discovered prelude.
func (d *Data) Identity(name string) (protocol.Hash, error) {
	if id, ok := d.names[displayToStore(name)]; ok {
		return id, nil
	}
	return "", &BindingError{Message: "unknown name " + name}
}

// Make returns a Variant after checking that the constructor exists.
func (d *Data) Make(name string, fields ...any) (Variant, error) {
	if _, err := d.id(name); err != nil {
		return Variant{}, err
	}
	return Variant{Tag: displayToStore(name), Fields: fields}, nil
}

// Record maps a struct type to a Jacquard constructor. The struct's exported
// fields, in declaration order, are the constructor's positional fields.
func (d *Data) Record(constructor string, prototype any) error {
	t := reflect.TypeOf(prototype)
	for t != nil && t.Kind() == reflect.Pointer {
		t = t.Elem()
	}
	if t == nil || t.Kind() != reflect.Struct || t.Name() == "" {
		return &BindingError{Message: "Record needs a named struct type"}
	}
	for i := 0; i < t.NumField(); i++ {
		if !t.Field(i).IsExported() {
			return &BindingError{Message: fmt.Sprintf("%s.%s: record fields must be exported", t.Name(), t.Field(i).Name)}
		}
	}
	name := displayToStore(constructor)
	id, err := d.id(name)
	if err != nil {
		return err
	}
	if builtins[name] || d.byID[id] != nil || d.records[t] != nil {
		return &BindingError{Message: fmt.Sprintf("constructor %s is builtin or already mapped", constructor)}
	}
	r := &record{tag: constructor, identity: id, typ: t}
	d.records[t], d.byID[id] = r, r
	return nil
}

// RegisterEnum maps a Go string type's values to nullary constructors.
func RegisterEnum[T ~string](d *Data, cases map[T]string) error {
	t := reflect.TypeFor[T]()
	if d.enums[t] != nil || len(cases) == 0 {
		return &BindingError{Message: "enum is empty or already mapped"}
	}
	table := map[string]protocol.Hash{}
	members := map[protocol.Hash]enumCase{}
	for value, constructor := range cases {
		id, err := d.id(constructor)
		if err != nil {
			return err
		}
		_, taken := d.enumByID[id]
		_, repeated := members[id]
		if builtins[d.labels[id]] || d.byID[id] != nil || taken || repeated {
			return &BindingError{Message: "enum constructors must be distinct and not builtin"}
		}
		table[string(value)] = id
		members[id] = enumCase{t, reflect.ValueOf(value)}
	}
	// Publish only after every case validated.
	d.enums[t] = table
	for id, member := range members {
		d.enumByID[id] = member
	}
	return nil
}

// RegisterUnion maps an interface type to registered record types that
// implement it, so results of a sum type decode into that interface.
func RegisterUnion[I any](d *Data, members ...any) error {
	t := reflect.TypeFor[I]()
	if t.Kind() != reflect.Interface || d.unions[t] != nil || len(members) == 0 {
		return &BindingError{Message: "RegisterUnion needs a new interface type and at least one member"}
	}
	var cases []*record
	for _, member := range members {
		m := reflect.TypeOf(member)
		r := d.records[m]
		if r == nil || !m.Implements(t) {
			return &BindingError{Message: fmt.Sprintf("%v is not a registered record implementing %v", m, t)}
		}
		cases = append(cases, r)
	}
	d.unions[t] = cases
	return nil
}

func (d *Data) depth(depth int, path string) error {
	if depth > d.limits.MaxJSONDepth {
		return invalid(path, "value exceeds the v0 JSON depth; use smaller independent batches")
	}
	return nil
}

func (d *Data) construct(name string, args ...protocol.Value) (protocol.Value, error) {
	id, err := d.id(name)
	if err != nil {
		return nil, err
	}
	return protocol.Constructor{Identity: id, Arguments: args}, nil
}

var (
	hashType  = reflect.TypeFor[protocol.Hash]()
	valueType = reflect.TypeFor[protocol.Value]()
	someType  = reflect.TypeFor[Some]()
	varType   = reflect.TypeFor[Variant]()
	errorType = reflect.TypeFor[error]()
)

// Encode converts a native value, guided by an optional checked type.
func (d *Data) Encode(value any, expected protocol.BoundaryType) (protocol.Value, error) {
	return d.encode(reflect.ValueOf(value), expected, "value", 3)
}

func (d *Data) encode(v reflect.Value, expected protocol.BoundaryType, path string, depth int) (protocol.Value, error) {
	if err := d.depth(depth, path); err != nil {
		return nil, err
	}
	for v.IsValid() && v.Kind() == reflect.Interface {
		if v.IsNil() {
			v = reflect.Value{}
			break
		}
		v = v.Elem()
	}
	switch t := expected.(type) {
	case protocol.Nominal:
		var child protocol.BoundaryType
		if len(t.Arguments) > 0 {
			child = t.Arguments[0]
		}
		switch d.primitives[t.Identity] {
		case "int":
			return d.encodeInt(v, path)
		case "real":
			return d.encodeReal(v, path)
		case "text":
			return d.encodeText(v, path)
		case "bool":
			if !v.IsValid() || v.Kind() != reflect.Bool {
				return nil, invalid(path, "expected a bool")
			}
			return d.encodeBool(v.Bool())
		case "hash":
			if !v.IsValid() || v.Type() != hashType {
				return nil, invalid(path, "expected a protocol.Hash")
			}
			return d.encodeHash(v, path)
		case "list":
			return d.encodeList(v, child, path, depth)
		case "option":
			return d.encodeOption(v, child, path, depth)
		default:
			// A checked non-optional data type: a pointer cannot mean Option here.
			if v.IsValid() && v.Kind() == reflect.Pointer {
				if v.IsNil() {
					return nil, invalid(path, "nil where a non-optional value is expected")
				}
				v = v.Elem()
			}
		}
	case protocol.TupleType:
		if (!v.IsValid() || (v.Kind() == reflect.Struct && v.NumField() == 0)) && len(t.Items) == 0 {
			return protocol.Tuple{}, nil
		}
		if !v.IsValid() || (v.Kind() != reflect.Slice && v.Kind() != reflect.Array) || v.Len() != len(t.Items) {
			return nil, invalid(path, "expected a %d-item tuple (an array or slice)", len(t.Items))
		}
		items := make(protocol.Tuple, len(t.Items))
		for i, item := range t.Items {
			encoded, err := d.encode(v.Index(i), item, fmt.Sprintf("%s[%d]", path, i), depth+2)
			if err != nil {
				return nil, err
			}
			items[i] = encoded
		}
		return items, nil
	}
	return d.encodeDynamic(v, path, depth)
}

func (d *Data) encodeDynamic(v reflect.Value, path string, depth int) (protocol.Value, error) {
	if !v.IsValid() {
		return d.construct("none")
	}
	t := v.Type()
	if t.Implements(valueType) {
		if t == hashType {
			return d.encodeHash(v, path)
		}
		return v.Interface().(protocol.Value), nil
	}
	switch t {
	case varType:
		variant := v.Interface().(Variant)
		id, err := d.id(variant.Tag)
		if err != nil {
			return nil, err
		}
		args := make([]protocol.Value, len(variant.Fields))
		for i, field := range variant.Fields {
			if args[i], err = d.encode(reflect.ValueOf(field), nil, fmt.Sprintf("%s.Fields[%d]", path, i), depth+2); err != nil {
				return nil, err
			}
		}
		return protocol.Constructor{Identity: id, Arguments: args}, nil
	case someType:
		inner, err := d.encode(reflect.ValueOf(v.Interface().(Some).Value), nil, path, depth+2)
		if err != nil {
			return nil, err
		}
		return d.construct("some", inner)
	}
	if r := d.records[t]; r != nil {
		args := make([]protocol.Value, t.NumField())
		for i := range args {
			var err error
			if args[i], err = d.encode(v.Field(i), nil, path+"."+t.Field(i).Name, depth+2); err != nil {
				return nil, err
			}
		}
		return protocol.Constructor{Identity: r.identity, Arguments: args}, nil
	}
	if table := d.enums[t]; table != nil {
		id, ok := table[v.String()]
		if !ok {
			return nil, invalid(path, "unknown %v member %q", t, v.String())
		}
		return protocol.Constructor{Identity: id}, nil
	}
	switch t.Kind() {
	case reflect.Bool:
		return d.encodeBool(v.Bool())
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64,
		reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		return d.encodeInt(v, path)
	case reflect.Float32, reflect.Float64:
		return d.encodeReal(v, path)
	case reflect.String:
		return d.encodeText(v, path)
	case reflect.Array:
		items := make(protocol.Tuple, v.Len())
		for i := range items {
			var err error
			if items[i], err = d.encode(v.Index(i), nil, fmt.Sprintf("%s[%d]", path, i), depth+2); err != nil {
				return nil, err
			}
		}
		return items, nil
	case reflect.Slice:
		return d.encodeList(v, nil, path, depth)
	case reflect.Pointer:
		return d.encodeOption(v, nil, path, depth)
	case reflect.Struct:
		if t.NumField() == 0 {
			return protocol.Tuple{}, nil
		}
	}
	return nil, invalid(path, "unsupported %v; register a record or use Data.Make", t)
}

func (d *Data) encodeBool(value bool) (protocol.Value, error) {
	if value {
		return d.construct("true")
	}
	return d.construct("false")
}

func (d *Data) encodeInt(v reflect.Value, path string) (protocol.Value, error) {
	if !v.IsValid() {
		return nil, invalid(path, "expected an integer")
	}
	switch v.Kind() {
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		n := v.Int()
		if n < protocol.IntMin || n > protocol.IntMax {
			return nil, invalid(path, "integer is outside Core's 63-bit Int range")
		}
		return protocol.Int(n), nil
	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		if v.Uint() > uint64(protocol.IntMax) {
			return nil, invalid(path, "integer is outside Core's 63-bit Int range")
		}
		return protocol.Int(v.Uint()), nil
	}
	return nil, invalid(path, "expected an integer, got %v", v.Type())
}

func (d *Data) encodeReal(v reflect.Value, path string) (protocol.Value, error) {
	if !v.IsValid() {
		return nil, invalid(path, "expected a number")
	}
	if v.Type() == reflect.TypeFor[protocol.Real]() {
		return v.Interface().(protocol.Real), nil
	}
	switch v.Kind() {
	case reflect.Float32, reflect.Float64:
		return protocol.RealOf(v.Float()), nil
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		n := v.Int()
		if f := float64(n); f >= -0x1p63 && f < 0x1p63 && int64(f) == n {
			return protocol.RealOf(f), nil
		}
		return nil, invalid(path, "integer is not exactly representable as a Real")
	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		n := v.Uint()
		if f := float64(n); f < 0x1p64 && uint64(f) == n {
			return protocol.RealOf(f), nil
		}
		return nil, invalid(path, "integer is not exactly representable as a Real")
	}
	return nil, invalid(path, "expected a number, got %v", v.Type())
}

func (d *Data) encodeText(v reflect.Value, path string) (protocol.Value, error) {
	if !v.IsValid() || v.Kind() != reflect.String {
		return nil, invalid(path, "expected text")
	}
	text := v.String()
	if !utf8.ValidString(text) {
		return nil, invalid(path, "text is not valid UTF-8")
	}
	if len(text) > d.limits.MaxTextBytes {
		return nil, invalid(path, "text exceeds max_text_bytes")
	}
	return protocol.Text(text), nil
}

func (d *Data) encodeHash(v reflect.Value, path string) (protocol.Value, error) {
	h, err := protocol.ParseHash(v.String())
	if err != nil {
		return nil, invalid(path, "%v", err)
	}
	return h, nil
}

func (d *Data) encodeList(v reflect.Value, child protocol.BoundaryType, path string, depth int) (protocol.Value, error) {
	if !v.IsValid() || (v.Kind() != reflect.Slice && v.Kind() != reflect.Array) {
		return nil, invalid(path, "expected a slice")
	}
	n := v.Len()
	if n > d.limits.MaxCollectionItems {
		return nil, invalid(path, "list exceeds max_collection_items")
	}
	// A linked list nests one constructor per element on the wire.
	if err := d.depth(depth+2*n, path); err != nil {
		return nil, err
	}
	result, err := d.construct("nil")
	if err != nil {
		return nil, err
	}
	for i := n - 1; i >= 0; i-- {
		item, err := d.encode(v.Index(i), child, fmt.Sprintf("%s[%d]", path, i), depth+2*(i+1))
		if err != nil {
			return nil, err
		}
		if result, err = d.construct("cons", item, result); err != nil {
			return nil, err
		}
	}
	return result, nil
}

func (d *Data) encodeOption(v reflect.Value, child protocol.BoundaryType, path string, depth int) (protocol.Value, error) {
	if err := d.depth(depth, path); err != nil {
		return nil, err
	}
	if !v.IsValid() || (v.Kind() == reflect.Pointer && v.IsNil()) {
		return d.construct("none")
	}
	var inner reflect.Value
	switch {
	case v.Kind() == reflect.Pointer:
		inner = v.Elem()
	case v.Type() == someType:
		inner = reflect.ValueOf(v.Interface().(Some).Value)
	default:
		inner = v
	}
	encoded, err := d.encode(inner, child, path, depth+2)
	if err != nil {
		return nil, err
	}
	return d.construct("some", encoded)
}

// mappingError is a decode failure; callers wrap it as a BindingError.
type mappingError struct{ message string }

func (e *mappingError) Error() string { return e.message }

func mapping(format string, args ...any) error {
	return &mappingError{fmt.Sprintf(format, args...)}
}

func (d *Data) label(c protocol.Constructor) string { return d.labels[c.Identity] }

func (d *Data) isNullary(value protocol.Value, name string) bool {
	c, ok := value.(protocol.Constructor)
	return ok && len(c.Arguments) == 0 && d.label(c) == name
}

// listItems walks a proper List, bounded by max_collection_items.
func (d *Data) listItems(value protocol.Value) ([]protocol.Value, error) {
	var items []protocol.Value
	cursor := value
	for {
		c, ok := cursor.(protocol.Constructor)
		if ok && d.label(c) == "cons" && len(c.Arguments) == 2 {
			if len(items) >= d.limits.MaxCollectionItems {
				return nil, mapping("list exceeds the selected limit")
			}
			items = append(items, c.Arguments[0])
			cursor = c.Arguments[1]
			continue
		}
		if ok && d.label(c) == "nil" && len(c.Arguments) == 0 {
			return items, nil
		}
		return nil, mapping("expected a proper List")
	}
}

// Decode converts a boundary value into target, which must be a non-nil pointer.
func (d *Data) Decode(value protocol.Value, expected protocol.BoundaryType, target any) error {
	out := reflect.ValueOf(target)
	if out.Kind() != reflect.Pointer || out.IsNil() {
		return errors.New("Decode needs a non-nil pointer")
	}
	decoded, err := d.decode(value, out.Elem().Type(), expected)
	if err != nil {
		return err
	}
	out.Elem().Set(decoded)
	return nil
}

func child(expected protocol.BoundaryType) protocol.BoundaryType {
	if n, ok := expected.(protocol.Nominal); ok && len(n.Arguments) > 0 {
		return n.Arguments[0]
	}
	return nil
}

func (d *Data) decode(value protocol.Value, t reflect.Type, expected protocol.BoundaryType) (reflect.Value, error) {
	if t.Kind() == reflect.Interface {
		if cases := d.unions[t]; cases != nil {
			c, ok := value.(protocol.Constructor)
			for _, r := range cases {
				if ok && r.identity == c.Identity {
					member, err := d.decode(value, r.typ, nil)
					if err != nil {
						return reflect.Value{}, err
					}
					out := reflect.New(t).Elem()
					out.Set(member)
					return out, nil
				}
			}
			return reflect.Value{}, mapping("unknown %v case", t)
		}
		if t == valueType {
			out := reflect.New(t).Elem()
			out.Set(reflect.ValueOf(value))
			return out, nil
		}
		native, err := d.decodeAny(value, expected)
		if err != nil {
			return reflect.Value{}, err
		}
		out := reflect.New(t).Elem()
		if native != nil {
			nv := reflect.ValueOf(native)
			if !nv.Type().AssignableTo(t) {
				return reflect.Value{}, mapping("%T does not implement %v", native, t)
			}
			out.Set(nv)
		}
		return out, nil
	}
	if t.Implements(valueType) && t != hashType {
		v := reflect.ValueOf(value)
		if v.Type() != t {
			return reflect.Value{}, mapping("expected %v, got %T", t, value)
		}
		return v, nil
	}
	if r := d.records[t]; r != nil {
		c, ok := value.(protocol.Constructor)
		if !ok || c.Identity != r.identity || len(c.Arguments) != t.NumField() {
			return reflect.Value{}, mapping("%s result has the wrong constructor or field count", r.tag)
		}
		out := reflect.New(t).Elem()
		for i, argument := range c.Arguments {
			field, err := d.decode(argument, t.Field(i).Type, nil)
			if err != nil {
				return reflect.Value{}, err
			}
			out.Field(i).Set(field)
		}
		return out, nil
	}
	if table := d.enums[t]; table != nil {
		c, ok := value.(protocol.Constructor)
		if ok && len(c.Arguments) == 0 {
			if member, found := d.enumByID[c.Identity]; found && member.typ == t {
				return member.value, nil
			}
		}
		return reflect.Value{}, mapping("unknown %v constructor", t)
	}
	switch t {
	case hashType:
		h, ok := value.(protocol.Hash)
		if !ok {
			return reflect.Value{}, mapping("expected Hash")
		}
		return reflect.ValueOf(h), nil
	case varType, someType:
		native, err := d.decodeAny(value, expected)
		if err != nil {
			return reflect.Value{}, err
		}
		v := reflect.ValueOf(native)
		if !v.IsValid() || v.Type() != t {
			return reflect.Value{}, mapping("expected %v, got %T", t, native)
		}
		return v, nil
	}
	out := reflect.New(t).Elem()
	switch t.Kind() {
	case reflect.Bool:
		switch {
		case d.isNullary(value, "true"):
			out.SetBool(true)
		case d.isNullary(value, "false"):
		default:
			return reflect.Value{}, mapping("expected Bool")
		}
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		n, ok := value.(protocol.Int)
		if !ok || out.OverflowInt(int64(n)) {
			return reflect.Value{}, mapping("expected Int that fits %v", t)
		}
		out.SetInt(int64(n))
	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		n, ok := value.(protocol.Int)
		if !ok || n < 0 || out.OverflowUint(uint64(n)) {
			return reflect.Value{}, mapping("expected Int that fits %v", t)
		}
		out.SetUint(uint64(n))
	case reflect.Float32, reflect.Float64:
		r, ok := value.(protocol.Real)
		if !ok {
			return reflect.Value{}, mapping("expected Real")
		}
		out.SetFloat(r.Float64())
	case reflect.String:
		text, ok := value.(protocol.Text)
		if !ok {
			return reflect.Value{}, mapping("expected Text")
		}
		out.SetString(string(text))
	case reflect.Slice:
		if tuple, ok := value.(protocol.Tuple); ok {
			out = reflect.MakeSlice(t, len(tuple), len(tuple))
			for i, item := range tuple {
				decoded, err := d.decode(item, t.Elem(), nil)
				if err != nil {
					return reflect.Value{}, err
				}
				out.Index(i).Set(decoded)
			}
			return out, nil
		}
		items, err := d.listItems(value)
		if err != nil {
			return reflect.Value{}, err
		}
		out = reflect.MakeSlice(t, len(items), len(items))
		for i, item := range items {
			decoded, err := d.decode(item, t.Elem(), child(expected))
			if err != nil {
				return reflect.Value{}, err
			}
			out.Index(i).Set(decoded)
		}
	case reflect.Array:
		tuple, ok := value.(protocol.Tuple)
		if !ok || len(tuple) != t.Len() {
			return reflect.Value{}, mapping("tuple shape mismatch")
		}
		for i, item := range tuple {
			decoded, err := d.decode(item, t.Elem(), nil)
			if err != nil {
				return reflect.Value{}, err
			}
			out.Index(i).Set(decoded)
		}
	case reflect.Pointer:
		if d.isNullary(value, "none") {
			return out, nil
		}
		c, ok := value.(protocol.Constructor)
		if !ok || d.label(c) != "some" || len(c.Arguments) != 1 {
			return reflect.Value{}, mapping("expected Option")
		}
		inner, err := d.decode(c.Arguments[0], t.Elem(), child(expected))
		if err != nil {
			return reflect.Value{}, err
		}
		out = reflect.New(t.Elem())
		out.Elem().Set(inner)
	case reflect.Struct:
		tuple, ok := value.(protocol.Tuple)
		if t.NumField() != 0 || !ok || len(tuple) != 0 {
			return reflect.Value{}, mapping("cannot decode into %v; register it as a record", t)
		}
	default:
		return reflect.Value{}, mapping("cannot decode into %v", t)
	}
	return out, nil
}

// decodeAny converts a boundary value into native Go values: int64, float64,
// string, bool, protocol.Hash, []any, nil for None, Unit, registered records
// and enums, and Variant for other constructors.
func (d *Data) decodeAny(value protocol.Value, expected protocol.BoundaryType) (any, error) {
	switch v := value.(type) {
	case protocol.Int:
		return int64(v), nil
	case protocol.Real:
		return v.Float64(), nil
	case protocol.Text:
		return string(v), nil
	case protocol.Hash:
		return v, nil
	case protocol.Tuple:
		if len(v) == 0 {
			return Unit{}, nil
		}
		items := make([]any, len(v))
		tuple, _ := expected.(protocol.TupleType)
		for i, item := range v {
			var t protocol.BoundaryType
			if i < len(tuple.Items) {
				t = tuple.Items[i]
			}
			decoded, err := d.decodeAny(item, t)
			if err != nil {
				return nil, err
			}
			items[i] = decoded
		}
		return items, nil
	case protocol.Constructor:
		label, ok := d.labels[v.Identity]
		if !ok {
			return nil, mapping("unmapped constructor %s", v.Identity)
		}
		switch label {
		case "true", "false":
			if len(v.Arguments) != 0 {
				return nil, mapping("expected Bool")
			}
			return label == "true", nil
		case "nil", "cons":
			items, err := d.listItems(v)
			if err != nil {
				return nil, err
			}
			out := make([]any, len(items))
			for i, item := range items {
				if out[i], err = d.decodeAny(item, child(expected)); err != nil {
					return nil, err
				}
			}
			return out, nil
		case "none":
			if len(v.Arguments) != 0 {
				return nil, mapping("None has fields")
			}
			return nil, nil
		case "some":
			if len(v.Arguments) != 1 {
				return nil, mapping("Some has the wrong field count")
			}
			inner, err := d.decodeAny(v.Arguments[0], child(expected))
			if err != nil {
				return nil, err
			}
			_, nested := inner.(Some)
			if n, ok := expected.(protocol.Nominal); ok && d.primitives[n.Identity] == "option" && inner != nil && !nested {
				return inner, nil
			}
			return Some{inner}, nil
		}
		if r := d.byID[v.Identity]; r != nil {
			out, err := d.decode(v, r.typ, nil)
			if err != nil {
				return nil, err
			}
			return out.Interface(), nil
		}
		if member, ok := d.enumByID[v.Identity]; ok && len(v.Arguments) == 0 {
			return member.value.Interface(), nil
		}
		fields := make([]any, len(v.Arguments))
		for i, argument := range v.Arguments {
			var err error
			if fields[i], err = d.decodeAny(argument, nil); err != nil {
				return nil, err
			}
		}
		return Variant{Tag: label, Fields: fields}, nil
	}
	return nil, mapping("unsupported value %T", value)
}
