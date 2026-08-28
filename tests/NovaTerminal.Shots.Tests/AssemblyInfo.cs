// These tests are not parallel-safe with each other, and the runner has no way to know it.
//
// DemoWorld's whole job is to redirect process-wide state: NOVATERM_APPDATA_ROOT, so nothing
// reads or rewrites the developer's real settings, plus HOME, USERPROFILE, PATH, PS1, TERM,
// LANG and LC_ALL, so the demo machine's identity is what a shell inherits. Process-wide state
// is shared by every test in the assembly, so with xunit's default per-class parallelism
// ShotHostSmokeTests can construct a MainWindow - which reads NOVATERM_APPDATA_ROOT in its
// constructor - while DemoWorldTests has that root pointed at a directory it is about to
// delete. Two live DemoWorlds also corrupt each other's restore map, each recording the
// other's override as the "previous" value to put back.
//
// Disabled at assembly level rather than by collecting the two classes together, because the
// hazard is the environment rather than those two classes: any test added later that reads
// settings, opens a window or spawns a shell joins the race without anyone noticing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
