using System.Text;

namespace GhostCode.Core.Analysis;

public enum DiffLineKind
{
    Unchanged,
    Added,
    Removed,
    Modified
}

public sealed record DiffLinePair(
    int? LeftLineNumber,
    string LeftText,
    int? RightLineNumber,
    string RightText,
    DiffLineKind Kind);

public sealed record UnifiedDiffLine(
    int? OldLineNumber,
    int? NewLineNumber,
    DiffLineKind Kind,
    char PrefixGlyph,
    string Text);

public sealed record SourceDiffResult(
    int AddedLines,
    int RemovedLines,
    int ModifiedLines,
    IReadOnlyList<DiffLinePair> SideBySideLines,
    string UnifiedDiffText,
    IReadOnlyList<UnifiedDiffLine>? UnifiedLines = null,
    IReadOnlyList<int>? SideBySideChangeStarts = null,
    IReadOnlyList<int>? UnifiedChangeStarts = null)
{
    public IReadOnlyList<UnifiedDiffLine> UnifiedLineRows => UnifiedLines ?? Array.Empty<UnifiedDiffLine>();
    public IReadOnlyList<int> SideBySideHunkIndices => SideBySideChangeStarts ?? Array.Empty<int>();
    public IReadOnlyList<int> UnifiedHunkIndices => UnifiedChangeStarts ?? Array.Empty<int>();
    public bool HasChanges => AddedLines > 0 || RemovedLines > 0 || ModifiedLines > 0;
}

/// <summary>
/// Non-destructive line-by-line source diff engine supporting virtualized Side-by-Side
/// and GitHub-style Unified Diff views with hunk navigation for C, C++, Python, and text archives.
/// </summary>
public static class SourceDiffEngine
{
    public static SourceDiffResult ComputeDiff(
        string leftText,
        string rightText,
        string leftLabel = "Left",
        string rightLabel = "Right")
    {
        var left = SplitLinesNormalized(leftText);
        var right = SplitLinesNormalized(rightText);

        // Trim matching prefix & suffix first so large files with small edits still get exact LCS cleanly
        int prefix = 0;
        while (prefix < left.Length && prefix < right.Length
            && string.Equals(left[prefix], right[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < left.Length - prefix && suffix < right.Length - prefix
            && string.Equals(left[left.Length - 1 - suffix], right[right.Length - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        int midN = left.Length - prefix - suffix;
        int midM = right.Length - prefix - suffix;

        var rawOps = new List<RawDiffOp>(Math.Max(left.Length, right.Length));
        for (int p = 0; p < prefix; p++)
            rawOps.Add(new RawDiffOp(DiffLineKind.Unchanged, p, p));

        if (midN > 0 || midM > 0)
        {
            var midOps = (long)midN * midM <= 2_500_000
                ? ComputeLcsMiddleOps(left, right, prefix, midN, midM)
                : ComputeLargeMiddleOps(left, right, prefix, midN, midM);
            rawOps.AddRange(midOps);
        }

        for (int s = 0; s < suffix; s++)
            rawOps.Add(new RawDiffOp(DiffLineKind.Unchanged, left.Length - suffix + s, right.Length - suffix + s));

        var sideBySide = new List<DiffLinePair>(rawOps.Count);
        int added = 0, removed = 0, modified = 0;

        int i = 0;
        while (i < rawOps.Count)
        {
            var op = rawOps[i];
            if (op.Kind == DiffLineKind.Unchanged)
            {
                sideBySide.Add(new DiffLinePair(op.LeftIdx + 1, left[op.LeftIdx], op.RightIdx + 1, right[op.RightIdx], DiffLineKind.Unchanged));
                i++;
                continue;
            }

            var remBlock = new List<int>();
            var addBlock = new List<int>();
            while (i < rawOps.Count && rawOps[i].Kind != DiffLineKind.Unchanged)
            {
                if (rawOps[i].Kind == DiffLineKind.Removed)
                    remBlock.Add(rawOps[i].LeftIdx);
                else
                    addBlock.Add(rawOps[i].RightIdx);
                i++;
            }

            int paired = Math.Min(remBlock.Count, addBlock.Count);
            for (int k = 0; k < paired; k++)
            {
                int li = remBlock[k];
                int ri = addBlock[k];
                sideBySide.Add(new DiffLinePair(li + 1, left[li], ri + 1, right[ri], DiffLineKind.Modified));
                modified++;
            }

            for (int k = paired; k < remBlock.Count; k++)
            {
                int li = remBlock[k];
                sideBySide.Add(new DiffLinePair(li + 1, left[li], null, string.Empty, DiffLineKind.Removed));
                removed++;
            }

            for (int k = paired; k < addBlock.Count; k++)
            {
                int ri = addBlock[k];
                sideBySide.Add(new DiffLinePair(null, string.Empty, ri + 1, right[ri], DiffLineKind.Added));
                added++;
            }
        }

        var (unifiedText, unifiedLines) = BuildUnifiedDiff(leftLabel, rightLabel, sideBySide);
        var sbsHunks = CollectHunkStarts(sideBySide, static p => p.Kind != DiffLineKind.Unchanged);
        var uniHunks = CollectHunkStarts(unifiedLines, static u => u.Kind != DiffLineKind.Unchanged);

        return new SourceDiffResult(
            added,
            removed,
            modified,
            sideBySide,
            unifiedText,
            unifiedLines,
            sbsHunks,
            uniHunks);
    }

    private static List<int> CollectHunkStarts<T>(IReadOnlyList<T> rows, Func<T, bool> isChanged)
    {
        var starts = new List<int>();
        bool inHunk = false;
        for (int i = 0; i < rows.Count; i++)
        {
            bool changed = isChanged(rows[i]);
            if (changed && !inHunk)
                starts.Add(i);
            inHunk = changed;
        }
        return starts;
    }

    private static string[] SplitLinesNormalized(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();
        var norm = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return norm.Split('\n');
    }

    private readonly record struct RawDiffOp(DiffLineKind Kind, int LeftIdx, int RightIdx);

    private static List<RawDiffOp> ComputeLcsMiddleOps(string[] left, string[] right, int prefix, int midN, int midM)
    {
        var ops = new List<RawDiffOp>(Math.Max(midN, midM));
        var dp = new int[midN + 1, midM + 1];
        for (int i = midN - 1; i >= 0; i--)
        {
            for (int j = midM - 1; j >= 0; j--)
            {
                dp[i, j] = string.Equals(left[prefix + i], right[prefix + j], StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < midN && y < midM)
        {
            if (string.Equals(left[prefix + x], right[prefix + y], StringComparison.Ordinal))
            {
                ops.Add(new RawDiffOp(DiffLineKind.Unchanged, prefix + x, prefix + y));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                ops.Add(new RawDiffOp(DiffLineKind.Removed, prefix + x, -1));
                x++;
            }
            else
            {
                ops.Add(new RawDiffOp(DiffLineKind.Added, -1, prefix + y));
                y++;
            }
        }

        while (x < midN)
        {
            ops.Add(new RawDiffOp(DiffLineKind.Removed, prefix + x, -1));
            x++;
        }
        while (y < midM)
        {
            ops.Add(new RawDiffOp(DiffLineKind.Added, -1, prefix + y));
            y++;
        }

        return ops;
    }

    private static List<RawDiffOp> ComputeLargeMiddleOps(string[] left, string[] right, int prefix, int midN, int midM)
    {
        var ops = new List<RawDiffOp>(Math.Max(midN, midM));
        int i = 0, j = 0;
        var rightIndex = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (int r = 0; r < midM; r++)
        {
            var key = right[prefix + r];
            if (!rightIndex.TryGetValue(key, out var q))
            {
                q = new Queue<int>();
                rightIndex[key] = q;
            }
            q.Enqueue(r);
        }

        while (i < midN && j < midM)
        {
            if (string.Equals(left[prefix + i], right[prefix + j], StringComparison.Ordinal))
            {
                ops.Add(new RawDiffOp(DiffLineKind.Unchanged, prefix + i, prefix + j));
                i++;
                j++;
                continue;
            }

            int nextMatchR = -1;
            if (rightIndex.TryGetValue(left[prefix + i], out var q))
            {
                while (q.Count > 0 && q.Peek() < j)
                    q.Dequeue();
                if (q.Count > 0 && q.Peek() - j <= 32)
                    nextMatchR = q.Peek();
            }

            if (nextMatchR > j)
            {
                while (j < nextMatchR)
                {
                    ops.Add(new RawDiffOp(DiffLineKind.Added, -1, prefix + j));
                    j++;
                }
            }
            else
            {
                ops.Add(new RawDiffOp(DiffLineKind.Removed, prefix + i, -1));
                i++;
            }
        }

        while (i < midN) { ops.Add(new RawDiffOp(DiffLineKind.Removed, prefix + i++, -1)); }
        while (j < midM) { ops.Add(new RawDiffOp(DiffLineKind.Added, -1, prefix + j++)); }
        return ops;
    }

    private static (string Text, List<UnifiedDiffLine> Rows) BuildUnifiedDiff(
        string leftLabel,
        string rightLabel,
        List<DiffLinePair> pairs)
    {
        var sb = new StringBuilder();
        var rows = new List<UnifiedDiffLine>(pairs.Count);
        sb.AppendLine($"--- {leftLabel}");
        sb.AppendLine($"+++ {rightLabel}");

        foreach (var p in pairs)
        {
            switch (p.Kind)
            {
                case DiffLineKind.Unchanged:
                    sb.AppendLine($"  {p.LeftText}");
                    rows.Add(new UnifiedDiffLine(p.LeftLineNumber, p.RightLineNumber, DiffLineKind.Unchanged, ' ', p.LeftText));
                    break;
                case DiffLineKind.Removed:
                    sb.AppendLine($"- {p.LeftText}");
                    rows.Add(new UnifiedDiffLine(p.LeftLineNumber, null, DiffLineKind.Removed, '-', p.LeftText));
                    break;
                case DiffLineKind.Added:
                    sb.AppendLine($"+ {p.RightText}");
                    rows.Add(new UnifiedDiffLine(null, p.RightLineNumber, DiffLineKind.Added, '+', p.RightText));
                    break;
                case DiffLineKind.Modified:
                    sb.AppendLine($"- {p.LeftText}");
                    sb.AppendLine($"+ {p.RightText}");
                    rows.Add(new UnifiedDiffLine(p.LeftLineNumber, null, DiffLineKind.Removed, '-', p.LeftText));
                    rows.Add(new UnifiedDiffLine(null, p.RightLineNumber, DiffLineKind.Added, '+', p.RightText));
                    break;
            }
        }
        return (sb.ToString(), rows);
    }
}
