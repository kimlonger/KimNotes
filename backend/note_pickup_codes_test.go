package main

import (
	"context"
	"crypto/ecdh"
	"crypto/rand"
	"encoding/base64"
	"net/http"
	"strings"
	"sync"
	"testing"
)

func notePickupTestKey(t *testing.T) string {
	t.Helper()
	key, err := ecdh.P256().GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	return base64.RawURLEncoding.EncodeToString(key.PublicKey().Bytes()[1:])
}

func TestNotePickupCodeAndKeyValidation(t *testing.T) {
	if len(notePickupAlphabet) != 32 || normalizeNotePickupCode(" 7km4-r9tx ") != "7KM4R9TX" {
		t.Fatal("unexpected alphabet or code normalization")
	}
	for _, value := range []string{"", "SHORT", "7KM4-R9T0", "7KM4-R9TI", "7K--R9TX", "7 KM4R9TX"} {
		if normalizeNotePickupCode(value) != "" {
			t.Errorf("invalid code accepted: %q", value)
		}
	}
	value := notePickupTestKey(t)
	if key, err := decodeNotePickupPublicKey(value); err != nil || len(key) != 64 {
		t.Fatalf("valid P-256 point: %v", err)
	}
	for _, value := range []string{"", value + "=", strings.Repeat("A", 86), strings.Repeat("_", 86)} {
		if _, err := decodeNotePickupPublicKey(value); err == nil {
			t.Error("invalid P-256 point or encoding accepted")
		}
	}
	app := &App{notePickupSecret: "test-only"}
	code := app.notePickupDisplayCode("offer", "session", 0)
	if normalizeNotePickupCode(code) != code || code != app.notePickupDisplayCode("offer", "session", 0) {
		t.Fatal("display code did not round-trip across create retries")
	}
}

func TestNotePickupOneTimeClaimAndSessionScope(t *testing.T) {
	e := newTestEnv(t)
	ownerWeb := e.account("pickup-owner")
	_ = e.account("pickup-other")
	owner := e.desktop("pickup-owner", true)["token"].(string)
	sameAccount := e.desktop("pickup-owner", true)["token"].(string)
	otherAccount := e.desktop("pickup-other", true)["token"].(string)
	const base = "kimnotes/transfers/pickup"
	request := func(method, path, token string, body any) (int, map[string]any) {
		t.Helper()
		status, result, response := e.sessionRequest(method, path, body, nil, token, nil)
		if response != nil && response.Header.Get("Cache-Control") != "private, no-store" {
			t.Error("pickup must not be cacheable")
		}
		return status, result
	}
	codes := func(status int, result map[string]any) (string, string) {
		t.Helper()
		id, _ := result["id"].(string)
		code, _ := result["code"].(string)
		if status != http.StatusOK || !validID(id) || code == "" || normalizeNotePickupCode(code) != code || result["expiresIn"] != float64(300) {
			t.Fatalf("unexpected offer response: %d %#v", status, result)
		}
		return id, code
	}
	key := notePickupTestKey(t)
	create := notePickupPairing{ID: newID(), PublicKey: key}
	if status, _, _ := e.request(http.MethodPost, base, create, ownerWeb); status != http.StatusForbidden {
		t.Fatalf("web session used desktop pickup: %d", status)
	}
	if status, _ := request(http.MethodPost, base, "", create); status != http.StatusUnauthorized {
		t.Fatalf("anonymous pickup: %d", status)
	}
	id, code := codes(request(http.MethodPost, base, owner, create))
	path := base + "/" + id
	if status, out := request(http.MethodPost, base, owner, create); status != http.StatusOK || out["code"] != code {
		t.Fatalf("create retry: %d %#v", status, out)
	}
	if status, _ := request(http.MethodPost, base, owner, notePickupPairing{ID: newID(), PublicKey: key}); status != http.StatusConflict {
		t.Fatalf("desktop created two pending pickups: %d", status)
	}
	if status, out := request(http.MethodGet, path, owner, nil); status != http.StatusOK || len(out) != 0 {
		t.Fatalf("pending poll: %d %#v", status, out)
	}
	if status, _ := request(http.MethodGet, path, sameAccount, nil); status != http.StatusNotFound {
		t.Fatalf("same account could poll another session's offer: %d", status)
	}
	claim := func(token, code, publicKey string) (int, map[string]any) {
		return request(http.MethodPost, base+"/claim", token, map[string]string{"code": code, "publicKey": publicKey})
	}
	first, second := notePickupTestKey(t), notePickupTestKey(t)
	if status, _ := claim(owner, code, first); status != http.StatusNotFound {
		t.Fatalf("sending session could redeem its own code: %d", status)
	}
	for _, token := range []string{sameAccount, otherAccount} {
		if status, _ := request(http.MethodDelete, path, token, nil); status != http.StatusOK {
			t.Fatalf("foreign cancellation leaked ownership: %d", status)
		}
	}

	tokens, keys := []string{sameAccount, otherAccount}, []string{first, second}
	statuses := make([]int, len(tokens))
	bodies := make([]map[string]any, len(tokens))
	var group sync.WaitGroup
	for i, token := range tokens {
		group.Add(1)
		go func() {
			defer group.Done()
			statuses[i], bodies[i] = claim(token, strings.ToLower(code[:4]+"-"+code[4:]), keys[i])
		}()
	}
	group.Wait()
	winner := 0
	if statuses[0] != http.StatusOK {
		winner = 1
	}
	if statuses[winner] != http.StatusOK || statuses[1-winner] != http.StatusNotFound ||
		bodies[winner]["id"] != id || bodies[winner]["publicKey"] != key {
		t.Fatalf("expected exactly one successful receiver: %v %#v", statuses, bodies)
	}
	if status, out := request(http.MethodGet, path, owner, nil); status != http.StatusOK || out["publicKey"] != keys[winner] {
		t.Fatalf("sender got wrong receiver key: %d %#v", status, out)
	}
	if status, out := claim(tokens[winner], code, keys[winner]); status != http.StatusOK || out["id"] != id || out["publicKey"] != key {
		t.Fatalf("winner retry: %d %#v", status, out)
	}
	if status, _ := claim(tokens[winner], code, keys[1-winner]); status != http.StatusNotFound {
		t.Fatalf("winner replaced the matched key: %d", status)
	}
	if status, _ := request(http.MethodDelete, path, owner, nil); status != http.StatusOK {
		t.Fatalf("sender could not cancel: %d", status)
	}
	if status, _ := claim(tokens[winner], code, keys[winner]); status != http.StatusNotFound {
		t.Fatalf("canceled code remained usable: %d", status)
	}
	if status, _ := request(http.MethodPost, base, owner, create); status != http.StatusNotFound {
		t.Fatalf("create retry resurrected a canceled offer: %d", status)
	}

	secondID, secondCode := codes(request(http.MethodPost, base, owner, notePickupPairing{ID: newID(), PublicKey: key}))
	if status, _ := claim(tokens[1-winner], secondCode, keys[1-winner]); status != http.StatusOK {
		t.Fatalf("distinct logged-in device could not claim new code: %d", status)
	}
	if status, _ := request(http.MethodGet, base+"/"+secondID, owner, nil); status != http.StatusOK {
		t.Fatalf("new match could not be polled: %d", status)
	}
}

func TestNotePickupExpiryRateLimitAndCleanup(t *testing.T) {
	e := newTestEnv(t)
	_ = e.account("pickup-expiry")
	_ = e.account("pickup-limit")
	owner := e.desktop("pickup-expiry", true)
	receiver := e.desktop("pickup-limit", true)
	const base = "kimnotes/transfers/pickup"
	req := func(method, path string, session map[string]any, body any) (int, map[string]any) {
		t.Helper()
		status, result, _ := e.sessionRequest(method, path, body, nil, session["token"].(string), nil)
		return status, result
	}
	id := newID()
	status, result := req(http.MethodPost, base, owner, notePickupPairing{ID: id, PublicKey: notePickupTestKey(t)})
	if status != http.StatusOK {
		t.Fatalf("generate code: %d %#v", status, result)
	}
	claim := map[string]string{"code": result["code"].(string), "publicKey": notePickupTestKey(t)}
	e.sql("UPDATE note_pickup_codes SET expires_at=now()-interval '1 second' WHERE id=$1", id)
	if status, _ := req(http.MethodPost, base+"/claim", receiver, claim); status != http.StatusNotFound {
		t.Fatalf("expired code was redeemed: %d", status)
	}
	if status, _ := req(http.MethodGet, base+"/"+id, owner, nil); status != http.StatusNotFound {
		t.Fatalf("expired code could still be polled: %d", status)
	}
	receiverID := receiver["user"].(map[string]any)["id"].(string)
	e.sql("UPDATE rate_limits SET count=12 WHERE key=$1", digest("kimnotes:pickup:claim:account:"+receiverID))
	if status, _ := req(http.MethodPost, base+"/claim", receiver, claim); status != http.StatusTooManyRequests {
		t.Fatalf("account claim limit was bypassed: %d", status)
	}
	ctx := context.Background()
	if err := e.app.cleanupSessionCaches(ctx); err != nil {
		t.Fatal(err)
	}
	var remaining int
	if err := e.db.QueryRow(ctx, "SELECT count(*) FROM note_pickup_codes").Scan(&remaining); err != nil || remaining != 0 {
		t.Fatalf("expired pairing metadata was retained: %d %v", remaining, err)
	}
}
