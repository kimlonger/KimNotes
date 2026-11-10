package main

import (
	"math"
	"strconv"
)

const guitarStarsSQL = `COALESCE(s.difficulty_stars,CASE s.difficulty WHEN '入门' THEN 1 WHEN '初级' THEN 2 ELSE 3 END)::double precision`

func guitarStars(value string) (float64, error) {
	stars, err := strconv.ParseFloat(value, 64)
	if err != nil || math.IsNaN(stars) || math.IsInf(stars, 0) || stars < 0.5 || stars > 5 || stars*2 != math.Trunc(stars*2) {
		return 0, fail(400, "invalid_difficulty", "难度需为0.5到5星，支持半星。")
	}
	return stars, nil
}

func guitarUploadDifficulty(value, legacy string) (float64, string, error) {
	if value == "" {
		stars, ok := map[string]float64{"入门": 1, "初级": 2, "中级": 3}[legacy]
		if !ok {
			return 0, "", fail(400, "invalid_difficulty", "请选择曲谱难度。")
		}
		return stars, legacy, nil
	}
	stars, err := guitarStars(value)
	if err != nil {
		return 0, "", err
	}
	level := "中级"
	if stars <= 1 {
		level = "入门"
	} else if stars <= 2.5 {
		level = "初级"
	}
	return stars, level, nil
}
