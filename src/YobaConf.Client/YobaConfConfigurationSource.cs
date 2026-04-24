using Microsoft.Extensions.Configuration;

namespace YobaConf.Client;

// Factory for `YobaConfConfigurationProvider`. `ConfigurationBuilder.Add(source)` calls
// `Build(builder)` once per `Build()` of the root IConfiguration. We don't need build-time
// state — just hand the provider the already-populated options.
public sealed class YobaConfConfigurationSource : IConfigurationSource
{
    public YobaConfConfigurationOptions Options { get; init; } = new();

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new YobaConfConfigurationProvider(Options);
}
