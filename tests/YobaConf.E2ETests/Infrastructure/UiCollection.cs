namespace YobaConf.E2ETests.Infrastructure;

// Single Kestrel + single browser per run. Starting multiple WebApplication + Chromium pairs
// in parallel would race Kestrel port binding and Playwright IPC, producing flaky POST
// timeouts on login. One shared fixture = one cold start; test classes use unique node
// paths to avoid data collisions against the same backing store.
[CollectionDefinition(nameof(UiCollection))]
public sealed class UiCollection : ICollectionFixture<WebAppFixture>;
