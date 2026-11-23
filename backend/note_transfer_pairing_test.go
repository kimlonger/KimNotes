package main

import (
	"bytes"
	"context"
	"encoding/base64"
	"errors"
	"net/http"
	"sync"
	"testing"
	"time"
)

func noteTransferTestCode(value byte) string {
	return base64.RawURLEncoding.EncodeToString(bytes.Repeat([]byte{value}, 32))
}

func TestNoteTransferDirectoryAccessAndCompetingOffers(t *testing.T) {
	e := newTestEnv(t)
	web := e.account("transfer-owner")
	_ = e.account("transfer-stranger")
	receiver := e.desktop("transfer-owner", true)["token"].(string)
	senderA := e.desktop("transfer-owner", true)["token"].(string)
	senderB := e.desktop("transfer-owner", true)["token"].(string)
	stranger := e.desktop("transfer-stranger", true)["token"].(string)
	const path = "kimnotes/transfers/receivers"
	req := func(method, endpoint string, body any, token string) (int, map[string]any) {
		t.Helper()
		status, out, res := e.sessionRequest(method, endpoint, body, nil, token, nil)
		if res != nil && res.Header.Get("Cache-Control") != "private, no-store" {
			t.Errorf("pairing response is cacheable: %q", res.Header.Get("Cache-Control"))
		}
		return status, out
	}
	if status, _, _ := e.request(http.MethodGet, path, nil, web); status != http.StatusForbidden {
		t.Fatal("web session gained access to desktop pairing")
	}
	if status, _ := req(http.MethodGet, path, nil, ""); status != http.StatusUnauthorized {
		t.Fatal("anonymous caller gained access to desktop pairing")
	}
	if status, _ := req(http.MethodPost, path, map[string]string{"file": "payload"}, receiver); status != http.StatusBadRequest {
		t.Fatal("unexpected payload was accepted")
	}
	status, created := req(http.MethodPost, path, struct{}{}, receiver)
	id, _ := created["id"].(string)
	if status != 200 || !validID(id) || created["expiresIn"] != float64(120) {
		t.Fatalf("registration: %d %#v", status, created)
	}
	if status, retry := req(http.MethodPost, path, struct{}{}, receiver); status != 200 || retry["id"] != id {
		t.Fatal("repeated registration returned a different receiver")
	}
	for _, token := range []string{receiver, stranger} {
		if status, list := req(http.MethodGet, path, nil, token); status != 200 || len(list["receivers"].([]any)) != 0 {
			t.Fatalf("directory revealed the caller or another account's device: %d %#v", status, list)
		}
	}
	status, list := req(http.MethodGet, path, nil, senderA)
	if status != 200 || len(list["receivers"].([]any)) != 1 {
		t.Fatalf("missing same-account device: %d %#v", status, list)
	}
	brief := list["receivers"].([]any)[0].(map[string]any)
	if brief["id"] != id || brief["name"] != "Test PC" || len(brief) != 2 {
		t.Fatalf("unexpected or sensitive receiver data: %#v", brief)
	}
	offer := path + "/" + id + "/offer"
	codes := []string{noteTransferTestCode(1), noteTransferTestCode(2)}
	postOffer := func(token, code string) (int, map[string]any) {
		return req(http.MethodPost, offer, map[string]string{"code": code}, token)
	}
	for _, token := range []string{receiver, stranger} {
		if status, _ := postOffer(token, codes[0]); status != http.StatusNotFound {
			t.Fatal("self or unrelated device offered a transfer")
		}
	}
	nonCanonical := noteTransferTestCode(0)
	nonCanonical = nonCanonical[:len(nonCanonical)-1] + "B"
	for _, code := range []string{"", "invalid", nonCanonical, codes[0] + "="} {
		if status, _ := postOffer(senderA, code); status != http.StatusBadRequest {
			t.Fatalf("malformed code accepted: %q", code)
		}
	}
	if status, _ := req(http.MethodDelete, path+"/"+id, nil, stranger); status != 200 {
		t.Fatal("foreign receiver close must behave like an unknown registration")
	}

	// The receiver may be claimed by exactly one simultaneous sender.
	senders := []string{senderA, senderB}
	statuses := make([]int, len(senders))
	var wg sync.WaitGroup
	for i, token := range senders {
		wg.Add(1)
		go func() {
			defer wg.Done()
			statuses[i], _ = postOffer(token, codes[i])
		}()
	}
	wg.Wait()
	winner := 0
	if statuses[0] == http.StatusConflict {
		winner = 1
	}
	if statuses[winner] != http.StatusOK || statuses[1-winner] != http.StatusConflict {
		t.Fatalf("expected one successful offer and one conflict: %v", statuses)
	}
	if status, _ := postOffer(senders[winner], codes[winner]); status != 200 {
		t.Fatal("retrying the successful offer must be idempotent")
	}
	if status, _ := postOffer(senders[winner], codes[1-winner]); status != http.StatusConflict {
		t.Fatal("a new code must not replace the first successful offer")
	}
	if status, list := req(http.MethodGet, path, nil, senderA); status != 200 || len(list["receivers"].([]any)) != 0 {
		t.Fatalf("directory listed a device with an active offer: %d %#v", status, list)
	}
	for _, token := range []string{stranger, senderA, senderB} {
		if status, _ := req(http.MethodGet, offer, nil, token); status != http.StatusNotFound {
			t.Fatal("pairing code was readable by a session other than the receiver")
		}
	}
	if status, out := req(http.MethodGet, offer, nil, receiver); status != 200 || out["code"] != codes[winner] {
		t.Fatalf("receiver offer: %d %#v", status, out)
	}
	for range 2 {
		if status, _ := req(http.MethodDelete, path+"/"+id, nil, receiver); status != 200 {
			t.Fatal("closing the receiver should be idempotent")
		}
	}
	if status, _ := postOffer(senderA, codes[0]); status != http.StatusNotFound {
		t.Fatal("closed device still accepted an offer")
	}
}

func TestNoteTransferReceiverLifetime(t *testing.T) {
	var store noteTransferDirectory
	now := time.Now()
	receiver := principal{UserID: "one", SessionID: "receiver"}
	sender := principal{UserID: "one", SessionID: "sender"}
	id, _, err := store.register(receiver, now)
	if err != nil {
		t.Fatal(err)
	}
	reused, seconds, err := store.register(receiver, now.Add(time.Minute))
	if reused != id || seconds != 60 || err != nil {
		t.Fatalf("registration retry extended the lifetime: %s %d %v", reused, seconds, err)
	}
	if items := store.list(sender, now.Add(noteTransferReceiverLifetime)); len(items) != 0 {
		t.Fatalf("expired receiver listed: %#v", items)
	}
	var failure *apiError
	if err = store.offer(sender, id, noteTransferTestCode(1), now.Add(noteTransferReceiverLifetime)); !errors.As(err, &failure) || failure.Status != http.StatusNotFound {
		t.Fatalf("expired receiver accepted an offer: %v", err)
	}
	renewed, seconds, err := store.register(receiver, now.Add(noteTransferReceiverLifetime))
	if renewed == id || seconds != 120 || err != nil {
		t.Fatalf("new registration after expiration: %s %d %v", renewed, seconds, err)
	}
}

func TestNoteTransferWaitCancellationAndNotification(t *testing.T) {
	var store noteTransferDirectory
	receiver := principal{UserID: "one", SessionID: "receiver"}
	sender := principal{UserID: "one", SessionID: "sender"}
	id, _, err := store.register(receiver, time.Now())
	if err != nil {
		t.Fatal(err)
	}
	wait := func(ctx context.Context) <-chan string {
		t.Helper()
		out := make(chan string, 1)
		go func() {
			code, err := store.waitForOffer(ctx, receiver, id)
			if err != nil {
				t.Errorf("wait failed: %v", err)
			}
			out <- code
		}()
		deadline := time.Now().Add(time.Second)
		for time.Now().Before(deadline) {
			store.mu.Lock()
			waiting := store.receivers[id].waiting
			store.mu.Unlock()
			if waiting {
				return out
			}
			time.Sleep(time.Millisecond)
		}
		t.Fatal("offer wait did not begin within a second")
		return nil
	}
	expect := func(result <-chan string, want string) {
		t.Helper()
		select {
		case code := <-result:
			if code != want {
				t.Fatalf("offer code: got %q, want %q", code, want)
			}
		case <-time.After(time.Second):
			t.Fatal("offer waiter was not woken")
		}
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	first := wait(ctx)
	cancel()
	expect(first, "")
	second := wait(context.Background())
	if err := store.offer(sender, id, noteTransferTestCode(3), time.Now()); err != nil {
		t.Fatal(err)
	}
	expect(second, noteTransferTestCode(3))
	store.close(receiver, id, time.Now())

	id, _, err = store.register(receiver, time.Now())
	if err != nil {
		t.Fatal(err)
	}
	closed := wait(context.Background())
	store.close(receiver, id, time.Now())
	expect(closed, "")
}
