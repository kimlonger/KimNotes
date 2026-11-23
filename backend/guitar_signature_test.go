package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"image"
	"image/png"
	"net/http"
	"strings"
	"sync"
	"testing"
)

func TestGuitarSignatureMigrationPreservesLegacyMember(t *testing.T) {
	e := guitarTestEnv(t)
	owner := guitarTestAccount(t, e, "signature-pre-migration")
	if _, err := e.db.Exec(context.Background(), "ALTER TABLE guitar_members DROP COLUMN signature"); err != nil {
		t.Fatal(err)
	}
	var imageBytes bytes.Buffer
	if err := png.Encode(&imageBytes, image.NewRGBA(image.Rect(0, 0, 4, 4))); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(context.Background(), `UPDATE guitar_members SET nickname='旧设备',avatar_mime='image/png',avatar_bytes=$1
 WHERE user_id=(SELECT id FROM users WHERE email=$2)`, imageBytes.Bytes(), "signature-pre-migration@example.com"); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(context.Background(), guitarSignatureSchema); err != nil {
		t.Fatal(err)
	}
	status, identity, _ := e.request("GET", "guitar/identity", nil, owner)
	if status != 200 {
		t.Fatalf("profile after migration: %d %v", status, identity)
	}
	if signature, exists := identity["signature"]; !exists || signature != nil {
		t.Fatalf("new capability should start unset: %v", identity)
	}
	status, identity, _ = e.request("PUT", "guitar/identity/signature", map[string]string{"signature": "练琴"}, owner)
	if status != 200 || identity["signature"] != "练琴" || identity["nickname"] != "旧设备" || identity["avatarUrl"] == "" {
		t.Fatalf("post-migration: %d %v", status, identity)
	}
}

func TestGuitarSignatureValidation(t *testing.T) {
	for _, tc := range []struct {
		input *string
		want  string
		valid bool
	}{
		{nil, "", false},
		{stringRef("  弹琴  "), "弹琴", true},
		{stringRef(""), "", true},
		{stringRef(strings.Repeat("🎸", 60)), strings.Repeat("🎸", 60), true},
		{stringRef(strings.Repeat("🎸", 61)), "", false},
		{stringRef("a\x00b"), "", false},
		{stringRef("a\nb"), "", false},
	} {
		got, err := validateGuitarSignature(tc.input)
		if (err == nil) != tc.valid || got != tc.want {
			t.Fatalf("validate(%v): got %q, %v", tc.input, got, err)
		}
	}
}

func stringRef(s string) *string { return &s }

func TestGuitarSignatureSharedAndLegacySafe(t *testing.T) {
	e := guitarTestEnv(t)
	owner := guitarTestAccount(t, e, "signature-owner")
	other := guitarTestAccount(t, e, "signature-other")
	path := "guitar/identity/signature"
	if status, _, _ := e.request("PUT", path, map[string]any{"signature": "x"}, nil); status != 401 {
		t.Fatalf("unauthenticated: %d", status)
	}
	status, identity, _ := e.request("GET", "guitar/identity", nil, owner)
	if status != 200 || identity["signature"] != nil {
		t.Fatalf("initial signature: %d %v", status, identity)
	}
	status, identity, _ = e.request("PUT", path, map[string]any{"signature": "  练琴  "}, owner)
	if status != 200 || identity["signature"] != "练琴" {
		t.Fatalf("save: %d %v", status, identity)
	}
	status, identity, _ = e.request("PUT", "guitar/identity", map[string]any{"nickname": "琴友甲", "avatar": "guitar"}, owner)
	if status != 200 || identity["signature"] != "练琴" {
		t.Fatalf("legacy identity update must preserve signature: %d %v", status, identity)
	}
	status, identity, _ = e.request("PUT", path, map[string]any{"signature": "旧设备", "migrate": true}, owner)
	if status != 200 || identity["signature"] != "练琴" {
		t.Fatalf("stale migration overwrote signature: %d %v", status, identity)
	}
	status, identity, _ = e.request("GET", "guitar/identity", nil, other)
	if status != 200 || identity["signature"] != nil {
		t.Fatalf("account isolation: %d %v", status, identity)
	}
	status, identity, _ = e.request("PUT", path, map[string]any{"signature": ""}, owner)
	if status != 200 || identity["signature"] != "" {
		t.Fatalf("clear: %d %v", status, identity)
	}
	status, identity, _ = e.request("PUT", path, map[string]any{"signature": "旧设备", "migrate": true}, owner)
	if status != 200 || identity["signature"] != "" {
		t.Fatalf("migration restored a cleared signature: %d %v", status, identity)
	}
	for _, body := range []map[string]any{{}, {"signature": nil}, {"signature": strings.Repeat("琴", 61)}, {"signature": "a\x00b"}} {
		if status, _, _ := e.request("PUT", path, body, owner); status != 400 {
			t.Fatalf("invalid %v returned %d", body, status)
		}
	}
}

func TestGuitarSignatureConcurrentMigration(t *testing.T) {
	e := guitarTestEnv(t)
	owner := guitarTestAccount(t, e, "signature-concurrent")
	start := make(chan struct{})
	results := make(chan error, 2)
	var wg sync.WaitGroup
	for _, value := range []string{"设备甲", "设备乙"} {
		wg.Add(1)
		go func(value string) {
			defer wg.Done()
			<-start
			body, _ := json.Marshal(map[string]any{"signature": value, "migrate": true})
			req, err := http.NewRequest("PUT", e.server.URL+"/api/v1/apps/guitar/identity/signature", bytes.NewReader(body))
			if err != nil {
				results <- err
				return
			}
			req.Header.Set("Origin", "https://notes.test")
			req.Header.Set("Content-Type", "application/json")
			req.AddCookie(owner)
			resp, err := http.DefaultClient.Do(req)
			if err != nil {
				results <- err
				return
			}
			defer resp.Body.Close()
			if resp.StatusCode != 200 {
				results <- fmt.Errorf("status %d", resp.StatusCode)
				return
			}
			results <- nil
		}(value)
	}
	close(start)
	wg.Wait()
	close(results)
	for err := range results {
		if err != nil {
			t.Fatal(err)
		}
	}
	status, identity, _ := e.request("GET", "guitar/identity", nil, owner)
	if status != 200 || (identity["signature"] != "设备甲" && identity["signature"] != "设备乙") {
		t.Fatalf("migration winner: %d %v", status, identity)
	}
	winner := identity["signature"]
	_, identity, _ = e.request("PUT", "guitar/identity/signature", map[string]any{"signature": "第三台设备", "migrate": true}, owner)
	if identity["signature"] != winner {
		t.Fatalf("later migration changed winner: %v", identity)
	}
}
