package jacquardhost_test

import (
	"bytes"
	"os"
	"testing"
)

// The Go module zip carries go/NOTICE but not the repository's root NOTICE,
// so the module keeps an identical copy.
func TestModuleNoticeMatchesTheRepository(t *testing.T) {
	root, err := os.ReadFile("../NOTICE")
	if err != nil {
		t.Skip("repository NOTICE is not available outside a checkout")
	}
	module, err := os.ReadFile("NOTICE")
	if err != nil || !bytes.Equal(root, module) {
		t.Fatal("go/NOTICE differs from the repository NOTICE; copy it again")
	}
}
