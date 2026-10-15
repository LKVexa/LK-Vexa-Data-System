package main

import (
	"bufio"
	"bytes"
	"encoding/binary"
	"io"
	"net"
	"sync"
	"testing"
	"time"
)

type observedWriteConn struct {
	net.Conn
	started chan struct{}
	once    sync.Once
}

func (c *observedWriteConn) Write(p []byte) (int, error) {
	c.once.Do(func() { close(c.started) })
	return c.Conn.Write(p)
}
func TestCloseBoundedReleasesSaturatedWriter(t *testing.T) {
	a, c := net.Pipe()
	defer c.Close()
	observed := &observedWriteConn{Conn: a, started: make(chan struct{})}
	w := newWS(observed, bufio.NewReader(observed), 512)
	done := make(chan error, 1)
	go func() { done <- w.Send([]byte("blocked output"), 1) }()
	<-observed.started
	started := time.Now()
	w.CloseBounded(1001, 30*time.Millisecond)
	if time.Since(started) > time.Second {
		t.Fatal("close waited on saturated writer")
	}
	if e := <-done; e == nil {
		t.Fatal("saturated writer unexpectedly completed")
	}
}

func maskedFrame(op byte, fin bool, data []byte) []byte {
	f := []byte{op, 0}
	if fin {
		f[0] |= 128
	}
	n := len(data)
	if n < 126 {
		f[1] = 128 | byte(n)
	} else if n <= 65535 {
		f[1] = 128 | 126
		f = append(f, byte(n>>8), byte(n))
	} else {
		f[1] = 128 | 127
		b := make([]byte, 8)
		binary.BigEndian.PutUint64(b, uint64(n))
		f = append(f, b...)
	}
	mask := []byte{17, 29, 43, 59}
	f = append(f, mask...)
	for i, c := range data {
		f = append(f, c^mask[i%4])
	}
	return f
}
func readServerFrame(r *bufio.Reader) ([]byte, byte, error) {
	h := make([]byte, 2)
	if _, e := io.ReadFull(r, h); e != nil {
		return nil, 0, e
	}
	if h[0]&128 == 0 || h[1]&128 != 0 {
		return nil, 0, wireError("bad server frame")
	}
	n := uint64(h[1] & 127)
	if n == 126 {
		b := make([]byte, 2)
		if _, e := io.ReadFull(r, b); e != nil {
			return nil, 0, e
		}
		n = uint64(binary.BigEndian.Uint16(b))
	} else if n == 127 {
		b := make([]byte, 8)
		if _, e := io.ReadFull(r, b); e != nil {
			return nil, 0, e
		}
		n = binary.BigEndian.Uint64(b)
	}
	if n > 10*1024*1024 {
		return nil, 0, wireError("fixture frame too large")
	}
	b := make([]byte, int(n))
	_, e := io.ReadFull(r, b)
	return b, h[0] & 15, e
}
func TestWebSocketMalformedInput(t *testing.T) {
	cases := map[string][]byte{
		"unmasked": {0x81, 0}, "RSV": {0xc1, 0x80}, "unknown opcode": {0x83, 0x80}, "nonminimal16": {0x81, 0xfe, 0, 1},
		"nonminimal64": {0x81, 0xff, 0, 0, 0, 0, 0, 0, 0, 126}, "signed64": {0x81, 0xff, 128, 0, 0, 0, 0, 0, 0, 0},
		"fragmented control": {0x09, 0x80}, "oversized control": {0x89, 0xfe, 0, 126},
		"unexpected continuation": maskedFrame(0, true, []byte("x")), "invalid UTF8": maskedFrame(1, true, []byte{255}),
		"onebyte close": maskedFrame(8, true, []byte{1}), "reserved closecode": maskedFrame(8, true, []byte{3, 237}),
		"invalid close reason": maskedFrame(8, true, []byte{3, 232, 255}), "inputlimit": maskedFrame(1, true, bytes.Repeat([]byte("x"), 513)),
	}
	for name, frame := range cases {
		t.Run(name, func(t *testing.T) {
			a, c := net.Pipe()
			defer a.Close()
			defer c.Close()
			go func() { _ = writeAll(c, frame); _ = c.Close() }()
			w := newWS(a, bufio.NewReader(a), 512)
			if _, _, e := w.Receive(); e == nil {
				t.Fatal("malformed frame was admitted")
			}
		})
	}
}
func TestWebSocketTextBinaryAndLengths(t *testing.T) {
	for _, size := range []int{0, 1, 125, 126, 65535, 65536} {
		for _, op := range []byte{1, 2} {
			t.Run(string(rune('A'+op))+"/"+itoa(size), func(t *testing.T) {
				payload := bytes.Repeat([]byte("x"), size)
				a, c := net.Pipe()
				defer a.Close()
				defer c.Close()
				go func() { _ = writeAll(c, maskedFrame(op, true, payload)) }()
				w := newWS(a, bufio.NewReader(a), 81920)
				got, kind, e := w.Receive()
				if e != nil || kind != op || !bytes.Equal(got, payload) {
					t.Fatalf("frame mismatch kind=%d error=%v", kind, e)
				}
			})
		}
	}
}
func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	s := ""
	for n > 0 {
		s = string(rune('0'+n%10)) + s
		n /= 10
	}
	return s
}
func TestWebSocketFragmentedTextAndPing(t *testing.T) {
	a, c := net.Pipe()
	defer a.Close()
	defer c.Close()
	w := newWS(a, bufio.NewReader(a), 512)
	result := make(chan []byte, 1)
	errCh := make(chan error, 1)
	go func() {
		b, op, e := w.Receive()
		if e == nil && op != 1 {
			e = wireError("text type lost")
		}
		result <- b
		errCh <- e
	}()
	if e := writeAll(c, maskedFrame(1, false, []byte("he"))); e != nil {
		t.Fatal(e)
	}
	if e := writeAll(c, maskedFrame(9, true, []byte("ping"))); e != nil {
		t.Fatal(e)
	}
	pong, op, e := readServerFrame(bufio.NewReader(c))
	if e != nil || op != 10 || string(pong) != "ping" {
		t.Fatalf("pong: %q %d %v", pong, op, e)
	}
	if e = writeAll(c, maskedFrame(0, true, []byte("llo"))); e != nil {
		t.Fatal(e)
	}
	if got := <-result; string(got) != "hello" {
		t.Fatalf("fragment payload %q", got)
	}
	if e = <-errCh; e != nil {
		t.Fatal(e)
	}
}
func TestWebSocketFragmentLimitsAndInterleaving(t *testing.T) {
	for name, frames := range map[string][]byte{
		"interleaving":    append(maskedFrame(1, false, []byte("a")), maskedFrame(2, true, []byte("b"))...),
		"aggregate limit": append(maskedFrame(1, false, bytes.Repeat([]byte("x"), 300)), maskedFrame(0, true, bytes.Repeat([]byte("x"), 300))...),
		"129 fragments": func() []byte {
			b := maskedFrame(1, false, nil)
			for i := 0; i < 128; i++ {
				b = append(b, maskedFrame(0, i == 127, nil)...)
			}
			return b
		}(),
	} {
		t.Run(name, func(t *testing.T) {
			a, c := net.Pipe()
			defer a.Close()
			defer c.Close()
			go func() { _ = writeAll(c, frames); _ = c.Close() }()
			if _, _, e := newWS(a, bufio.NewReader(a), 512).Receive(); e == nil {
				t.Fatal("invalid fragmentation admitted")
			}
		})
	}
}
func TestWebSocketAuthenticationDeadline(t *testing.T) {
	a, c := net.Pipe()
	defer a.Close()
	defer c.Close()
	w := newWS(a, bufio.NewReader(a), 512)
	w.authUntil = time.Now().Add(30 * time.Millisecond)
	go func() { _ = writeAll(c, []byte{0x81, 0x81}) }()
	started := time.Now()
	if _, _, e := w.Receive(); e == nil {
		t.Fatal("stalled auth admitted")
	}
	if time.Since(started) > time.Second {
		t.Fatal("authentication deadline ignored")
	}
}
func TestWebSocketOutputLimitAndCloseCodes(t *testing.T) {
	a, c := net.Pipe()
	defer a.Close()
	defer c.Close()
	w := newWS(a, bufio.NewReader(a), 512)
	if e := w.Send(make([]byte, 513), 1); e == nil {
		t.Fatal("output limit ignored")
	}
	for _, code := range []uint16{1004, 1005, 1006, 1015, 2999, 5000} {
		if validClose(code) {
			t.Fatalf("reserved close code %d admitted", code)
		}
	}
	for _, code := range []uint16{1000, 1001, 1014, 3000, 4999} {
		if !validClose(code) {
			t.Fatalf("valid close code %d rejected", code)
		}
	}
}
