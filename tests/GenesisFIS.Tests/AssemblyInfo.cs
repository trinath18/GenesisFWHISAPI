using Xunit;

// Write tests keep transactions open on shared legacy tables (rates, members); running them in parallel causes lock waits and deadlocks.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
