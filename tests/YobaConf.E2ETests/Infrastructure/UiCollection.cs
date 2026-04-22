namespace YobaConf.E2ETests.Infrastructure;

// Single-Kestrel + single-Browser collection — every UI test class shares one fixture. Cold
// startup is paid once; tests use fresh data (FreshSlug helpers) to avoid collisions.
[CollectionDefinition(nameof(UiCollection))]
public sealed class UiCollection : ICollectionFixture<WebAppFixture>;
