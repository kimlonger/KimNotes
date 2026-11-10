package main

import (
	"errors"
	"net/url"
	"os"
	"regexp"
	"strings"
)

type Config struct {
	DatabaseURL, Address, PublicOrigin, GatewaySecret    string
	SMTPHost, SMTPPort, SMTPUser, SMTPPassword, MailFrom string
	RequireVerification, Development                     bool
	MainProjectIDs                                       []string
}

func loadModeConfig(mode string) (Config, error) {
	if mode != "maintenance" && mode != "migrate" && mode != "migration-status" && mode != "content-import" && mode != "guitar-import" && mode != "guitar-review" {
		return loadConfig()
	}
	// Database tooling never exposes the application handler or sends email.
	// It should not require the API's gateway/SMTP credentials to migrate schema.
	c := Config{DatabaseURL: os.Getenv("DATABASE_URL"), Address: os.Getenv("LISTEN_ADDR")}
	if c.Address == "" {
		c.Address = ":8080"
	}
	if c.DatabaseURL == "" {
		return c, errors.New("DATABASE_URL required")
	}
	return c, nil
}

func loadConfig() (Config, error) {
	c := Config{DatabaseURL: os.Getenv("DATABASE_URL"), Address: os.Getenv("LISTEN_ADDR"), PublicOrigin: os.Getenv("PUBLIC_ORIGIN"), GatewaySecret: os.Getenv("GATEWAY_SECRET"), SMTPHost: os.Getenv("SMTP_HOST"), SMTPPort: os.Getenv("SMTP_PORT"), SMTPUser: os.Getenv("SMTP_USER"), SMTPPassword: os.Getenv("SMTP_PASSWORD"), MailFrom: os.Getenv("MAIL_FROM"), RequireVerification: os.Getenv("REQUIRE_EMAIL_VERIFICATION") != "false", Development: os.Getenv("APP_ENV") == "development"}
	if c.Address == "" {
		c.Address = ":8080"
	}
	if c.PublicOrigin == "" {
		c.PublicOrigin = "https://note.kimlulu.com"
	}
	if c.SMTPPort == "" {
		c.SMTPPort = "587"
	}
	u, err := url.Parse(c.PublicOrigin)
	if err != nil || u.Host == "" || u.Path != "" || (!c.Development && u.Scheme != "https") {
		return c, errors.New("PUBLIC_ORIGIN must be an HTTPS origin")
	}
	c.PublicOrigin = strings.TrimRight(c.PublicOrigin, "/")
	if c.MainProjectIDs, err = mainProjectIDs(os.Getenv("MAIN_PROJECT_IDS")); err != nil {
		return c, err
	}
	if c.DatabaseURL == "" {
		return c, errors.New("DATABASE_URL required")
	}
	if !c.Development && len(c.GatewaySecret) < 32 {
		return c, errors.New("GATEWAY_SECRET must contain at least 32 bytes")
	}
	if c.RequireVerification && !c.mailReady() {
		return c, errors.New("email verification requires SMTP_HOST and MAIL_FROM")
	}
	return c, nil
}
func (c Config) mailReady() bool { return c.SMTPHost != "" && c.MailFrom != "" }

var projectIDPattern = regexp.MustCompile(`^[a-z][a-z0-9-]{1,47}$`)

func mainProjectIDs(value string) ([]string, error) {
	if value == "" {
		return nil, nil
	}
	ids, seen := []string{}, map[string]bool{}
	for _, part := range strings.Split(value, ",") {
		id := strings.TrimSpace(part)
		if !projectIDPattern.MatchString(id) || seen[id] || len(ids) >= 64 {
			return nil, errors.New("MAIN_PROJECT_IDS requires 1–64 distinct lowercase project IDs")
		}
		seen[id] = true
		ids = append(ids, id)
	}
	return ids, nil
}
