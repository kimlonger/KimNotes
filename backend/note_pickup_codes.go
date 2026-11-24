package main

import (
	"bytes"
	"crypto/ecdh"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"errors"
	"net"
	"net/http"
	"net/netip"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
)

const (
	notePickupAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"
	notePickupLifetime = 5 * time.Minute
	notePickupAttempts = 8
)

type notePickupPairing struct {
	ID        string `json:"id,omitempty"`
	PublicKey string `json:"publicKey,omitempty"`
}

func normalizeNotePickupCode(code string) string {
	code = strings.ToUpper(strings.TrimSpace(code))
	if len(code) == 9 && code[4] == '-' {
		code = code[:4] + code[5:]
	}
	if len(code) != 8 {
		return ""
	}
	for _, character := range code {
		if !strings.ContainsRune(notePickupAlphabet, character) {
			return ""
		}
	}
	return code
}

func decodeNotePickupPublicKey(value string) ([]byte, error) {
	const publicKeyBytes = 64
	invalid := fail(http.StatusBadRequest, "invalid_pickup_key", "取件码配对信息无效。")
	if len(value) != base64.RawURLEncoding.EncodedLen(publicKeyBytes) {
		return nil, invalid
	}
	key, err := base64.RawURLEncoding.Strict().DecodeString(value)
	if err != nil || len(key) != publicKeyBytes {
		return nil, invalid
	}
	if _, err = ecdh.P256().NewPublicKey(append([]byte{4}, key...)); err != nil {
		return nil, invalid
	}
	return key, nil
}

func (a *App) notePickupMAC(domain, value string) []byte {
	mac := hmac.New(sha256.New, []byte(a.notePickupSecret))
	mac.Write([]byte(domain))
	mac.Write([]byte{0})
	mac.Write([]byte(value))
	return mac.Sum(nil)
}

// Derive the display code again on a create retry. The database only stores a
// keyed lookup digest, so reading it alone does not reveal usable short codes.
func (a *App) notePickupDisplayCode(id, session string, attempt int) string {
	random := a.notePickupMAC("kimnotes pickup code v1", id+"\x00"+session+"\x00"+string(rune(attempt)))
	var code [8]byte
	for i := range code {
		code[i] = notePickupAlphabet[int(random[i])&31]
	}
	return string(code[:])
}

func (a *App) notePickupCodeKey(code string) []byte {
	return a.notePickupMAC("kimnotes pickup lookup v1", code)
}

func unavailableNotePickup() error {
	return fail(http.StatusNotFound, "pickup_unavailable", "取件码无效、已使用或已过期。")
}

func (a *App) limitNotePickup(r *http.Request, action string, accountLimit, networkLimit int) error {
	clientIP := r.Header.Get("X-Kim-Client-IP")
	if clientIP == "" {
		clientIP, _, _ = net.SplitHostPort(r.RemoteAddr)
	}
	if address, err := netip.ParseAddr(clientIP); err == nil {
		address = address.Unmap()
		clientIP = address.String()
		if address.Is6() {
			clientIP = netip.PrefixFrom(address, 64).Masked().String()
		}
	} else {
		clientIP = "unknown"
	}
	bucket := "kimnotes:pickup:" + action
	if err := a.limited(r, bucket+":ip:"+clientIP, networkLimit, notePickupLifetime); err != nil {
		return err
	}
	return a.limited(r, bucket+":account:"+identity(r).UserID, accountLimit, notePickupLifetime)
}

func (a *App) createNotePickup(w http.ResponseWriter, r *http.Request) error {
	if err := a.limitNotePickup(r, "create", 12, 72); err != nil {
		return err
	}
	r.Body = http.MaxBytesReader(w, r.Body, maxNoteTransferRequestBytes)
	var in notePickupPairing
	if err := decode(w, r, &in); err != nil {
		return err
	}
	if !validID(in.ID) {
		return fail(http.StatusBadRequest, "invalid_pickup_id", "取件码请求无效。")
	}
	publicKey, err := decodeNotePickupPublicKey(in.PublicKey)
	if err != nil {
		return err
	}

	ctx := r.Context()
	session := identity(r).SessionID
	tx, err := a.db.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx)
	// Serialize pending-offer creation for one desktop session, including retries.
	var liveSession string
	err = tx.QueryRow(ctx, "SELECT id::text FROM sessions WHERE id=$1 AND revoked_at IS NULL AND expires_at>now() FOR NO KEY UPDATE", session).Scan(&liveSession)
	if errors.Is(err, pgx.ErrNoRows) {
		return unavailableNotePickup()
	}
	if err != nil {
		return err
	}

	var existingSession string
	var existingKey []byte
	var attempt, expiresIn int
	err = tx.QueryRow(ctx, `SELECT sender_session_id::text, sender_public_key, code_attempt,
		CASE WHEN canceled_at IS NULL THEN CEIL(EXTRACT(epoch FROM expires_at-now()))::integer ELSE 0 END
		FROM note_pickup_codes WHERE id=$1`, in.ID).Scan(&existingSession, &existingKey, &attempt, &expiresIn)
	if err == nil {
		if existingSession != session || !bytes.Equal(existingKey, publicKey) || expiresIn <= 0 {
			return unavailableNotePickup()
		}
	} else if errors.Is(err, pgx.ErrNoRows) {
		var hasPending bool
		if err = tx.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM note_pickup_codes
			WHERE sender_session_id=$1 AND expires_at>now() AND claimed_at IS NULL AND canceled_at IS NULL)`, session).Scan(&hasPending); err != nil {
			return err
		}
		if hasPending {
			return fail(http.StatusConflict, "pickup_busy", "此设备已有一个待接收的取件码。")
		}
		for attempt = 0; attempt < notePickupAttempts; attempt++ {
			code := a.notePickupDisplayCode(in.ID, session, attempt)
			tag, err := tx.Exec(ctx, `INSERT INTO note_pickup_codes(id, code_key, code_attempt, sender_session_id, sender_public_key)
				VALUES($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING`, in.ID, a.notePickupCodeKey(code), attempt, session, publicKey)
			if err != nil {
				return err
			}
			if tag.RowsAffected() == 1 {
				break
			}
		}
		if attempt == notePickupAttempts {
			return fail(http.StatusServiceUnavailable, "pickup_capacity", "暂时无法生成取件码，请稍后重试。")
		}
		expiresIn = int(notePickupLifetime / time.Second)
	} else {
		return err
	}
	if err = tx.Commit(ctx); err != nil {
		return err
	}
	writeJSON(w, http.StatusOK, map[string]any{"id": in.ID, "code": a.notePickupDisplayCode(in.ID, session, attempt), "expiresIn": expiresIn})
	return nil
}

func (a *App) claimNotePickup(w http.ResponseWriter, r *http.Request) error {
	if err := a.limitNotePickup(r, "claim", 12, 60); err != nil {
		return err
	}
	r.Body = http.MaxBytesReader(w, r.Body, maxNoteTransferRequestBytes)
	var in struct {
		Code      string `json:"code"`
		PublicKey string `json:"publicKey"`
	}
	if err := decode(w, r, &in); err != nil {
		return err
	}
	code := normalizeNotePickupCode(in.Code)
	if code == "" {
		return fail(http.StatusBadRequest, "invalid_pickup_code", "请输入 8 位取件码。")
	}
	publicKey, err := decodeNotePickupPublicKey(in.PublicKey)
	if err != nil {
		return err
	}
	var pairing notePickupPairing
	var senderKey []byte
	// A retry from the winning session with the same key returns the same match.
	// PostgreSQL rechecks the row after competing claims, so another receiver
	// cannot replace the winner or extend the public code's original lifetime.
	err = a.db.QueryRow(r.Context(), `UPDATE note_pickup_codes AS pickup
		SET receiver_session_id=$2, receiver_public_key=$3, claimed_at=COALESCE(claimed_at,now())
		WHERE code_key=$1 AND sender_session_id<>$2 AND sender_public_key<>$3 AND canceled_at IS NULL
		  AND ((claimed_at IS NULL AND expires_at>now())
		       OR (receiver_session_id=$2 AND receiver_public_key=$3
		           AND GREATEST(expires_at,claimed_at+interval '1 minute')>now()))
		  AND EXISTS (SELECT 1 FROM sessions s JOIN users u ON u.id=s.user_id
		              WHERE s.id=pickup.sender_session_id AND s.revoked_at IS NULL
		              AND s.expires_at>now() AND s.auth_version=u.auth_version)
		RETURNING id::text, sender_public_key`, a.notePickupCodeKey(code), identity(r).SessionID, publicKey).Scan(&pairing.ID, &senderKey)
	if errors.Is(err, pgx.ErrNoRows) {
		return unavailableNotePickup()
	}
	if err != nil {
		return err
	}
	pairing.PublicKey = base64.RawURLEncoding.EncodeToString(senderKey)
	writeJSON(w, http.StatusOK, pairing)
	return nil
}

func (a *App) pollNotePickup(w http.ResponseWriter, r *http.Request) error {
	id := r.PathValue("pickup")
	if !validID(id) {
		return unavailableNotePickup()
	}
	var receiverKey []byte
	err := a.db.QueryRow(r.Context(), `SELECT receiver_public_key FROM note_pickup_codes
		WHERE id=$1 AND sender_session_id=$2 AND canceled_at IS NULL
		  AND GREATEST(expires_at,claimed_at+interval '1 minute')>now()`, id, identity(r).SessionID).Scan(&receiverKey)
	if errors.Is(err, pgx.ErrNoRows) {
		return unavailableNotePickup()
	}
	if err != nil {
		return err
	}
	writeJSON(w, http.StatusOK, notePickupPairing{PublicKey: base64.RawURLEncoding.EncodeToString(receiverKey)})
	return nil
}

func (a *App) cancelNotePickup(w http.ResponseWriter, r *http.Request) error {
	id := r.PathValue("pickup")
	if validID(id) {
		if _, err := a.db.Exec(r.Context(), `UPDATE note_pickup_codes SET canceled_at=now()
			WHERE id=$1 AND sender_session_id=$2 AND canceled_at IS NULL`, id, identity(r).SessionID); err != nil {
			return err
		}
	}
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	return nil
}
