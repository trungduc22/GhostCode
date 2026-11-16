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

public sealed record SourceDiffResult(
    int AddedLines,
    int RemovedLines,
    int ModifiedLines,
    IReadOnlyList<DiffLinePair> SideBySideLines,
    string UnifiedDiffText);

/// <summary>
/// Non-destructive line-by-line source diff engine supporting Side-by-Side
/// and Unified Diff views for C, C++, Python, and text archives.
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

        var rawOps = (long)left.Length * right.Length <= 2_500_000
            ? ComputeLcsOps(left, right)
            : ComputeLargeFileOps(left, right);

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

            // Collect contiguous block of Removed/Added operations and pair 1:1 as Modified where possible
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

        var unified = BuildUnifiedDiff(leftLabel, rightLabel, sideBySide);
        return new SourceDiffResult(added, removed, modified, sideBySide, unified);
    }

    private static string[] SplitLinesNormalized(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();
        var norm = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return norm.Split('\n');
    }

    private readonly record struct RawDiffOp(DiffLineKind Kind, int LeftIdx, int RightIdx);

    private static List<RawDiffOp> ComputeLcsOps(string[] left, string[] right)
    {
        int n = left.Length;
        int m = right.Length;
        int prefix = 0;
        while (prefix < n && prefix < m && string.Equals(left[prefix], right[prefix], StringComparison.Ordinal))
            prefix++;

        int suffix = 0;
        while (suffix < n - prefix && suffix < m - prefix
            && string.Equals(left[n - 1 - suffix], right[m - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        var ops = new List<RawDiffOp>(Math.Max(n, m));
        for (int p = 0; p < prefix; p++)
            ops.Add(new RawDiffOp(DiffLineKind.Unchanged, p, p));

        int midN = n - prefix - suffix;
        int midM = m - prefix - suffix;
        if (midN > 0 || midM > 0)
        {
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
        }

        for (int s = 0; s < suffix; s++)
            ops.Add(new RawDiffOp(DiffLineKind.Unchanged, n - suffix + s, m - suffix + s));

        return ops;
    }

    private static List<RawDiffOp> ComputeLargeFileOps(string[] left, string[] right)
    {
        var ops = new List<RawDiffOp>(Math.Max(left.Length, right.Length));
        int i = 0, j = 0;
        var rightIndex = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (int r = 0; r < right.Length; r++)
        {
            if (!rightIndex.TryGetValue(right[r], out var q))
            {
                q = new Queue<int>();
                rightIndex[right[r]] = q;
            }
            q.Enqueue(r);
        }

        while (i < left.Length && j < right.Length)
        {
            if (string.Equals(left[i], right[j], StringComparison.Ordinal))
            {
                ops.Add(new RawDiffOp(DiffLineKind.Unchanged, i, j));
                i++;
                j++;
                continue;
            }

            // Bounded lookahead sync window for very large files
            int nextMatchR = -1;
            if (rightIndex.TryGetValue(left[i], out var q))
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
                    ops.Add(new RawDiffOp(DiffLineKind.Added, -1, j));
                    j++;
                }
            }
            else
            {
                ops.Add(new RawDiffOp(DiffLineKind.Removed, i, -1));
                i++;
            }
        }

        while (i < left.Length) { ops.Add(new RawDiffOp(DiffLineKind.Removed, i++, -1)); }
        while (j < right.Length) { ops.Add(new RawDiffOp(DiffLineKind.Added, -1, j++)); }
        return ops;
    }

    private static string BuildUnifiedDiff(string leftLabel, string rightLabel, List<DiffLinePair> pairs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- {leftLabel}");
        sb.AppendLine($"+++ {rightLabel}");
        foreach (var p in pairs)
        {
            switch (p.Kind)
            {
                case DiffLineKind.Unchanged:
                    sb.AppendLine($"  {p.LeftText}");
                    break;
                case DiffLineKind.Removed:
                    sb.AppendLine($"- {p.LeftText}");
                    break;
                case DiffLineKind.Added:
                    sb.AppendLine($"+ {p.RightText}");
                    break;
                case DiffLineKind.Modified:
                    sb.AppendLine($"- {p.LeftText}");
                    sb.AppendLine($"+ {p.RightText}");
                    break;
            }
        }
        return sb.ToString();
    }
}
