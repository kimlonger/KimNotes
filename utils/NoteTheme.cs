using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using KimNotes.utils;

namespace KimNotes
{
    /// <summary>
    /// 便签主题：定义正文/文字/标题栏/图标等一整套配色，仿便笺的色板切换。
    /// </summary>
    public class NoteTheme
    {
        public int Id;
        public string Name;
        public Color Body;        // 正文/工具栏底色
        public Color Text;        // 正文文字
        public Color Chrome;      // 标题栏底色
        public Color ChromeText;  // 标题栏文字
        public Color Icon;        // 工具栏图标默认色
        public Color IconHover;   // 工具栏图标悬停色
        public Color Divider;     // 工具栏顶部分隔线

        public static readonly List<NoteTheme> All = new List<NoteTheme>
        {
            new NoteTheme { Id = 0, Name = "浅蓝", Body = C(234,240,247), Text = C(43,47,54),
                Chrome = C(219,230,242), ChromeText = C(58,70,84), Icon = C(85,96,107), IconHover = C(74,127,193), Divider = C(205,216,228) },
            new NoteTheme { Id = 1, Name = "柠檬黄", Body = C(253,247,209), Text = C(63,58,42),
                Chrome = C(247,236,180), ChromeText = C(90,80,44), Icon = C(112,102,60), IconHover = C(176,144,40), Divider = C(226,214,160) },
            new NoteTheme { Id = 2, Name = "薄荷绿", Body = C(221,240,227), Text = C(40,54,46),
                Chrome = C(201,230,211), ChromeText = C(52,80,66), Icon = C(74,102,88), IconHover = C(46,140,102), Divider = C(186,214,198) },
            new NoteTheme { Id = 3, Name = "樱花粉", Body = C(251,227,238), Text = C(64,44,54),
                Chrome = C(245,207,224), ChromeText = C(104,60,82), Icon = C(122,89,104), IconHover = C(204,90,140), Divider = C(228,192,208) },
            new NoteTheme { Id = 4, Name = "淡紫", Body = C(233,226,248), Text = C(48,44,64),
                Chrome = C(220,210,240), ChromeText = C(74,66,104), Icon = C(96,88,124), IconHover = C(122,90,200), Divider = C(206,196,232) },
            new NoteTheme { Id = 5, Name = "天蓝", Body = C(217,236,251), Text = C(38,52,66),
                Chrome = C(196,224,246), ChromeText = C(44,74,102), Icon = C(70,100,124), IconHover = C(40,130,200), Divider = C(186,212,232) },
            new NoteTheme { Id = 6, Name = "纸白", Body = C(246,245,241), Text = C(43,47,54),
                Chrome = C(235,233,226), ChromeText = C(74,84,96), Icon = C(90,100,110), IconHover = C(74,127,193), Divider = C(214,224,236) },
            new NoteTheme { Id = 7, Name = "暖橙", Body = C(252,235,219), Text = C(90,70,54),
                Chrome = C(246,214,184), ChromeText = C(122,88,60), Icon = C(150,105,70), IconHover = C(214,120,50), Divider = C(236,201,171) },
        };

        private static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);

        public static NoteTheme Current()
        {
            int id;
            int.TryParse(InitConfig.GetConfigValue("theme"), out id);
            return All.FirstOrDefault(t => t.Id == id) ?? All[0];
        }

        public static void Save(int id)
        {
            InitConfig.SetConfigValue("theme", id.ToString());
        }
    }
}
