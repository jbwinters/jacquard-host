package jacquard

import (
	"fmt"
	"regexp"
	"slices"
	"strings"
	"unicode"

	"github.com/jbwinters/jacquard-host/go/protocol"
)

// entry is one public name and identity printed by `jacquard hash`.
type entry struct {
	Name string        `json:"name"`
	Hash protocol.Hash `json:"hash"`
}

// group is one declaration group: a type or effect identity followed by its
// constructors or operations, in Core's printed order.
type group []entry

var hashLine = regexp.MustCompile(`^(\d+):(\S+) ([0-9a-f]{64})$`)

// hashGroups parses `jacquard hash` output into declaration groups.
func hashGroups(output string) ([]group, error) {
	var order []string
	groups := map[string]group{}
	for _, line := range strings.Split(output, "\n") {
		match := hashLine.FindStringSubmatch(strings.TrimSpace(line))
		if match == nil {
			continue
		}
		if _, seen := groups[match[1]]; !seen {
			order = append(order, match[1])
		}
		groups[match[1]] = append(groups[match[1]], entry{match[2], protocol.Hash(match[3])})
	}
	if len(order) == 0 {
		return nil, &BindingError{Message: "Core hash command returned no public identities"}
	}
	out := make([]group, len(order))
	for i, key := range order {
		out[i] = groups[key]
	}
	return out, nil
}

// checkGroups refuses a public name that denotes different identities in
// different declaration groups.
func checkGroups(groups []group) error {
	seen := map[string]map[protocol.Hash]bool{}
	for _, g := range groups {
		current := map[string]map[protocol.Hash]bool{}
		for _, e := range g {
			if current[e.Name] == nil {
				current[e.Name] = map[protocol.Hash]bool{}
			}
			current[e.Name][e.Hash] = true
		}
		for name, hashes := range current {
			if previous := seen[name]; previous != nil {
				for hash := range hashes {
					if !previous[hash] {
						return &BindingError{Message: fmt.Sprintf("ambiguous public name %s occurs in different declaration groups; use distinct names or explicit low-level identities", name)}
					}
				}
			}
			if seen[name] == nil {
				seen[name] = map[protocol.Hash]bool{}
			}
			for hash := range hashes {
				seen[name][hash] = true
			}
		}
	}
	return nil
}

// displayToStore converts a display name such as WorkToday to its store
// spelling, work-today; lowercase names are unchanged.
func displayToStore(name string) string {
	if name == "" || !unicode.IsUpper(rune(name[0])) {
		return name
	}
	var out strings.Builder
	for i, r := range name {
		if i > 0 && unicode.IsUpper(r) {
			out.WriteByte('-')
		}
		out.WriteRune(unicode.ToLower(r))
	}
	return out.String()
}

// normal is the effect-name form used for service lookup.
func normal(name string) string { return strings.ReplaceAll(displayToStore(name), "_", "-") }

// goName converts an operation such as booking-status to BookingStatus.
func goName(operation string) string {
	var out strings.Builder
	upper := true
	for _, r := range operation {
		if r == '-' || r == '.' || r == '_' {
			upper = true
			continue
		}
		if upper {
			r = unicode.ToUpper(r)
			upper = false
		}
		out.WriteRune(r)
	}
	return out.String()
}

var signatureToken = regexp.MustCompile(`^\s*(->\{|[(),{}]|[A-Za-z][A-Za-z0-9.?!-]*)`)

// parseSignature reads a closed v0 signature printed by `check --print-sigs`,
// such as "(List Line, Int) ->{Inventory} Option Text". Polymorphic and
// effect-polymorphic signatures are refused.
func parseSignature(text string, names map[string]protocol.Hash) (protocol.Interface, error) {
	var tokens []string
	rest := text
	for strings.TrimSpace(rest) != "" {
		match := signatureToken.FindStringSubmatch(rest)
		if match == nil {
			return protocol.Interface{}, fmt.Errorf("unsupported signature near %.12q", strings.TrimSpace(rest))
		}
		tokens = append(tokens, match[1])
		rest = rest[len(match[0]):]
	}
	p := &signatureParser{tokens: tokens, names: names}
	return p.parse()
}

type signatureParser struct {
	tokens []string
	index  int
	names  map[string]protocol.Hash
}

func (p *signatureParser) peek() string {
	if p.index < len(p.tokens) {
		return p.tokens[p.index]
	}
	return ""
}

func (p *signatureParser) take(expected string) (string, error) {
	token := p.peek()
	if token == "" || (expected != "" && token != expected) {
		return "", fmt.Errorf("expected %q, found %q", expected, token)
	}
	p.index++
	return token, nil
}

func (p *signatureParser) identity(display string) (protocol.Hash, error) {
	if id, ok := p.names[displayToStore(display)]; ok {
		return id, nil
	}
	if id, ok := p.names[display]; ok {
		return id, nil
	}
	return "", fmt.Errorf("no identity for %s", display)
}

func (p *signatureParser) list(close string) ([]protocol.BoundaryType, error) {
	var out []protocol.BoundaryType
	if p.peek() == close {
		p.index++
		return out, nil
	}
	for {
		item, err := p.typ()
		if err != nil {
			return nil, err
		}
		out = append(out, item)
		if p.peek() == "," {
			p.index++
			if p.peek() == close {
				p.index++
				return out, nil
			}
			continue
		}
		if _, err := p.take(close); err != nil {
			return nil, err
		}
		return out, nil
	}
}

func (p *signatureParser) atom() (protocol.BoundaryType, error) {
	token, err := p.take("")
	if err != nil {
		return nil, err
	}
	if token == "(" {
		items, err := p.list(")")
		if err != nil {
			return nil, err
		}
		if len(items) == 1 && p.tokens[p.index-2] != "," {
			return items[0], nil
		}
		return protocol.TupleType{Items: items}, nil
	}
	if !unicode.IsLetter(rune(token[0])) {
		return nil, fmt.Errorf("unsupported type %q", token)
	}
	id, err := p.identity(token)
	if err != nil {
		return nil, err
	}
	return protocol.Nominal{Identity: id}, nil
}

func (p *signatureParser) typ() (protocol.BoundaryType, error) {
	head, err := p.atom()
	if err != nil {
		return nil, err
	}
	nominal, ok := head.(protocol.Nominal)
	if !ok {
		return head, nil
	}
	for token := p.peek(); token != "" && !slices.Contains([]string{",", ")", "}", "->{"}, token); token = p.peek() {
		argument, err := p.atom()
		if err != nil {
			return nil, err
		}
		nominal.Arguments = append(nominal.Arguments, argument)
	}
	return nominal, nil
}

func (p *signatureParser) parse() (protocol.Interface, error) {
	if _, err := p.take("("); err != nil {
		return protocol.Interface{}, err
	}
	parameters, err := p.list(")")
	if err != nil {
		return protocol.Interface{}, err
	}
	if _, err := p.take("->{"); err != nil {
		return protocol.Interface{}, err
	}
	var effects []protocol.Hash
	if p.peek() != "}" {
		for {
			name, err := p.take("")
			if err != nil {
				return protocol.Interface{}, err
			}
			id, err := p.identity(name)
			if err != nil {
				return protocol.Interface{}, err
			}
			if !slices.Contains(effects, id) {
				effects = append(effects, id)
			}
			if p.peek() != "," {
				break
			}
			p.index++
		}
	}
	if _, err := p.take("}"); err != nil {
		return protocol.Interface{}, err
	}
	result, err := p.typ()
	if err != nil {
		return protocol.Interface{}, err
	}
	if p.peek() != "" {
		return protocol.Interface{}, fmt.Errorf("trailing signature tokens")
	}
	slices.Sort(effects)
	return protocol.Interface{Parameters: parameters, Result: result, Effects: effects}, nil
}
