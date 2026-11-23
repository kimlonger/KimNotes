package main

type guitarPracticeFrame struct {
	Level int    `json:"level"`
	Name  string `json:"name"`
	Hours int64  `json:"hours"`
	Days  int    `json:"days"`
}

// Frame numbers correspond to the frame artwork shipped in the clients.
var guitarPracticeFrames = []guitarPracticeFrame{
	{1, "初拨琴弦", 0, 0},
	{2, "和弦学徒", 5, 10},
	{3, "扫弦新手", 15, 30},
	{4, "弹唱琴友", 40, 80},
	{5, "指弹行者", 100, 180},
	{6, "六弦匠人", 250, 400},
	{7, "小羊琴师", 500, 700},
}

type guitarPracticeFrameProgress struct {
	Current guitarPracticeFrame   `json:"current"`
	Next    *guitarPracticeFrame  `json:"next"`
	Levels  []guitarPracticeFrame `json:"levels"`
}

func guitarFrameProgress(seconds int64, days int) guitarPracticeFrameProgress {
	current := 0
	for index, frame := range guitarPracticeFrames {
		if seconds < frame.Hours*3600 || days < frame.Days {
			break
		}
		current = index
	}
	progress := guitarPracticeFrameProgress{Current: guitarPracticeFrames[current], Levels: guitarPracticeFrames}
	if current+1 < len(guitarPracticeFrames) {
		progress.Next = &guitarPracticeFrames[current+1]
	}
	return progress
}
