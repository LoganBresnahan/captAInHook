using System.Text;

namespace CaptainHook.Install;

// A minimal line-based unified diff (zero new dependencies — invariant 3), so
// install-hooks can SHOW the exact change before it makes it. LCS over lines
// with 3 lines of context, `diff -u` hunk headers. Config files are small; a
// pair too large for the quadratic table degrades to one whole-file hunk,
// which is still an honest picture of the change.
public static class UnifiedDiff
{
    private const int Context = 3;
    private const long MaxCells = 4_000_000;

    public static string Render(string before, string after, string fromLabel, string toLabel)
    {
        var a = Lines(before);
        var b = Lines(after);
        var ops = (long)a.Length * b.Length <= MaxCells ? Script(a, b) : WholeFile(a, b);

        var sb = new StringBuilder();
        sb.Append("--- ").Append(fromLabel).Append('\n');
        sb.Append("+++ ").Append(toLabel).Append('\n');

        // Group ops into hunks: a change run plus up to Context equal lines on
        // either side; runs closer than 2*Context merge into one hunk.
        int i = 0;
        while (i < ops.Count)
        {
            while (i < ops.Count && ops[i].Kind == ' ') i++;
            if (i >= ops.Count) break;
            int start = Math.Max(0, i - Context);
            int end = i;
            while (true)
            {
                while (end < ops.Count && ops[end].Kind != ' ') end++;
                int equal = 0;
                while (end + equal < ops.Count && ops[end + equal].Kind == ' ') equal++;
                if (end + equal < ops.Count && equal <= 2 * Context) { end += equal; continue; }
                end = Math.Min(ops.Count, end + Math.Min(equal, Context));
                break;
            }
            var hunk = ops.GetRange(start, end - start);
            int aStart = hunk[0].A, bStart = hunk[0].B;
            int aLen = hunk.Count(o => o.Kind != '+'), bLen = hunk.Count(o => o.Kind != '-');
            sb.Append($"@@ -{Range(aStart, aLen)} +{Range(bStart, bLen)} @@\n");
            foreach (var o in hunk) sb.Append(o.Kind).Append(o.Text).Append('\n');
            i = end;
        }
        return sb.ToString();
    }

    // `diff -u` convention: 1-based start, and an empty side names the line before.
    private static string Range(int zeroBasedStart, int len) =>
        len == 0 ? $"{zeroBasedStart},0" : len == 1 ? $"{zeroBasedStart + 1}" : $"{zeroBasedStart + 1},{len}";

    private readonly record struct Op(char Kind, string Text, int A, int B);

    private static string[] Lines(string s)
    {
        if (s.Length == 0) return [];
        var t = s.EndsWith('\n') ? s[..^1] : s;
        return t.Split('\n');
    }

    private static List<Op> Script(string[] a, string[] b)
    {
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (int x = a.Length - 1; x >= 0; x--)
            for (int y = b.Length - 1; y >= 0; y--)
                lcs[x, y] = a[x] == b[y] ? lcs[x + 1, y + 1] + 1 : Math.Max(lcs[x + 1, y], lcs[x, y + 1]);

        var ops = new List<Op>();
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            if (i < a.Length && j < b.Length && a[i] == b[j]) { ops.Add(new(' ', a[i], i, j)); i++; j++; }
            // On a tie, delete first: `diff -u` shows the old line above the new.
            else if (i < a.Length && (j == b.Length || lcs[i + 1, j] >= lcs[i, j + 1])) { ops.Add(new('-', a[i], i, j)); i++; }
            else { ops.Add(new('+', b[j], i, j)); j++; }
        }
        return ops;
    }

    private static List<Op> WholeFile(string[] a, string[] b)
    {
        var ops = new List<Op>(a.Length + b.Length);
        for (int i = 0; i < a.Length; i++) ops.Add(new('-', a[i], i, 0));
        for (int j = 0; j < b.Length; j++) ops.Add(new('+', b[j], a.Length, j));
        return ops;
    }
}
