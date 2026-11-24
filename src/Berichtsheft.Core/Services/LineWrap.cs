namespace Berichtsheft.Core.Services;

/// <summary>Greedy wrapping of an entry into form lines of about <c>width</c> characters.</summary>
public static class LineWrap
{
    public static List<string> WrapText(string? text, int width)
    {
        var words = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        if (words.Length == 0) return new List<string> { "" };
        width = Math.Max(1, width);
        var cur = "";
        foreach (var raw in words)
        {
            var w = raw;
            while (w.Length > width) // a word longer than a line is cut hard
            {
                if (cur.Length > 0) { lines.Add(cur); cur = ""; }
                lines.Add(w[..width]);
                w = w[width..];
            }
            if (w.Length == 0) continue;
            if (cur.Length == 0) cur = w;
            else if (cur.Length + 1 + w.Length <= width) cur += " " + w;
            else { lines.Add(cur); cur = w; }
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }
}
