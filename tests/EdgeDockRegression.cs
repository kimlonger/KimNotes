using System;
using System.Drawing;
using System.Reflection;
using KimNotes;

internal static class EdgeDockRegression
{
    private static readonly MethodInfo Resolve = typeof(note).GetMethod("FindDockSide", BindingFlags.NonPublic | BindingFlags.Static);

    private static void Expect(string label, string expected, Point cursor, Rectangle screen, Rectangle work, params Rectangle[] displays)
    {
        var actual = Resolve.Invoke(null, new object[] { cursor, screen, work, displays, 18 })?.ToString();
        if (actual != expected) throw new Exception(label + ": expected " + expected + ", got " + actual);
        Console.WriteLine("PASS " + label);
    }

    private static void Main()
    {
        var main = new Rectangle(0, 0, 1920, 1080);
        var right = new Rectangle(1920, 250, 1440, 850);
        var left = new Rectangle(-1440, 50, 1440, 900);
        var upper = new Rectangle(800, -720, 1280, 720);
        Expect("single screen: top", "Top", new Point(300, 12), main, main, main);
        Expect("single screen: left at threshold", "Left", new Point(18, 600), main, main, main);
        Expect("single screen: right", "Right", new Point(1918, 600), main, main, main);
        Expect("bottom excluded", null, new Point(900, 1078), main, main, main);
        Expect("shared right: actual overlap", null, new Point(1918, 480), main, main, main, right);
        Expect("shared right: short screen excludes the rest of the entire edge", null, new Point(1918, 100), main, main, main, right);
        Expect("next to the shared edge even near the corner", null, new Point(1918, 4), main, main, main, right);
        Expect("shared left of the secondary screen", null, new Point(1922, 400), right, right, main, right);
        Expect("outer edge of the screen at negative coordinates", "Left", new Point(-1438, 400), left, left, main, left);
        Expect("shared top blocks the entire top edge", null, new Point(300, 10), main, main, main, upper);
        Expect("outside edge threshold", null, new Point(40, 340), main, main, main);
        var work = new Rectangle(0, 40, 1920, 1040);
        Expect("reserved taskbar area", null, new Point(300, 10), main, work, main);
        Expect("top respects work area", "Top", new Point(300, 45), main, work, main);

        var titleCharacters = typeof(note).GetMethod("EdgeTitleCharacters", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (var pair in new[] { Tuple.Create("项目 周报备忘", "项目周报"), Tuple.Create("待办", "待办"),
            Tuple.Create(" ", "小羊便签"), Tuple.Create("📌工作记录", "📌工作记") })
        {
            var characters = (string[])titleCharacters.Invoke(null, new object[] { pair.Item1 });
            if (characters.Length > 4 || string.Concat(characters) != pair.Item2)
                throw new Exception("Unexpected edge tab title: " + string.Concat(characters));
        }
        Console.WriteLine("PASS edge tab titles use at most four complete visible characters");
    }
}
