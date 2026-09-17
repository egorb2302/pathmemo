using Xunit;

// The data directory is process-wide state: --data-dir redirects one static path for the
// whole process (README section 12.1), and the storage tests point it at a temporary
// directory of their own. Two collections running at once would move the store out from
// under each other, so the suite runs sequentially. It costs a few seconds and removes a
// whole class of flaky test.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
