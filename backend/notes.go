package main

import (
	"encoding/json"
	"errors"
	"github.com/jackc/pgx/v5"
	"net/http"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"
)

type Document struct {
	Title       string     `json:"title"`
	Text        string     `json:"text"`
	RTF         string     `json:"rtf,omitempty"`
	RTFGzip     string     `json:"rtfGzip,omitempty"`
	RTFImages   []RTFImage `json:"rtfImages,omitempty"`
	ThemeID     int        `json:"themeId"`
	Attachments []string   `json:"attachments"`
}
type Note struct {
	ID         string     `json:"id"`
	Revision   int64      `json:"revision"`
	Document   Document   `json:"document"`
	ConflictOf *string    `json:"conflictOf,omitempty"`
	DeletedAt  *time.Time `json:"deletedAt,omitempty"`
	CreatedAt  time.Time  `json:"createdAt"`
	UpdatedAt  time.Time  `json:"updatedAt"`
}
type Operation struct {
	OperationID  string    `json:"operationId"`
	NoteID       string    `json:"noteId"`
	BaseRevision int64     `json:"baseRevision"`
	Action       string    `json:"action"`
	Document     *Document `json:"document,omitempty"`
}
type OperationResult struct {
	OperationID string `json:"operationId"`
	Status      string `json:"status"`
	Note        Note   `json:"note"`
}

func (d *Document) validate() error {
	if !utf8.ValidString(d.Title) || !utf8.ValidString(d.Text) || !utf8.ValidString(d.RTF) || len([]rune(d.Title)) > 240 || len(d.Text) > 256<<10 || len(d.RTF) > 2<<20 || d.ThemeID < 0 || d.ThemeID > 7 || len(d.Attachments) > 32 {
		return fail(400, "invalid_document", "便签内容无效或超出大小限制。")
	}
	if d.RTF != "" && !strings.HasPrefix(d.RTF, "{\\rtf") {
		return fail(400, "invalid_rtf", "富文本格式无效。")
	}
	rtf, e := unpackRichText(d.RTF, d.RTFGzip)
	if e != nil {
		return e
	}
	seen := map[string]bool{}
	for _, id := range d.Attachments {
		if !validID(id) || seen[id] {
			return fail(400, "invalid_attachment", "图片引用无效。")
		}
		seen[id] = true
	}
	if d.Attachments == nil {
		d.Attachments = []string{}
	}
	return d.validateInlineImages(rtf)
}
func (a *App) push(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	var in struct {
		Operations []Operation `json:"operations"`
	}
	if e := decode(w, r, &in); e != nil {
		return e
	}
	if len(in.Operations) == 0 || len(in.Operations) > 50 {
		return fail(400, "invalid_operations", "每批同步需要 1–50 个操作。")
	}
	for _, op := range in.Operations {
		if !validID(op.OperationID) || !validID(op.NoteID) || op.BaseRevision < 0 {
			return fail(400, "invalid_operation", "同步操作标识无效。")
		}
		if op.Action != "upsert" && op.Action != "delete" && op.Action != "restore" {
			return fail(400, "invalid_operation", "同步操作类型无效。")
		}
		if op.Action == "upsert" {
			if op.Document == nil {
				return fail(400, "missing_document", "缺少便签正文。")
			}
			if e := op.Document.validate(); e != nil {
				return e
			}
			if desktopNeedsInlineImages(r) && len(op.Document.RTFImages) > 0 {
				return inlineUpgradeRequired()
			}
		} else if op.Document != nil {
			return fail(400, "invalid_operation", "删除和恢复操作不应携带正文。")
		}
	}
	if e := a.limited(r, "writes:"+p.AppID+":"+p.UserID, 120, time.Minute); e != nil {
		return e
	}
	tx, e := a.db.Begin(r.Context())
	if e != nil {
		return e
	}
	defer tx.Rollback(r.Context())
	// Serialize the entire user's app stream. Allocating a global SQL sequence
	// would allow a later commit to jump ahead of an uncommitted earlier event.
	var cursor int64
	if e = tx.QueryRow(r.Context(), "SELECT cursor FROM user_apps WHERE app_id=$1 AND user_id=$2 FOR UPDATE", p.AppID, p.UserID).Scan(&cursor); e != nil {
		return e
	}
	if desktopNeedsInlineImages(r) {
		// The stream lock makes this check atomic with newer clients' writes.
		// An older client must never overwrite a template it cannot reconstruct.
		var found bool
		if e = tx.QueryRow(r.Context(), "SELECT EXISTS(SELECT 1 FROM notes WHERE app_id=$1 AND user_id=$2 AND COALESCE(jsonb_array_length(document->'rtfImages'),0)>0)", p.AppID, p.UserID).Scan(&found); e != nil {
			return e
		}
		if found {
			return inlineUpgradeRequired()
		}
	}
	results := make([]OperationResult, 0, len(in.Operations))
	resultBytes := 0
	for _, op := range in.Operations {
		raw, _ := json.Marshal(op)
		hash := digest(string(raw))
		var oldHash string
		var oldResponse []byte
		e = tx.QueryRow(r.Context(), "SELECT request_hash,response FROM operations WHERE app_id=$1 AND user_id=$2 AND id=$3", p.AppID, p.UserID, op.OperationID).Scan(&oldHash, &oldResponse)
		if e == nil {
			if oldHash != hash {
				return fail(409, "operation_reused", "同一操作编号不能用于不同内容。")
			}
			var result OperationResult
			if e = json.Unmarshal(oldResponse, &result); e != nil {
				return e
			}
			resultBytes += len(oldResponse)
			if resultBytes > 8<<20 {
				return fail(413, "response_limit", "本批同步内容过多，请拆分操作后重试。")
			}
			results = append(results, result)
			continue
		}
		if !errors.Is(e, pgx.ErrNoRows) {
			return e
		}
		n, exists, e := loadNote(r, tx, p, op.NoteID)
		if e != nil {
			return e
		}
		result := OperationResult{OperationID: op.OperationID, Status: "applied"}
		now := time.Now().UTC()
		write := true
		if !exists && op.Action != "upsert" {
			return fail(404, "not_found", "便签不存在。")
		}
		if op.Action == "upsert" {
			if !exists && op.BaseRevision != 0 {
				return fail(409, "missing_base", "原便签不存在，无法应用该版本。")
			}
			if exists && (n.Revision != op.BaseRevision || n.DeletedAt != nil) {
				source := op.NoteID
				n = Note{ID: newID(), Revision: 1, CreatedAt: now, ConflictOf: &source}
				result.Status = "conflict_copy"
			} else if exists {
				n.Revision++
			} else {
				n = Note{ID: op.NoteID, Revision: 1, CreatedAt: now}
			}
			n.Document = *op.Document
			n.UpdatedAt = now
			for _, id := range n.Document.Attachments {
				var found bool
				e = tx.QueryRow(r.Context(), "SELECT EXISTS(SELECT 1 FROM attachments WHERE app_id=$1 AND user_id=$2 AND id=$3)", p.AppID, p.UserID, id).Scan(&found)
				if e != nil {
					return e
				}
				if !found {
					return fail(400, "invalid_attachment", "图片不存在或不属于当前账号。")
				}
			}
			if e = a.validateInlineImageSizes(r, tx, p, n.Document); e != nil {
				return e
			}
		} else if n.Revision != op.BaseRevision {
			result.Status = "conflict"
			write = false
		} else if op.Action == "delete" {
			if n.DeletedAt != nil {
				write = false
			} else {
				n.DeletedAt = &now
				n.Revision++
				n.UpdatedAt = now
			}
		} else {
			if n.DeletedAt == nil {
				write = false
			} else if now.Sub(*n.DeletedAt) > 30*24*time.Hour {
				return fail(410, "recovery_expired", "已超过 30 天恢复期限。")
			} else {
				n.DeletedAt = nil
				n.Revision++
				n.UpdatedAt = now
			}
		}
		if write {
			var count int
			if e = tx.QueryRow(r.Context(), "SELECT count(*) FROM notes WHERE app_id=$1 AND user_id=$2", p.AppID, p.UserID).Scan(&count); e != nil {
				return e
			}
			if count >= 1000 && n.Revision == 1 {
				return fail(413, "note_limit", "当前账号已达到 1000 张便签的上限。")
			}
			doc, _ := json.Marshal(n.Document)
			snapshot, _ := json.Marshal(n)
			var used int64
			if e = tx.QueryRow(r.Context(), `SELECT COALESCE((SELECT sum(octet_length(snapshot::text)) FROM note_versions WHERE app_id=$1 AND user_id=$2),0)+COALESCE((SELECT sum(octet_length(bytes)) FROM attachments WHERE app_id=$1 AND user_id=$2),0)`, p.AppID, p.UserID).Scan(&used); e != nil {
				return e
			}
			if used+int64(len(snapshot)) > 256<<20 {
				return fail(413, "storage_limit", "云端存储已达上限，请联系管理员。")
			}
			_, e = tx.Exec(r.Context(), `INSERT INTO notes(app_id,user_id,id,revision,document,conflict_of,deleted_at,created_at,updated_at) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9) ON CONFLICT(app_id,user_id,id) DO UPDATE SET revision=EXCLUDED.revision,document=EXCLUDED.document,conflict_of=EXCLUDED.conflict_of,deleted_at=EXCLUDED.deleted_at,updated_at=EXCLUDED.updated_at`, p.AppID, p.UserID, n.ID, n.Revision, doc, n.ConflictOf, n.DeletedAt, n.CreatedAt, n.UpdatedAt)
			if e != nil {
				return e
			}
			if _, e = tx.Exec(r.Context(), "INSERT INTO note_versions(app_id,user_id,note_id,revision,snapshot) VALUES($1,$2,$3,$4,$5)", p.AppID, p.UserID, n.ID, n.Revision, snapshot); e != nil {
				return e
			}
			cursor++
			if _, e = tx.Exec(r.Context(), "INSERT INTO note_changes(app_id,user_id,sequence,note_id,revision) VALUES($1,$2,$3,$4,$5)", p.AppID, p.UserID, cursor, n.ID, n.Revision); e != nil {
				return e
			}
		}
		result.Note = n
		response, _ := json.Marshal(result)
		resultBytes += len(response)
		if resultBytes > 8<<20 {
			return fail(413, "response_limit", "本批同步内容过多，请拆分操作后重试。")
		}
		if _, e = tx.Exec(r.Context(), "INSERT INTO operations(app_id,user_id,id,request_hash,response) VALUES($1,$2,$3,$4,$5)", p.AppID, p.UserID, op.OperationID, hash, response); e != nil {
			return e
		}
		results = append(results, result)
	}
	if _, e = tx.Exec(r.Context(), "UPDATE user_apps SET cursor=$3 WHERE app_id=$1 AND user_id=$2", p.AppID, p.UserID, cursor); e != nil {
		return e
	}
	if e = tx.Commit(r.Context()); e != nil {
		return e
	}
	writeJSON(w, 200, map[string]any{"results": results, "cursor": strconv.FormatInt(cursor, 10)})
	return nil
}
func loadNote(r *http.Request, tx pgx.Tx, p principal, id string) (Note, bool, error) {
	var n Note
	var raw []byte
	e := tx.QueryRow(r.Context(), "SELECT id::text,revision,document,conflict_of::text,deleted_at,created_at,updated_at FROM notes WHERE app_id=$1 AND user_id=$2 AND id=$3", p.AppID, p.UserID, id).Scan(&n.ID, &n.Revision, &raw, &n.ConflictOf, &n.DeletedAt, &n.CreatedAt, &n.UpdatedAt)
	if e == pgx.ErrNoRows {
		return n, false, nil
	}
	if e != nil {
		return n, false, e
	}
	e = json.Unmarshal(raw, &n.Document)
	return n, true, e
}
func (a *App) pull(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	cursor, e := parseCursor(r.URL.Query().Get("cursor"))
	if e != nil {
		return fail(400, "invalid_cursor", "同步游标无效。")
	}
	limit := 100
	if s := r.URL.Query().Get("limit"); s != "" {
		limit, e = strconv.Atoi(s)
		if e != nil || limit < 1 || limit > 100 {
			return fail(400, "invalid_limit", "每页数量需要在 1–100 之间。")
		}
	}
	tx, e := a.db.BeginTx(r.Context(), pgx.TxOptions{IsoLevel: pgx.RepeatableRead, AccessMode: pgx.ReadOnly})
	if e != nil {
		return e
	}
	defer tx.Rollback(r.Context())
	var high int64
	if e = tx.QueryRow(r.Context(), "SELECT cursor FROM user_apps WHERE app_id=$1 AND user_id=$2", p.AppID, p.UserID).Scan(&high); e != nil {
		return e
	}
	if cursor > high {
		return fail(409, "cursor_ahead", "同步游标超出当前账号范围，请重新拉取。")
	}
	rows, e := tx.Query(r.Context(), `SELECT c.sequence,v.snapshot FROM note_changes c JOIN note_versions v USING(app_id,user_id,note_id,revision) WHERE c.app_id=$1 AND c.user_id=$2 AND c.sequence>$3 AND c.sequence<=$4 ORDER BY c.sequence LIMIT $5`, p.AppID, p.UserID, cursor, high, limit)
	if e != nil {
		return e
	}
	defer rows.Close()
	changes := []json.RawMessage{}
	next := cursor
	totalBytes := 0
	for rows.Next() {
		var raw []byte
		var sequence int64
		if e = rows.Scan(&sequence, &raw); e != nil {
			return e
		}
		if e = checkInlineReader(r, raw); e != nil {
			return e
		}
		if totalBytes+len(raw) > 4<<20 && len(changes) > 0 {
			break
		}
		next = sequence
		totalBytes += len(raw)
		changes = append(changes, raw)
	}
	if e = rows.Err(); e != nil {
		return e
	}
	if len(changes) == 0 {
		next = high
	}
	rows.Close()
	if e = tx.Commit(r.Context()); e != nil {
		return e
	}
	writeJSON(w, 200, map[string]any{"changes": changes, "cursor": strconv.FormatInt(next, 10), "hasMore": next < high, "serverTime": time.Now().UTC()})
	return nil
}
func (a *App) versions(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	id := r.PathValue("note")
	if !validID(id) {
		return fail(404, "not_found", "便签不存在。")
	}
	before := int64(1<<63 - 1)
	if s := r.URL.Query().Get("before"); s != "" {
		var e error
		before, e = parseCursor(s)
		if e != nil {
			return fail(400, "invalid_cursor", "版本游标无效。")
		}
	}
	rows, e := a.db.Query(r.Context(), "SELECT snapshot FROM note_versions WHERE app_id=$1 AND user_id=$2 AND note_id=$3 AND revision<$4 ORDER BY revision DESC LIMIT 20", p.AppID, p.UserID, id, before)
	if e != nil {
		return e
	}
	defer rows.Close()
	out := []json.RawMessage{}
	totalBytes := 0
	for rows.Next() {
		var raw []byte
		if e = rows.Scan(&raw); e != nil {
			return e
		}
		if e = checkInlineReader(r, raw); e != nil {
			return e
		}
		if totalBytes+len(raw) > 4<<20 && len(out) > 0 {
			break
		}
		totalBytes += len(raw)
		out = append(out, raw)
	}
	if e = rows.Err(); e != nil {
		return e
	}
	if len(out) == 0 {
		return fail(404, "not_found", "没有可用的历史版本。")
	}
	writeJSON(w, 200, map[string]any{"versions": out})
	return nil
}
