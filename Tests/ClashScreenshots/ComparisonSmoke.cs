using System;
using System.Collections.Generic;
using PluginForRevit2022.Core.Clashes;

internal static class ComparisonSmoke
{
    private static ClashItem Item(string key, string test, long id1 = 0, long id2 = 0) => new ClashItem
    {
        Key = key, Test = test, Item1 = new ClashSide { Id = id1 == 0 ? (long?)null : id1, File = "A.rvt" },
        Item2 = new ClashSide { Id = id2 == 0 ? (long?)null : id2, File = "B.rvt" },
    };
    public static void Run()
    {
        var old = new List<ClashItem> { Item("same", "T"), Item("old-key", "T", 1, 2), Item("removed", "T") };
        var next = new List<ClashItem> { Item("same", "T"), Item("new-key", "T", 1, 2), Item("added", "T") };
        var result = ClashReportComparison.Compare(old, next, true);
        if (result.Common.Count != 2 || result.Added.Count != 1 || result.Removed.Count != 1) throw new Exception("Clash diff partitions");
        result = ClashReportComparison.Compare(old, next, false);
        if (result.Common.Count != 1) throw new Exception("Element matching cannot be disabled");
        old.Add(Item("other-old", "T", 1, 2));
        result = ClashReportComparison.Compare(old, next, true);
        if (result.Ambiguous.Count == 0 || result.Common.Count != 1) throw new Exception("Ambiguous element pairs guessed");
        result = ClashReportComparison.Compare(new[] { Item("same", "T", 1, 2) }, new[] { Item("same", "T", 3, 4) }, true);
        if (result.Common.Count != 0) throw new Exception("Same name matched another pair");
        result = ClashReportComparison.Compare(new[] { Item("same", "T1") }, new[] { Item("same", "T2") }, true);
        if (result.Common.Count != 0) throw new Exception("Different tests matched");
        Console.WriteLine("PASS: clash report revisions, renamed keys, ambiguity, different tests and changed element pairs");
    }
}
