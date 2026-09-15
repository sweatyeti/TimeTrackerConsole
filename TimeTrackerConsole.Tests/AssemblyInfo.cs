using Xunit;

// Tests run one at a time: several of them chdir into their own scratch directory (EntryStore
// resolves entries/ against the process working directory), and the test assembly is single-threaded
// so that stays safe. No test ever writes into the repo's own entries/ directory.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
