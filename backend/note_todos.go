package main

import (
	"net/http"
	"sort"
	"strings"
	"time"
)

const (
	noteTodoBatchSize = 50
	noteTodoPageSize  = 100
	noteTodoTextBytes = 256 << 10
	noteTodoPageBytes = 1 << 20
)

type noteTodo struct {
	ID        string     `json:"id"`
	Text      string     `json:"text"`
	CreatedAt *time.Time `json:"createdAt"`
	DueAt     *time.Time `json:"dueAt"`
	Done      bool       `json:"done"`
	DoneAt    *time.Time `json:"doneAt"`
	Repeat    int        `json:"repeat"`
}

type noteTodoChange struct {
	noteTodo
	Revision  int64      `json:"revision"`
	DeletedAt *time.Time `json:"deletedAt"`
}

type noteTodoPush struct {
	Todos []noteTodoChange `json:"todos"`
}

type noteTodoPage struct {
	Todos      []noteTodo `json:"todos"`
	NextCursor string     `json:"nextCursor,omitempty"`
}

func (change noteTodoChange) validate() error {
	if !validID(change.ID) || change.Revision < 1 {
		return fail(400, "invalid_todo_revision", "待办编号或版本无效。")
	}
	if change.DeletedAt != nil {
		return nil
	}
	if len(change.Text) > noteTodoTextBytes || strings.ContainsRune(change.Text, 0) || strings.TrimSpace(change.Text) == "" {
		return fail(400, "invalid_todo_text", "待办内容为空或过大。")
	}
	if change.CreatedAt == nil || change.CreatedAt.IsZero() || change.Repeat < 0 || change.Repeat > 3 {
		return fail(400, "invalid_todo", "待办日期或重复规则无效。")
	}
	return nil
}

func (a *App) pushNoteTodos(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	if p.AppID != "kimnotes" {
		return fail(404, "not_found", "内容不存在或已不可用。")
	}
	if p.ClientKind != "desktop" {
		return fail(403, "desktop_required", "请在小羊便签桌面端修改待办。")
	}
	var in noteTodoPush
	if err := decode(w, r, &in); err != nil {
		return err
	}
	if len(in.Todos) == 0 || len(in.Todos) > noteTodoBatchSize {
		return fail(400, "invalid_todo_batch", "一次可同步 1 至 50 条待办。")
	}
	for _, change := range in.Todos {
		if err := change.validate(); err != nil {
			return err
		}
	}
	// Use the same row lock order when delayed requests overlap.
	sort.SliceStable(in.Todos, func(i, j int) bool { return in.Todos[i].ID < in.Todos[j].ID })

	ctx := r.Context()
	tx, err := a.db.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx)
	for _, change := range in.Todos {
		if change.DeletedAt != nil {
			change.noteTodo = noteTodo{ID: change.ID}
		}
		// Preserve the task contents when applying a tombstone for an existing row.
		_, err = tx.Exec(ctx, `
			INSERT INTO note_todos (app_id, user_id, id, revision, body, created_at, due_at, done, done_at, repeat_rule, deleted_at)
			VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
			ON CONFLICT (app_id, user_id, id) DO UPDATE SET
			  revision=EXCLUDED.revision, deleted_at=EXCLUDED.deleted_at,
			  body=CASE WHEN EXCLUDED.deleted_at IS NULL THEN EXCLUDED.body ELSE note_todos.body END,
			  due_at=CASE WHEN EXCLUDED.deleted_at IS NULL THEN EXCLUDED.due_at ELSE note_todos.due_at END,
			  done=CASE WHEN EXCLUDED.deleted_at IS NULL THEN EXCLUDED.done ELSE note_todos.done END,
			  done_at=CASE WHEN EXCLUDED.deleted_at IS NULL THEN EXCLUDED.done_at ELSE note_todos.done_at END,
			  repeat_rule=CASE WHEN EXCLUDED.deleted_at IS NULL THEN EXCLUDED.repeat_rule ELSE note_todos.repeat_rule END
			WHERE note_todos.revision < EXCLUDED.revision AND note_todos.deleted_at IS NULL`,
			p.AppID, p.UserID, change.ID, change.Revision, change.Text, change.CreatedAt,
			change.DueAt, change.Done, change.DoneAt, change.Repeat, change.DeletedAt)
		if err != nil {
			return err
		}
	}
	if err = tx.Commit(ctx); err != nil {
		return err
	}
	writeJSON(w, 200, map[string]bool{"ok": true})
	return nil
}

func (a *App) listNoteTodos(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	if p.AppID != "kimnotes" {
		return fail(404, "not_found", "内容不存在或已不可用。")
	}
	var afterTime *time.Time
	var afterID *string
	if cursor := r.URL.Query().Get("cursor"); cursor != "" {
		timestamp, id, ok := strings.Cut(cursor, ",")
		created, err := time.Parse(time.RFC3339Nano, timestamp)
		if !ok || err != nil || !validID(id) {
			return fail(400, "invalid_todo_cursor", "待办分页位置无效。")
		}
		afterTime, afterID = &created, &id
	}

	rows, err := a.db.Query(r.Context(), `
		SELECT id::text, body, created_at, due_at, done, done_at, repeat_rule
		FROM note_todos
		WHERE app_id=$1 AND user_id=$2 AND deleted_at IS NULL
		  AND ($3::timestamptz IS NULL OR (created_at, id) < ($3, $4::uuid))
		ORDER BY created_at DESC, id DESC
		LIMIT $5`, p.AppID, p.UserID, afterTime, afterID, noteTodoPageSize+1)
	if err != nil {
		return err
	}
	defer rows.Close()
	page := noteTodoPage{Todos: make([]noteTodo, 0)}
	textBytes := 0
	for rows.Next() {
		var item noteTodo
		if err := rows.Scan(&item.ID, &item.Text, &item.CreatedAt, &item.DueAt, &item.Done, &item.DoneAt, &item.Repeat); err != nil {
			return err
		}
		textBytes += len(item.Text)
		if len(page.Todos) == noteTodoPageSize || textBytes > noteTodoPageBytes {
			last := page.Todos[len(page.Todos)-1]
			page.NextCursor = last.CreatedAt.UTC().Format(time.RFC3339Nano) + "," + last.ID
			break
		}
		page.Todos = append(page.Todos, item)
	}
	if err = rows.Err(); err != nil {
		return err
	}
	writeJSON(w, 200, page)
	return nil
}
