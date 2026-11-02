package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
)

type guitarSeedCourse struct {
	ID       string              `json:"id"`
	Title    string              `json:"title"`
	Summary  string              `json:"summary"`
	Level    string              `json:"level"`
	Status   string              `json:"status"`
	Chapters []guitarSeedChapter `json:"chapters"`
}
type guitarSeedChapter struct {
	ID      string `json:"id"`
	Title   string `json:"title"`
	Lessons []struct {
		ID      string          `json:"id"`
		Title   string          `json:"title"`
		Content json.RawMessage `json:"content"`
	} `json:"lessons"`
}
type guitarSeedScore struct {
	ID          string          `json:"id"`
	Title       string          `json:"title"`
	Description string          `json:"description"`
	Category    string          `json:"category"`
	Difficulty  string          `json:"difficulty"`
	Notation    json.RawMessage `json:"notation"`
	Credit      string          `json:"credit"`
}

func importGuitar(ctx context.Context, a *App, raw []byte) error {
	return importGuitarContent(ctx, a, raw, false)
}

func guitarImportInput(args []string) ([]byte, bool, error) {
	raw, replace := guitarSeed, false
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--replace":
			if replace {
				return nil, false, fmt.Errorf("duplicate --replace")
			}
			replace = true
		case "--file":
			if i+1 >= len(args) {
				return nil, false, fmt.Errorf("--file requires a path")
			}
			i++
			info, e := os.Stat(args[i])
			if e != nil {
				return nil, false, e
			}
			if info.Size() > 8<<20 {
				return nil, false, fmt.Errorf("course import exceeds 8 MiB")
			}
			raw, e = os.ReadFile(args[i])
			if e != nil {
				return nil, false, e
			}
		default:
			return nil, false, fmt.Errorf("guitar-import accepts --file <path> and --replace")
		}
	}
	return raw, replace, nil
}

func importGuitarContent(ctx context.Context, a *App, raw []byte, replace bool) error {
	var seed struct {
		Courses []guitarSeedCourse `json:"courses"`
		Scores  []guitarSeedScore  `json:"scores"`
	}
	if e := json.Unmarshal(raw, &seed); e != nil {
		return e
	}
	if len(seed.Courses) == 0 {
		return fmt.Errorf("empty guitar seed")
	}
	tx, e := a.db.Begin(ctx)
	if e != nil {
		return e
	}
	defer tx.Rollback(ctx)
	courseConflict, chapterConflict, lessonConflict, scoreConflict := " ON CONFLICT DO NOTHING", " ON CONFLICT DO NOTHING", " ON CONFLICT DO NOTHING", " ON CONFLICT DO NOTHING"
	if replace {
		courseConflict = " ON CONFLICT(app_id,id) DO UPDATE SET title=EXCLUDED.title,summary=EXCLUDED.summary,level=EXCLUDED.level,status=EXCLUDED.status,sort_order=EXCLUDED.sort_order,updated_at=now()"
		chapterConflict = " ON CONFLICT(app_id,id) DO UPDATE SET title=EXCLUDED.title,sort_order=EXCLUDED.sort_order"
		lessonConflict = " ON CONFLICT(app_id,id) DO UPDATE SET title=EXCLUDED.title,sort_order=EXCLUDED.sort_order,content=EXCLUDED.content,updated_at=now()"
		scoreConflict = " ON CONFLICT(app_id,id) DO UPDATE SET title=EXCLUDED.title,description=EXCLUDED.description,category=EXCLUDED.category,difficulty=EXCLUDED.difficulty,notation=EXCLUDED.notation,credit=EXCLUDED.credit"
	}
	if _, e = tx.Exec(ctx, "SELECT pg_advisory_xact_lock(725194139)"); e != nil {
		return e
	}
	for i, c := range seed.Courses {
		if c.ID == "" || c.Title == "" {
			return fmt.Errorf("invalid course")
		}
		if _, e = tx.Exec(ctx, `INSERT INTO guitar_courses(id,title,summary,level,status,sort_order) VALUES($1,$2,$3,$4,$5,$6)`+courseConflict, c.ID, c.Title, c.Summary, c.Level, c.Status, i); e != nil {
			return e
		}
		for j, ch := range c.Chapters {
			if ch.ID == "" {
				return fmt.Errorf("invalid chapter")
			}
			var changedParent bool
			if e = tx.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM guitar_chapters WHERE app_id='guitar' AND id=$1 AND course_id<>$2)`, ch.ID, c.ID).Scan(&changedParent); e != nil {
				return e
			}
			if changedParent {
				return fmt.Errorf("chapter %q already belongs to another course; use a new ID", ch.ID)
			}
			if _, e = tx.Exec(ctx, `INSERT INTO guitar_chapters(id,course_id,title,sort_order) VALUES($1,$2,$3,$4)`+chapterConflict, ch.ID, c.ID, ch.Title, j); e != nil {
				return e
			}
			for k, l := range ch.Lessons {
				if l.ID == "" || l.Title == "" || !json.Valid(l.Content) {
					return fmt.Errorf("invalid lesson")
				}
				if e = tx.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM guitar_lessons WHERE app_id='guitar' AND id=$1 AND chapter_id<>$2)`, l.ID, ch.ID).Scan(&changedParent); e != nil {
					return e
				}
				if changedParent {
					return fmt.Errorf("lesson %q already belongs to another chapter; use a new ID", l.ID)
				}
				if _, e = tx.Exec(ctx, `INSERT INTO guitar_lessons(id,chapter_id,title,sort_order,content) VALUES($1,$2,$3,$4,$5)`+lessonConflict, l.ID, ch.ID, l.Title, k, l.Content); e != nil {
					return e
				}
			}
		}
	}
	for _, s := range seed.Scores {
		if s.ID == "" || !json.Valid(s.Notation) {
			return fmt.Errorf("invalid score")
		}
		if _, e = tx.Exec(ctx, `INSERT INTO guitar_scores(id,title,description,category,difficulty,notation,credit) VALUES($1,$2,$3,$4,$5,$6,$7)`+scoreConflict, s.ID, s.Title, s.Description, s.Category, s.Difficulty, s.Notation, s.Credit); e != nil {
			return e
		}
	}
	return tx.Commit(ctx)
}
