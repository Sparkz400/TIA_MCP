namespace TiaMcp.Core
{
    /// <summary>
    /// The uniform paging totals every item pager returns beside its own total field: <c>totalItems</c> repeats that
    /// total and <c>pageCount</c> is the number of pages at the effective <c>count</c> of the request. Both are null
    /// when the family's total is unknown or partial. Byte budgets can end a page early, so <c>nextOffset</c> stays the
    /// continuation signal. Dependency free: the net48 worker links this file.
    /// </summary>
    public static class PageTotals
    {
        /// <summary>ceil(total / count), 0 for an empty result, null when the total is unknown.</summary>
        public static long? PageCount(long? total, int count)
        {
            if (total is not long known) return null;
            if (count < 1) throw new System.ArgumentOutOfRangeException(nameof(count), "The effective page count must be at least 1.");
            return known <= 0 ? 0 : (known + count - 1) / count;
        }

        /// <summary>The same for an int total, so an in-process reader of a JSON node gets the type of the total beside it.</summary>
        public static int? PageCount(int? total, int count) => total is int known ? (int)PageCount((long)known, count)!.Value : null;
    }
}
