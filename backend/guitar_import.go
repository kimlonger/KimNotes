package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
)

type guitarSeedScore struct {
	Artist      string          `json:"artist"`
	Genre       string          `json:"genre"`
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
				return nil, false, fmt.Errorf("score import exceeds 8 MiB")
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
		Scores []guitarSeedScore `json:"scores"`
	}
	if e := json.Unmarshal(raw, &seed); e != nil {
		return e
	}
	if len(seed.Scores) == 0 {
		return fmt.Errorf("guitar import requires at least one score")
	}
	tx, e := a.db.Begin(ctx)
	if e != nil {
		return e
	}
	defer tx.Rollback(ctx)
	scoreConflict := " ON CONFLICT DO NOTHING"
	if replace {
		scoreConflict = " ON CONFLICT(app_id,id) DO UPDATE SET title=EXCLUDED.title,description=EXCLUDED.description,category=EXCLUDED.category,difficulty=EXCLUDED.difficulty,notation=EXCLUDED.notation,credit=EXCLUDED.credit,artist=EXCLUDED.artist,genre=EXCLUDED.genre,updated_at=now()"
	}
	if _, e = tx.Exec(ctx, "SELECT pg_advisory_xact_lock(725194139)"); e != nil {
		return e
	}
	for _, s := range seed.Scores {
		if s.ID == "" || !json.Valid(s.Notation) {
			return fmt.Errorf("invalid score")
		}
		if _, e = tx.Exec(ctx, `INSERT INTO guitar_scores(id,title,description,category,difficulty,notation,credit,artist,genre) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)`+scoreConflict, s.ID, s.Title, s.Description, s.Category, s.Difficulty, s.Notation, s.Credit, s.Artist, s.Genre); e != nil {
			return e
		}
	}
	return tx.Commit(ctx)
}
