namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// How many exceptions (the outermost, its causes and aggregate siblings, outermost first) one managed
    /// payload may carry. An aggregate of thousands of faulted tasks must not grow the report without bound; the
    /// worker symbolicates at most 5 anyway. Pure logic so it can be unit-tested.
    /// </summary>
    public sealed class ManagedNodeBudget
    {
        public const int DefaultMax = 8;

        int _left;

        public ManagedNodeBudget(int max = DefaultMax) { _left = max; }

        public bool IsExhausted { get { return _left <= 0; } }

        /// <summary>Takes one node; false once the budget is spent.</summary>
        public bool TryTake()
        {
            if (_left <= 0) return false;
            _left--;
            return true;
        }
    }
}
