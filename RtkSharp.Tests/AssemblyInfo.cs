using Xunit;

// Serialize the whole test assembly: many command tests drive their subjects through the
// process-global Console (CommandRunner.RunFilteredAsync writes filtered output to Console.Out),
// and ReadCommandTests / TreeCommandTests redirect Console.Out/Error via Console.SetOut globally.
// Under xUnit's default per-class parallelism those interleave nondeterministically — e.g. the
// "HELLO" that CommandRunnerTests prints leaks into a StringWriter another class installed as
// Console.Out, corrupting captured output. Disabling parallelization removes the race and makes
// the suite deterministic (the entire suite still runs in a few seconds).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
