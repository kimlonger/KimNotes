package main

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestShanghaiGatewayOrigins(t *testing.T) {
	t.Setenv("DATABASE_URL", "postgres://unit-test")
	t.Setenv("GATEWAY_SECRET", strings.Repeat("x", 48))
	t.Setenv("REQUIRE_EMAIL_VERIFICATION", "false")
	t.Setenv("PUBLIC_ORIGIN", "https://note.kimlulu.com")
	t.Setenv("CANDIDATE_ALLOWED_ORIGINS", "https://note.kimlulu.com,https://guitar.kimlulu.com")
	t.Setenv("CANDIDATE_DISABLE_ACCOUNT_MUTATIONS", "false")

	config, err := loadConfig()
	if err != nil {
		t.Fatal(err)
	}
	handler := newApp(config, nil).routes()
	for _, test := range []struct {
		name   string
		origin string
		client string
		status int
	}{
		{"notes and the main site's adapter", "https://note.kimlulu.com", "", http.StatusNotFound},
		{"guitar web", "https://guitar.kimlulu.com", "", http.StatusNotFound},
		{"native clients", "", "desktop", http.StatusNotFound},
		{"missing browser origin", "", "", http.StatusForbidden},
		{"unconfigured origin", "https://www.kimlulu.com", "", http.StatusForbidden},
		{"different scheme", "http://guitar.kimlulu.com", "", http.StatusForbidden},
	} {
		t.Run(test.name, func(t *testing.T) {
			request := httptest.NewRequest(http.MethodPost, "/nonexistent", nil)
			request.Header.Set("Origin", test.origin)
			request.Header.Set("X-Kim-Client", test.client)
			request.Header.Set("X-Kim-Gateway", config.GatewaySecret)
			response := httptest.NewRecorder()
			handler.ServeHTTP(response, request)
			if response.Code != test.status {
				t.Fatalf("status = %d, want %d", response.Code, test.status)
			}
		})
	}

	t.Setenv("CANDIDATE_DISABLE_ACCOUNT_MUTATIONS", "true")
	config, err = loadConfig()
	if err != nil {
		t.Fatal(err)
	}
	request := httptest.NewRequest(http.MethodPost, "/api/v1/apps/guitar/auth/register", nil)
	request.Header.Set("Origin", "https://guitar.kimlulu.com")
	request.Header.Set("X-Kim-Gateway", config.GatewaySecret)
	response := httptest.NewRecorder()
	newApp(config, nil).routes().ServeHTTP(response, request)
	if response.Code != http.StatusForbidden || !strings.Contains(response.Body.String(), "candidate_account_operation_disabled") {
		t.Fatalf("candidate account operation response = %d %s", response.Code, response.Body.String())
	}

	t.Setenv("CANDIDATE_ALLOWED_ORIGINS", "https://example.invalid")
	if _, err := loadConfig(); err == nil {
		t.Fatal("expected an error for an unknown additional origin")
	}
}
