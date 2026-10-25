package protocol

import (
	"encoding/binary"
	"fmt"
	"sort"
	"strconv"
	"strings"
	"unicode/utf16"
	"unicode/utf8"
)

// Object is a decoded JSON object. Decoded JSON values are nil, bool, int64,
// string, []any, or Object; the strict decoder produces nothing else.
type Object = map[string]any

// IntMin and IntMax bound Core's signed 63-bit Int and every JSON integer.
const (
	IntMin = -(int64(1) << 62)
	IntMax = (int64(1) << 62) - 1
)

// ProtocolError carries a frozen v0 diagnostic code such as E1601.
type ProtocolError struct {
	Code    string
	Message string
}

func (e *ProtocolError) Error() string { return e.Code + ": " + e.Message }

func malformed(format string, args ...any) error {
	return &ProtocolError{"E1601", fmt.Sprintf(format, args...)}
}
func exceeded(format string, args ...any) error {
	return &ProtocolError{"E1602", fmt.Sprintf(format, args...)}
}
func outOfOrder(format string, args ...any) error {
	return &ProtocolError{"E1608", fmt.Sprintf(format, args...)}
}
func unsupported(format string, args ...any) error {
	return &ProtocolError{"E1604", fmt.Sprintf(format, args...)}
}

// DecodeObject strictly decodes one UTF-8 JSON object: duplicate keys, lone
// surrogates, non-integer numbers, integers outside 63 bits, trailing data,
// and nesting deeper than maxDepth are refused.
func DecodeObject(payload []byte, maxDepth int) (Object, error) {
	value, err := Decode(payload, maxDepth)
	if err != nil {
		return nil, err
	}
	obj, ok := value.(Object)
	if !ok {
		return nil, malformed("expected a JSON object")
	}
	return obj, nil
}

// Decode strictly decodes one UTF-8 JSON value under the same rules as DecodeObject.
func Decode(payload []byte, maxDepth int) (any, error) {
	if !utf8.Valid(payload) {
		return nil, malformed("payload is not valid UTF-8")
	}
	d := &decoder{text: string(payload), maxDepth: maxDepth}
	value, err := d.value(1)
	if err != nil {
		return nil, err
	}
	d.whitespace()
	if d.pos != len(d.text) {
		return nil, malformed("payload is not one JSON value")
	}
	return value, nil
}

type decoder struct {
	text     string
	pos      int
	maxDepth int
}

func (d *decoder) whitespace() {
	for d.pos < len(d.text) && strings.IndexByte(" \t\r\n", d.text[d.pos]) >= 0 {
		d.pos++
	}
}

func (d *decoder) peek() byte {
	if d.pos < len(d.text) {
		return d.text[d.pos]
	}
	return 0
}

func (d *decoder) value(depth int) (any, error) {
	d.whitespace()
	switch c := d.peek(); {
	case c == '"':
		return d.quoted()
	case c == '{' || c == '[':
		if depth > d.maxDepth {
			return nil, exceeded("JSON nesting exceeds max_json_depth")
		}
		return d.container(c, depth)
	}
	for _, literal := range []struct {
		text  string
		value any
	}{{"true", true}, {"false", false}, {"null", nil}} {
		if strings.HasPrefix(d.text[d.pos:], literal.text) {
			d.pos += len(literal.text)
			return literal.value, nil
		}
	}
	start := d.pos
	if d.peek() == '-' {
		d.pos++
	}
	switch {
	case d.peek() == '0':
		d.pos++
	case d.peek() >= '1' && d.peek() <= '9':
		for d.peek() >= '0' && d.peek() <= '9' {
			d.pos++
		}
	default:
		return nil, malformed("invalid JSON scalar")
	}
	token := d.text[start:d.pos]
	if len(token) > 20 {
		return nil, malformed("invalid JSON scalar")
	}
	integer, err := strconv.ParseInt(token, 10, 64)
	if err != nil || integer < IntMin || integer > IntMax {
		return nil, malformed("JSON integer is outside the 63-bit range")
	}
	return integer, nil
}

func (d *decoder) container(open byte, depth int) (any, error) {
	d.pos++
	d.whitespace()
	close := byte(']')
	var list []any
	var obj Object
	if open == '{' {
		close, obj = '}', Object{}
	} else {
		list = []any{}
	}
	if d.peek() == close {
		d.pos++
		if obj != nil {
			return obj, nil
		}
		return list, nil
	}
	for d.pos < len(d.text) {
		if obj == nil {
			item, err := d.value(depth + 1)
			if err != nil {
				return nil, err
			}
			list = append(list, item)
		} else {
			if d.peek() != '"' {
				return nil, malformed("object key must be a string")
			}
			key, err := d.quoted()
			if err != nil {
				return nil, err
			}
			d.whitespace()
			if _, seen := obj[key]; seen {
				return nil, malformed("duplicate JSON field name")
			}
			if d.peek() != ':' {
				return nil, malformed("expected a colon")
			}
			d.pos++
			item, err := d.value(depth + 1)
			if err != nil {
				return nil, err
			}
			obj[key] = item
		}
		d.whitespace()
		if d.peek() == close {
			d.pos++
			if obj != nil {
				return obj, nil
			}
			return list, nil
		}
		if d.peek() != ',' {
			return nil, malformed("expected a comma")
		}
		d.pos++
		d.whitespace()
	}
	return nil, malformed("unterminated JSON container")
}

func (d *decoder) hex4() (rune, bool) {
	if d.pos+4 > len(d.text) {
		return 0, false
	}
	value, err := strconv.ParseUint(d.text[d.pos:d.pos+4], 16, 32)
	if err != nil {
		return 0, false
	}
	d.pos += 4
	return rune(value), true
}

func (d *decoder) quoted() (string, error) {
	d.pos++
	var out strings.Builder
	for d.pos < len(d.text) {
		c := d.text[d.pos]
		switch {
		case c == '"':
			d.pos++
			return out.String(), nil
		case c < 0x20:
			return "", malformed("invalid JSON string")
		case c != '\\':
			out.WriteByte(c)
			d.pos++
			continue
		}
		d.pos++
		if d.pos >= len(d.text) {
			break
		}
		escape := d.text[d.pos]
		d.pos++
		if simple, ok := map[byte]byte{'"': '"', '\\': '\\', '/': '/', 'b': '\b', 'f': '\f', 'n': '\n', 'r': '\r', 't': '\t'}[escape]; ok {
			out.WriteByte(simple)
			continue
		}
		if escape != 'u' {
			return "", malformed("invalid JSON string")
		}
		r, ok := d.hex4()
		if !ok {
			return "", malformed("invalid JSON string")
		}
		if utf16.IsSurrogate(r) {
			if r >= 0xdc00 || !strings.HasPrefix(d.text[d.pos:], `\u`) {
				return "", malformed("a JSON string contains a lone surrogate")
			}
			d.pos += 2
			low, ok := d.hex4()
			if !ok {
				return "", malformed("invalid JSON string")
			}
			if r = utf16.DecodeRune(r, low); r == utf8.RuneError {
				return "", malformed("a JSON string contains a lone surrogate")
			}
		}
		out.WriteRune(r)
	}
	return "", malformed("unterminated JSON string")
}

// Stringify encodes a JSON value canonically: sorted keys, no whitespace, and
// the same escaping as JavaScript's JSON.stringify. Integers must be int or
// int64 within the 63-bit range.
func Stringify(value any, maxDepth int) (string, error) {
	var out strings.Builder
	if err := stringify(&out, value, maxDepth, 1); err != nil {
		return "", err
	}
	return out.String(), nil
}

func stringify(out *strings.Builder, value any, maxDepth, depth int) error {
	switch v := value.(type) {
	case nil:
		out.WriteString("null")
	case bool:
		out.WriteString(strconv.FormatBool(v))
	case int:
		return stringify(out, int64(v), maxDepth, depth)
	case int64:
		if v < IntMin || v > IntMax {
			return malformed("JSON integer is outside the 63-bit range")
		}
		out.WriteString(strconv.FormatInt(v, 10))
	case string:
		return quote(out, v)
	case []any:
		if depth > maxDepth {
			return exceeded("JSON nesting exceeds max_json_depth")
		}
		out.WriteByte('[')
		for i, item := range v {
			if i > 0 {
				out.WriteByte(',')
			}
			if err := stringify(out, item, maxDepth, depth+1); err != nil {
				return err
			}
		}
		out.WriteByte(']')
	case Object:
		if depth > maxDepth {
			return exceeded("JSON nesting exceeds max_json_depth")
		}
		keys := make([]string, 0, len(v))
		for key := range v {
			keys = append(keys, key)
		}
		sort.Strings(keys)
		out.WriteByte('{')
		for i, key := range keys {
			if i > 0 {
				out.WriteByte(',')
			}
			if err := quote(out, key); err != nil {
				return err
			}
			out.WriteByte(':')
			if err := stringify(out, v[key], maxDepth, depth+1); err != nil {
				return err
			}
		}
		out.WriteByte('}')
	default:
		return malformed("%T has no JSON encoding", value)
	}
	return nil
}

func quote(out *strings.Builder, text string) error {
	if !utf8.ValidString(text) {
		return malformed("a JSON string is not valid UTF-8")
	}
	out.WriteByte('"')
	for i := 0; i < len(text); i++ {
		c := text[i]
		switch c {
		case '"':
			out.WriteString(`\"`)
		case '\\':
			out.WriteString(`\\`)
		case '\b':
			out.WriteString(`\b`)
		case '\f':
			out.WriteString(`\f`)
		case '\n':
			out.WriteString(`\n`)
		case '\r':
			out.WriteString(`\r`)
		case '\t':
			out.WriteString(`\t`)
		default:
			if c < 0x20 {
				fmt.Fprintf(out, `\u%04x`, c)
			} else {
				out.WriteByte(c)
			}
		}
	}
	out.WriteByte('"')
	return nil
}

// FrameBytes encodes one carrier frame: a big-endian u32 length and the
// canonical JSON payload, refusing payloads outside 1..maxBytes.
func FrameBytes(frame Object, maxBytes, maxDepth int) ([]byte, error) {
	payload, err := Stringify(frame, maxDepth)
	if err != nil {
		return nil, err
	}
	if len(payload) < 1 || len(payload) > maxBytes {
		return nil, exceeded("payload exceeds max_frame_bytes")
	}
	out := make([]byte, 4, 4+len(payload))
	binary.BigEndian.PutUint32(out, uint32(len(payload)))
	return append(out, payload...), nil
}

func asObject(value any) (Object, error) {
	obj, ok := value.(Object)
	if !ok {
		return nil, malformed("expected a JSON object")
	}
	return obj, nil
}

// exact requires an object with exactly the given keys.
func exact(value any, keys ...string) (Object, error) {
	obj, err := asObject(value)
	if err != nil {
		return nil, err
	}
	if len(obj) != len(keys) {
		return nil, malformed("expected exactly: %s", strings.Join(keys, ", "))
	}
	for _, key := range keys {
		if _, ok := obj[key]; !ok {
			return nil, malformed("expected exactly: %s", strings.Join(keys, ", "))
		}
	}
	return obj, nil
}

func asString(value any) (string, error) {
	text, ok := value.(string)
	if !ok {
		return "", malformed("expected a string")
	}
	return text, nil
}

func asArray(value any) ([]any, error) {
	items, ok := value.([]any)
	if !ok {
		return nil, malformed("expected an array")
	}
	return items, nil
}
