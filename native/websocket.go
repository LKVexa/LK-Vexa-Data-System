package main

import (
	"bufio"
	"encoding/binary"
	"errors"
	"io"
	"net"
	"sync"
	"sync/atomic"
	"time"
	"unicode/utf8"
)

type wireError string

func (e wireError) Error() string { return string(e) }

// WS is the server half of the bounded RFC6455 connection. HTTP upgrade parsing
// retains its buffered reader, so a frame coalesced with the handshake is kept.
type WS struct {
	conn         net.Conn
	r            *bufio.Reader
	limit        int64
	receiveLimit int64
	authUntil    time.Time
	writeMu      sync.Mutex
	pending      atomic.Int64
	closed       atomic.Bool
	closeOnce    sync.Once
}

func newWS(c net.Conn, r *bufio.Reader, limit int64) *WS { return &WS{conn: c, r: r, limit: limit} }
func (w *WS) Send(data []byte, op byte) error {
	if w.closed.Load() {
		return io.ErrClosedPipe
	}
	bound := w.limit
	if op >= 8 {
		bound = 125
	}
	if int64(len(data)) > bound {
		return wireError("output limit")
	}
	n := int64(len(data))
	pending := w.pending.Add(n)
	defer w.pending.Add(-n)
	max := 2 * w.limit
	if max < 250 {
		max = 250
	}
	if pending > max {
		return wireError("output backpressure")
	}
	w.writeMu.Lock()
	defer w.writeMu.Unlock()
	if w.closed.Load() {
		return io.ErrClosedPipe
	}
	if e := w.conn.SetWriteDeadline(time.Now().Add(10 * time.Second)); e != nil {
		return e
	}
	h := make([]byte, 2, 10)
	h[0] = 0x80 | op
	if n < 126 {
		h[1] = byte(n)
	} else if n <= 65535 {
		h[1] = 126
		h = append(h, byte(n>>8), byte(n))
	} else {
		h[1] = 127
		b := make([]byte, 8)
		binary.BigEndian.PutUint64(b, uint64(n))
		h = append(h, b...)
	}
	if e := writeAll(w.conn, h); e != nil {
		return e
	}
	return writeAll(w.conn, data)
}
func (w *WS) Obj(v any) error {
	b, e := jsonBytes(v)
	if e != nil {
		return e
	}
	return w.Send(b, 1)
}
func (w *WS) Close(code uint16) {
	w.closeOnce.Do(func() {
		b := []byte{byte(code >> 8), byte(code)}
		_ = w.Send(b, 8)
		w.closed.Store(true)
		_ = w.conn.Close()
	})
}

// CloseBounded permits a close frame while bounding a saturated socket or a
// writer waiting on writeMu. Closing the connection releases any blocked send.
func (w *WS) CloseBounded(code uint16, limit time.Duration) {
	timer := time.AfterFunc(limit, func() { _ = w.conn.Close() })
	w.Close(code)
	timer.Stop()
}
func (w *WS) Receive() ([]byte, byte, error) {
	var data []byte
	var kind byte
	var deadline time.Time
	controls, fragments := 0, 0
	controlWindow := time.Now()
	for {
		until := time.Now().Add(15 * time.Minute)
		if !deadline.IsZero() {
			until = deadline
		}
		if !w.authUntil.IsZero() && w.authUntil.Before(until) {
			until = w.authUntil
		}
		if e := w.conn.SetReadDeadline(until); e != nil {
			return nil, 0, e
		}
		head := make([]byte, 2)
		if _, e := io.ReadFull(w.r, head); e != nil {
			return nil, 0, e
		}
		fin := head[0]&128 != 0
		op := head[0] & 15
		masked := head[1]&128 != 0
		n := uint64(head[1] & 127)
		if head[0]&112 != 0 || !masked || (op != 0 && op != 1 && op != 2 && op != 8 && op != 9 && op != 10) {
			return nil, 0, wireError("bad frame")
		}
		frameDeadline := time.Now().Add(10 * time.Second)
		if !deadline.IsZero() && deadline.Before(frameDeadline) {
			frameDeadline = deadline
		}
		if !w.authUntil.IsZero() && w.authUntil.Before(frameDeadline) {
			frameDeadline = w.authUntil
		}
		if op < 8 && deadline.IsZero() {
			deadline = frameDeadline
		}
		_ = w.conn.SetReadDeadline(frameDeadline)
		if n == 126 {
			b := make([]byte, 2)
			if _, e := io.ReadFull(w.r, b); e != nil {
				return nil, 0, e
			}
			n = uint64(binary.BigEndian.Uint16(b))
			if n < 126 {
				return nil, 0, wireError("nonminimal length")
			}
		} else if n == 127 {
			b := make([]byte, 8)
			if _, e := io.ReadFull(w.r, b); e != nil {
				return nil, 0, e
			}
			n = binary.BigEndian.Uint64(b)
			if n <= 65535 || n >= 1<<63 {
				return nil, 0, wireError("nonminimal length")
			}
		}
		if op >= 8 && (!fin || n > 125) {
			return nil, 0, wireError("bad control")
		}
		bound := w.limit
		if w.receiveLimit > 0 {
			bound = w.receiveLimit
		}
		if op < 8 && (n > uint64(bound) || uint64(len(data))+n > uint64(bound)) {
			return nil, 0, wireError("input limit")
		}
		mask := make([]byte, 4)
		if _, e := io.ReadFull(w.r, mask); e != nil {
			return nil, 0, e
		}
		raw := make([]byte, int(n))
		if _, e := io.ReadFull(w.r, raw); e != nil {
			return nil, 0, e
		}
		for i := range raw {
			raw[i] ^= mask[i%4]
		}
		if op == 8 {
			if len(raw) == 1 {
				return nil, 0, wireError("bad close")
			}
			if len(raw) >= 2 {
				code := binary.BigEndian.Uint16(raw)
				if !validClose(code) || !utf8.Valid(raw[2:]) {
					return nil, 0, wireError("bad close code/reason")
				}
			}
			w.CloseBounded(1000, 250*time.Millisecond)
			return nil, 0, io.EOF
		}
		if op == 9 || op == 10 {
			if time.Since(controlWindow) >= 10*time.Second {
				controlWindow = time.Now()
				controls = 0
			}
			controls++
			if controls > 32 {
				return nil, 0, wireError("control flood")
			}
			if op == 9 {
				if e := w.Send(raw, 10); e != nil {
					return nil, 0, e
				}
			}
			continue
		}
		if op == 0 {
			if kind == 0 {
				return nil, 0, wireError("unexpected continuation")
			}
		} else {
			if kind != 0 {
				return nil, 0, wireError("interleaved fragments")
			}
			kind = op
		}
		fragments++
		if fragments > 128 {
			return nil, 0, wireError("fragment count limit")
		}
		data = append(data, raw...)
		if fin {
			if kind == 1 && !utf8.Valid(data) {
				return nil, 0, wireError("invalid UTF-8 text")
			}
			return data, kind, nil
		}
	}
}
func validClose(c uint16) bool {
	switch c {
	case 1000, 1001, 1002, 1003, 1007, 1008, 1009, 1010, 1011, 1012, 1013, 1014:
		return true
	}
	return c >= 3000 && c <= 4999
}
func writeAll(w io.Writer, b []byte) error {
	for len(b) > 0 {
		n, e := w.Write(b)
		if e != nil {
			return e
		}
		if n <= 0 {
			return io.ErrShortWrite
		}
		b = b[n:]
	}
	return nil
}
func isDisconnect(e error) bool {
	return errors.Is(e, io.EOF) || errors.Is(e, io.ErrUnexpectedEOF) || errors.Is(e, net.ErrClosed)
}
