using Xunit;

// Several tests across this assembly redirect the process-wide Console.Out to assert on
// printed output (EventPrinter writes straight to Console.Out, by design - see
// EventPrinter.cs). That is only safe if no two tests can run concurrently: xUnit
// parallelises across test classes by default, and a concurrently-running test in another
// class whose server prints to the real Console.Out would otherwise leak into a redirected
// capture in this class, causing flaky cross-test contamination. Serialize the whole
// assembly rather than hunt down every current and future Console.Out-touching test.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
