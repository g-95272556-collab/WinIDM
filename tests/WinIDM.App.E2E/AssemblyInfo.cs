// UI tests share one desktop and a single-instance pipe: run one at a time.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
