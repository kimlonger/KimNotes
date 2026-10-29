// Export synthetic notes using the shipped controls, without opening windows
// or reading the user's profile. Compile alongside ExportProductAssets.cs.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using KimNotes;
using KimNotes.utils;

internal static class ExportHeroAssets
{
    [STAThread]
    static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("KIMNOTES_DATA_DIR", args[0]);
        Directory.CreateDirectory(args[1]);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        typeof(note).Assembly.GetType("KimNotes.utils.InitConfig").GetMethod("InitSettings", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        typeof(note).Assembly.GetType("KimNotes.utils.UiDpi").GetField("factor", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, (float?)2f);
        var render = typeof(ExportProductAssets).GetMethod("Note", BindingFlags.NonPublic | BindingFlags.Static);
        var titles = new[] { "hero-mint.png", "hero-blue.png", "hero-lilac.png" };
        var content = new[] {
            "今天的小计划\n\n整理本周的项目进度\n记录会议中的新想法\n把截图和资料放在一起\n\n灵感随手记，重要的事慢慢完成。",
            "会议记录\n\n项目进度：周五完成界面优化\n设计反馈：减少操作，突出重点\n\n下一步\n整理资料，将截图插入便签。",
            "灵感摘录\n\n读到一段喜欢的文字，\n先记下来，再慢慢体会。\n\n好的想法往往是在\n随手记录中，变得更加清晰。"
        };
        var original = NoteTheme.All[0];
        try {
            for (int i = 0; i < titles.Length; i++) {
                if (i == 2) NoteTheme.All[0] = NoteTheme.All[4];
                using (var bitmap = (Bitmap)render.Invoke(null, new object[] { 672, 768, content[i], i == 0 }))
                    bitmap.Save(Path.Combine(args[1], titles[i]), ImageFormat.Png);
            }
        } finally { NoteTheme.All[0] = original; }
        Console.WriteLine("Exported three note assets with synthetic content.");
        return 0;
    }
}
