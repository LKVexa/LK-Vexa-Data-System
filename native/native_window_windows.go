package main

import (
	"errors"
	"fmt"
	"os/exec"
	"syscall"
	"time"
	"unsafe"
)

// Request the ordinary close path only for windows owned by the child we started.
// This exercises WPF's existing save/acknowledgement handler; it never terminates
// an unrelated process or suppresses a failed persistence acknowledgement.
func requestOwnedWindowClose(pid int) error {
	if pid <= 0 {
		return errors.New("invalid owned presenter process")
	}
	u := syscall.NewLazyDLL("user32.dll")
	getPID := u.NewProc("GetWindowThreadProcessId")
	post := u.NewProc("PostMessageW")
	visible := u.NewProc("IsWindowVisible")
	found := false
	callback := syscall.NewCallback(func(hwnd, _ uintptr) uintptr {
		var owner uint32
		getPID.Call(hwnd, uintptr(unsafe.Pointer(&owner)))
		shown, _, _ := visible.Call(hwnd)
		if int(owner) == pid && shown != 0 {
			if result, _, _ := post.Call(hwnd, 0x0010, 0, 0); result != 0 {
				found = true
			}
		}
		return 1
	})
	result, _, e := u.NewProc("EnumWindows").Call(callback, 0)
	if result == 0 {
		return fmt.Errorf("enumerate presenter windows: %v", e)
	}
	if !found {
		return errors.New("owned native presenter window was not found")
	}
	return nil
}

func closeQualificationWindow(b *Broker, child *exec.Cmd, done <-chan struct{}) error {
	if child == nil || child.Process == nil {
		return errors.New("native qualification presenter was not started")
	}
	deadline := time.Now().Add(15 * time.Second)
	select {
	case <-done:
	default:
		if e := requestOwnedWindowClose(child.Process.Pid); e != nil {
			return e
		}
	}
	select {
	case <-done:
		if child.ProcessState == nil || !child.ProcessState.Success() {
			return errors.New("qualification presenter did not exit successfully")
		}
		if !waitPresenterAcknowledgement(b, time.Until(deadline)) {
			return errors.New("qualification presenter closed without its persistence acknowledgement")
		}
		return nil
	case <-time.After(time.Until(deadline)):
		return errors.New("qualification presenter did not close gracefully within 15 seconds")
	}
}

// A child process can finish before the socket reader consumes its final
// persistence acknowledgement. Allow both events to arrive within the budget.
func waitPresenterAcknowledgement(b *Broker, timeout time.Duration) bool {
	b.mu.Lock()
	clean, stopped := b.clean, b.stopped
	b.mu.Unlock()
	if clean {
		return true
	}
	select {
	case <-stopped:
		return b.Clean()
	case <-time.After(timeout):
		return false
	}
}
