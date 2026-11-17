using System.Diagnostics;
using GhostCode.Core.Analysis;
using Xunit;

namespace GhostCode.Tests;

public sealed class ArchiveDiffEngineTests
{
    [Fact]
    public void ComputeDiff_TracksAddedRemovedModifiedAndChangeHunksInCorrectDirection()
    {
        string v1 = "line1\nkeepA\noldLine\nkeepB\ntoDelete\nkeepC\n";
        string v2 = "line1\nkeepA\nnewLine\nkeepB\nkeepC\naddedOne\naddedTwo\n";

        var res = SourceDiffEngine.ComputeDiff(v1, v2, "v1", "v2");

        Assert.True(res.HasChanges);
        Assert.Equal(1, res.ModifiedLines); // oldLine -> newLine
        Assert.Equal(1, res.RemovedLines);  // toDelete
        Assert.Equal(2, res.AddedLines);    // addedOne, addedTwo
        Assert.Equal(3, res.SideBySideHunkIndices.Count);
        Assert.Equal(3, res.UnifiedHunkIndices.Count);
    }

    [Fact]
    public void ComputeDiff_AddedAndRemovedInSeparateHunks_AreNotReversed()
    {
        string baseline = "alpha\nbeta\ngamma\ndelta\n";
        string target = "alpha\ngamma\ndelta\nepsilon\n";

        var res = SourceDiffEngine.ComputeDiff(baseline, target, "v1", "v2");

        Assert.Equal(1, res.RemovedLines);
        Assert.Equal(1, res.AddedLines);
        Assert.Equal(0, res.ModifiedLines);
        Assert.Contains(res.SideBySideLines, l => l.Kind == DiffLineKind.Removed && l.LeftText == "beta" && l.LeftLineNumber == 2 && l.RightLineNumber is null);
        Assert.Contains(res.SideBySideLines, l => l.Kind == DiffLineKind.Added && l.RightText == "epsilon" && l.RightLineNumber == 4 && l.LeftLineNumber is null);
        Assert.Equal(2, res.SideBySideHunkIndices.Count);
        Assert.Equal(2, res.UnifiedHunkIndices.Count);
    }

    [Fact]
    public void ComputeDiff_IdenticalAndEmptyFiles_ReturnCleanZeroChangeState()
    {
        var same = SourceDiffEngine.ComputeDiff("int main(){return 0;}", "int main(){return 0;}");
        Assert.False(same.HasChanges);
        Assert.Empty(same.SideBySideHunkIndices);
        Assert.Empty(same.UnifiedHunkIndices);

        var bothEmpty = SourceDiffEngine.ComputeDiff(string.Empty, string.Empty);
        Assert.False(bothEmpty.HasChanges);
        Assert.Empty(bothEmpty.SideBySideLines);

        var emptyToCode = SourceDiffEngine.ComputeDiff(string.Empty, "a\nb");
        Assert.True(emptyToCode.HasChanges);
        Assert.Equal(2, emptyToCode.AddedLines);
        Assert.Equal(0, emptyToCode.RemovedLines);

        var codeToEmpty = SourceDiffEngine.ComputeDiff("a\nb", string.Empty);
        Assert.True(codeToEmpty.HasChanges);
        Assert.Equal(2, codeToEmpty.RemovedLines);
        Assert.Equal(0, codeToEmpty.AddedLines);
    }

    [Fact]
    public void ComputeDiff_SupportsWhitespaceSensitivity_AndLargeFilesFast()
    {
        var ws = SourceDiffEngine.ComputeDiff("int x = 1;", "int x =  1;");
        Assert.True(ws.HasChanges);
        Assert.Equal(1, ws.ModifiedLines);

        var leftLines = Enumerable.Range(1, 3000).Select(i => $"long long v{i} = {i};").ToArray();
        var rightLines = (string[])leftLines.Clone();
        rightLines[1500] = "long long v1501 = 999999;";
        rightLines[2800] = "long long v2801 = 888888;";

        var sw = Stopwatch.StartNew();
        var big = SourceDiffEngine.ComputeDiff(string.Join('\n', leftLines), string.Join('\n', rightLines));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1500, $"Diff took {sw.ElapsedMilliseconds}ms");
        Assert.Equal(2, big.ModifiedLines);
        Assert.Equal(0, big.AddedLines);
        Assert.Equal(0, big.RemovedLines);
        Assert.Equal(2, big.SideBySideHunkIndices.Count);
        Assert.Equal(2, big.UnifiedHunkIndices.Count);
    }
}
