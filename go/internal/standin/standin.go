// Package standin lets a Go test binary act as a recorded or faulty worker for
// offline tests. It implements no Core semantics.
package standin

import (
	"encoding/binary"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"os/exec"
	"strings"
	"time"

	"github.com/jbwinters/jacquard-host/go/kit"
	"github.com/jbwinters/jacquard-host/go/protocol"
)

// Variables naming the stand-in behaviour in the child environment.
const (
	ModeVar = "JACQUARD_GO_STANDIN"
	CaseVar = "JACQUARD_GO_STANDIN_CASE"
)

// Env returns an environment that runs the test binary as a stand-in.
func Env(mode, transcript string) []string {
	// A race-enabled stand-in otherwise lingers for one second after exiting.
	return append(os.Environ(), ModeVar+"="+mode, CaseVar+"="+transcript, "GORACE=atexit_sleep_ms=0")
}

// Command runs the current test binary.
func Command() []string { return []string{os.Args[0]} }

// MainIfRequested runs the stand-in and exits when the environment asks for
// one. Call it first in TestMain.
func MainIfRequested() {
	mode := os.Getenv(ModeVar)
	if mode == "" {
		return
	}
	switch mode {
	case "exit":
		os.Exit(0)
	case "silent":
		block()
	case "truncated":
		_, _ = os.Stdout.Write([]byte{0, 0, 0})
		os.Exit(0)
	case "escaped-descendant":
		// A descendant in its own session holds stderr; group kills miss it.
		child, err := startEscaped()
		if err != nil {
			os.Exit(3)
		}
		fmt.Fprintf(os.Stderr, "%d", child.Process.Pid)
		loaded, err := kit.Load()
		if err != nil {
			os.Exit(3)
		}
		os.Exit(fault(loaded, "ok"))
	case "stderr-descendant", "stderr-descendant-nonzero":
		// Answer normally, but leave a descendant holding only stderr.
		child := exec.Command("sleep", "30")
		child.Stderr = os.Stderr
		if child.Start() != nil {
			os.Exit(3)
		}
		fmt.Fprintf(os.Stderr, "%d", child.Process.Pid)
		loaded, err := kit.Load()
		if err != nil {
			os.Exit(3)
		}
		if mode == "stderr-descendant-nonzero" {
			os.Exit(fault(loaded, "nonzero"))
		}
		os.Exit(fault(loaded, "ok"))
	case "descendant":
		// Leave a descendant holding stdout, like a launcher that does not exec.
		child := exec.Command("sleep", "30")
		child.Stdout = os.Stdout
		if child.Start() != nil {
			os.Exit(3)
		}
		fmt.Fprintf(os.Stderr, "%d", child.Process.Pid)
		os.Exit(0)
	}
	loaded, err := kit.Load()
	if err != nil {
		os.Exit(3)
	}
	if mode == "replay" {
		os.Exit(replay(loaded, os.Getenv(CaseVar)))
	}
	os.Exit(fault(loaded, mode))
}

func send(frame protocol.Object) {
	payload, err := protocol.Stringify(frame, 256)
	if err != nil {
		os.Exit(3)
	}
	var prefix [4]byte
	binary.BigEndian.PutUint32(prefix[:], uint32(len(payload)))
	_, _ = os.Stdout.Write(append(prefix[:], payload...))
}

func readFrame() bool {
	var prefix [4]byte
	if _, err := io.ReadFull(os.Stdin, prefix[:]); err != nil {
		return false
	}
	_, err := io.CopyN(io.Discard, os.Stdin, int64(binary.BigEndian.Uint32(prefix[:])))
	return err == nil
}

func replay(loaded *kit.Kit, name string) int {
	var transcript *kit.Transcript
	for i := range loaded.Transcripts {
		if loaded.Transcripts[i].Name == name {
			transcript = &loaded.Transcripts[i]
		}
	}
	if transcript == nil {
		return 3
	}
	index, raw := 0, false
	for _, step := range transcript.HostInput {
		switch {
		case step.Direction == "core_to_host":
			frame := transcript.CoreOutput[index]
			index++
			send(frame)
			if kind, _ := frame["kind"].(string); strings.Contains("outcome fatal shutdown_ack", kind) {
				return transcript.ExitStatus
			}
		case step.HasRaw:
			data, _ := hex.DecodeString(step.RawHex)
			_, _ = io.CopyN(io.Discard, os.Stdin, int64(len(data)))
			raw = true
		case !step.AfterTerminal && !raw:
			readFrame()
		}
	}
	return transcript.ExitStatus
}

func block() {
	for {
		time.Sleep(time.Hour)
	}
}

func fault(loaded *kit.Kit, mode string) int {
	pure := loaded.Transcripts[0]
	hello, outcome := pure.CoreOutput[0], pure.CoreOutput[1]
	if mode == "stderr" {
		_, _ = os.Stderr.Write([]byte(strings.Repeat("x", 2*1024*1024)))
	}
	if mode == "unknown-field" {
		altered := protocol.Object{"extra": int64(1)}
		for key, value := range hello {
			altered[key] = value
		}
		hello = altered
	}
	send(hello)
	if mode == "no-read" {
		block()
	}
	for count := 1; readFrame(); count++ {
		if count != 2 {
			continue
		}
		if mode == "invalid-hash" {
			outcome = withCallable(outcome, strings.Repeat("A", 64))
		}
		send(outcome)
		if mode == "extra" {
			send(outcome)
		}
		if mode == "stall" {
			block()
		}
		_ = os.Stdout.Close()
		if mode == "slow-exit" {
			time.Sleep(1500 * time.Millisecond)
		}
		if mode == "nonzero" {
			return 74
		}
		return 0
	}
	return 0
}

func withCallable(outcome protocol.Object, callable string) protocol.Object {
	copyOf := func(value any) protocol.Object {
		out := protocol.Object{}
		for key, item := range value.(protocol.Object) {
			out[key] = item
		}
		return out
	}
	frame, evidence := copyOf(outcome), copyOf(outcome["evidence"])
	core, target := copyOf(evidence["core"]), copyOf(evidence["core"].(protocol.Object)["target"])
	target["callable"] = callable
	core["target"], evidence["core"], frame["evidence"] = target, core, evidence
	return frame
}
