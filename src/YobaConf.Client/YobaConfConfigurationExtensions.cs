using Microsoft.Extensions.Configuration;

namespace YobaConf.Client;

// Public entry point for consumers. Usage (in Program.cs):
//
//     builder.Configuration.AddYobaConf(o =>
//     {
//         o.BaseUrl = "https://yobaconf.3po.su";
//         o.ApiKey = Environment.GetEnvironmentVariable("YOBACONF_API_KEY")!;
//         o.WithTag("env", "prod")
//          .WithTag("project", "yobapub")
//          .WithTag("host", Environment.MachineName);
//         o.RefreshInterval = TimeSpan.FromMinutes(5);
//     });
//
// After this, `IConfiguration` reads (typed `GetValue<T>` / options-binding) come from
// the resolved YobaConf JSON. Changes poll-in every RefreshInterval via ETag conditional GET.
public static class YobaConfConfigurationExtensions
{
    public static IConfigurationBuilder AddYobaConf(
        this IConfigurationBuilder builder,
        Action<YobaConfConfigurationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new YobaConfConfigurationOptions();
        configure(options);

        builder.Add(new YobaConfConfigurationSource { Options = options });
        return builder;
    }
}
