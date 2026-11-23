package main

import (
	"context"
	"encoding/base64"
	"net/http"
	"slices"
	"strings"
	"sync"
	"time"
)

const (
	noteTransferReceiverLifetime = 2 * time.Minute
	noteTransferOfferWait        = 18 * time.Second
	maxNoteTransferReceivers     = 4096
	maxNoteTransferRequestBytes  = 512
)

type noteTransferDirectory struct {
	mu        sync.Mutex
	receivers map[string]*noteTransferReceiver
	nextSweep time.Time
}

type noteTransferReceiver struct {
	id, user, session, name string
	expires                 time.Time
	sender, code            string
	ready                   chan struct{}
	waiting                 bool
}

type noteTransferReceiverBrief struct {
	ID   string `json:"id"`
	Name string `json:"name"`
}

func noteTransferDesktop(fn func(http.ResponseWriter, *http.Request) error) func(http.ResponseWriter, *http.Request) error {
	return func(w http.ResponseWriter, r *http.Request) error {
		p := identity(r)
		if p.AppID != "kimnotes" {
			return fail(http.StatusNotFound, "not_found", "入口不存在。")
		}
		if p.ClientKind != "desktop" {
			return fail(http.StatusForbidden, "desktop_required", "请通过桌面便签使用文件互传。")
		}
		return fn(w, r)
	}
}

func (s *noteTransferDirectory) remove(receiver *noteTransferReceiver) {
	delete(s.receivers, receiver.id)
	if receiver.code == "" {
		close(receiver.ready)
	}
}

func (s *noteTransferDirectory) purge(now time.Time) {
	if now.Before(s.nextSweep) {
		return
	}
	s.nextSweep = now.Add(time.Second)
	for _, receiver := range s.receivers {
		if !now.Before(receiver.expires) {
			s.remove(receiver)
		}
	}
}

func (s *noteTransferDirectory) register(p principal, now time.Time) (id string, expiresIn int, err error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.purge(now)
	for _, receiver := range s.receivers {
		if receiver.session == p.SessionID {
			if now.Before(receiver.expires) {
				return receiver.id, int((receiver.expires.Sub(now) + time.Second - 1) / time.Second), nil
			}
			s.remove(receiver)
			break
		}
	}
	if len(s.receivers) >= maxNoteTransferReceivers {
		return "", 0, fail(http.StatusServiceUnavailable, "receiver_capacity", "正在等待的设备过多，请稍后重试。")
	}
	name := strings.TrimSpace(p.DeviceName)
	if name == "" {
		name = "Windows 便签"
	}
	receiver := &noteTransferReceiver{
		id: newID(), user: p.UserID, session: p.SessionID, name: name,
		expires: now.Add(noteTransferReceiverLifetime), ready: make(chan struct{}),
	}
	if s.receivers == nil {
		s.receivers = make(map[string]*noteTransferReceiver)
	}
	s.receivers[receiver.id] = receiver
	return receiver.id, int(noteTransferReceiverLifetime / time.Second), nil
}

func (s *noteTransferDirectory) list(p principal, now time.Time) []noteTransferReceiverBrief {
	s.mu.Lock()
	s.purge(now)
	items := []noteTransferReceiverBrief{}
	for _, receiver := range s.receivers {
		if receiver.user == p.UserID && receiver.session != p.SessionID && receiver.code == "" && now.Before(receiver.expires) {
			items = append(items, noteTransferReceiverBrief{ID: receiver.id, Name: receiver.name})
		}
	}
	s.mu.Unlock()
	slices.SortFunc(items, func(a, b noteTransferReceiverBrief) int {
		if a.Name == b.Name {
			return strings.Compare(a.ID, b.ID)
		}
		return strings.Compare(a.Name, b.Name)
	})
	return items
}

func (s *noteTransferDirectory) offer(p principal, id, code string, now time.Time) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.purge(now)
	receiver := s.receivers[id]
	if receiver == nil || receiver.user != p.UserID || receiver.session == p.SessionID || !now.Before(receiver.expires) {
		return fail(http.StatusNotFound, "receiver_not_found", "对方设备已离开，或尚未准备接收。")
	}
	if receiver.code != "" {
		if receiver.sender == p.SessionID && receiver.code == code {
			return nil
		}
		return fail(http.StatusConflict, "receiver_busy", "对方设备正在建立其他传输。")
	}
	receiver.sender, receiver.code = p.SessionID, code
	close(receiver.ready)
	return nil
}

func (s *noteTransferDirectory) waitForOffer(ctx context.Context, p principal, id string) (string, error) {
	s.mu.Lock()
	now := time.Now()
	s.purge(now)
	receiver := s.receivers[id]
	if receiver == nil || receiver.user != p.UserID || receiver.session != p.SessionID || !now.Before(receiver.expires) {
		s.mu.Unlock()
		return "", fail(http.StatusNotFound, "receiver_not_found", "接收已结束，请重新打开文件互传。")
	}
	if receiver.code != "" {
		code := receiver.code
		s.mu.Unlock()
		return code, nil
	}
	if receiver.waiting {
		s.mu.Unlock()
		return "", fail(http.StatusConflict, "receiver_already_waiting", "此设备已在等待传输。")
	}
	receiver.waiting = true
	deadline := min(noteTransferOfferWait, receiver.expires.Sub(now))
	s.mu.Unlock()

	timer := time.NewTimer(deadline)
	defer timer.Stop()
	select {
	case <-receiver.ready:
	case <-timer.C:
	case <-ctx.Done():
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	receiver.waiting = false
	s.purge(time.Now())
	if ctx.Err() != nil || s.receivers[id] != receiver || !time.Now().Before(receiver.expires) {
		return "", nil
	}
	return receiver.code, nil
}

func (s *noteTransferDirectory) close(p principal, id string, now time.Time) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.purge(now)
	if receiver := s.receivers[id]; receiver != nil && receiver.user == p.UserID && receiver.session == p.SessionID {
		s.remove(receiver)
	}
}

func validNoteTransferCode(code string) bool {
	if len(code) != base64.RawURLEncoding.EncodedLen(32) {
		return false
	}
	bytes, err := base64.RawURLEncoding.Strict().DecodeString(code)
	return err == nil && len(bytes) == 32
}

func (a *App) registerNoteTransferReceiver(w http.ResponseWriter, r *http.Request) error {
	r.Body = http.MaxBytesReader(w, r.Body, maxNoteTransferRequestBytes)
	var in *struct{}
	if err := decode(w, r, &in); err != nil {
		return err
	}
	if in == nil {
		return fail(http.StatusBadRequest, "invalid_json", "请求需要一个 JSON 对象。")
	}
	id, expiresIn, err := a.noteTransfers.register(identity(r), time.Now())
	if err != nil {
		return err
	}
	writeJSON(w, http.StatusOK, map[string]any{"id": id, "expiresIn": expiresIn})
	return nil
}

func (a *App) listNoteTransferReceivers(w http.ResponseWriter, r *http.Request) error {
	writeJSON(w, http.StatusOK, map[string]any{"receivers": a.noteTransfers.list(identity(r), time.Now())})
	return nil
}

func (a *App) createNoteTransferOffer(w http.ResponseWriter, r *http.Request) error {
	r.Body = http.MaxBytesReader(w, r.Body, maxNoteTransferRequestBytes)
	var in struct {
		Code string `json:"code"`
	}
	if err := decode(w, r, &in); err != nil {
		return err
	}
	if !validNoteTransferCode(in.Code) {
		return fail(http.StatusBadRequest, "invalid_transfer_code", "文件互传配对信息无效。")
	}
	if err := a.noteTransfers.offer(identity(r), r.PathValue("receiver"), in.Code, time.Now()); err != nil {
		return err
	}
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	return nil
}

func (a *App) getNoteTransferOffer(w http.ResponseWriter, r *http.Request) error {
	code, err := a.noteTransfers.waitForOffer(r.Context(), identity(r), r.PathValue("receiver"))
	if err != nil {
		return err
	}
	out := struct {
		Code string `json:"code,omitempty"`
	}{Code: code}
	writeJSON(w, http.StatusOK, out)
	return nil
}

func (a *App) closeNoteTransferReceiver(w http.ResponseWriter, r *http.Request) error {
	a.noteTransfers.close(identity(r), r.PathValue("receiver"), time.Now())
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	return nil
}
