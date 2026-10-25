package jacquard

import (
	"fmt"

	"github.com/jbwinters/jacquard-host/go/host"
)

// PreparationError reports that a program could not be checked, hashed,
// installed, or loaded from its cache.
type PreparationError struct {
	Message string
	Err     error
}

func (e *PreparationError) Error() string { return e.Message }
func (e *PreparationError) Unwrap() error { return e.Err }

// BindingError reports a mapping or binding problem: an unknown name, a
// missing handler, or a successful Core result that does not decode into the
// requested Go type. Result is set when Core completed.
type BindingError struct {
	Message string
	Result  *host.InvocationResult
}

func (e *BindingError) Error() string { return e.Message }

// InvalidArgument reports a local input that cannot be sent; no worker starts.
type InvalidArgument struct{ Message string }

func (e *InvalidArgument) Error() string { return e.Message }

func invalid(path, format string, args ...any) error {
	return &InvalidArgument{Message: path + ": " + fmt.Sprintf(format, args...)}
}

// BatchError reports which explicit batch failed. Completed batches are not
// retried; their decoded values are returned alongside the error.
type BatchError struct {
	Index int
	Err   error
}

func (e *BatchError) Error() string { return fmt.Sprintf("batch %d: %v", e.Index, e.Err) }
func (e *BatchError) Unwrap() error { return e.Err }
