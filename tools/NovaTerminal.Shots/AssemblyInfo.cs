using System.Runtime.CompilerServices;

// Lets NovaTerminal.Shots.Tests exercise Program's internal argument-parsing helpers
// (ResolveScenarios, ResolveScale) directly, rather than only through a full Main() run - which
// would mean standing up a real headless MainWindow just to test which tokens count as scenario
// names.
[assembly: InternalsVisibleTo("NovaTerminal.Shots.Tests")]
