using Xunit;

// Several tests here exercise process-global static state (e.g. TerminalLogger's hooks and
// MinimumLevel). Running test classes in parallel races on that shared state, so serialize the
// whole assembly — it is small and fast, so the cost is negligible.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
