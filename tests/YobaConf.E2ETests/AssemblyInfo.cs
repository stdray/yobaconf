// Each IClassFixture<WebAppFixture> spins up its own Kestrel + Chromium. Running classes in
// parallel means 3+ browser+server pairs racing on startup — on a contended box the `Expect`
// after login times out. Disable test-parallelization within this assembly; we share a single
// UiCollection fixture so the cost of cold-starting one Kestrel is paid exactly once.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
