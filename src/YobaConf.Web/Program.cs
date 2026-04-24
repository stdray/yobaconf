using Seq.Extensions.Logging;
using YobaConf.Core;
using YobaConf.Web;

// CLI shortcut for operators setting up the admin account. Usage:
//     dotnet run --project src/YobaConf.Web -- --hash-password <plaintext>
//     dotnet YobaConf.Web.dll --hash-password <plaintext>    (in the deployed container)
// Emits the PBKDF2 encoded hash on stdout — copy into appsettings `Admin:PasswordHash`.
// Mirrors yobalog's `--hash-password` entry point.
if (args.Length >= 2 && args[0] == "--hash-password")
{
    Console.WriteLine(AdminPasswordHasher.Hash(args[1]));
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Stamp TraceId/SpanId on every log event (W3C activity tracking). Cheap: zero cost
// when no Activity is current; when AspNetCoreInstrumentation started a request span
// (Phase C.5), that TraceId becomes a CLEF property and yobalog joins logs <-> traces
// on trace-id click-through. Stays enabled even when OTel tracing is off — the stamp
// still reflects ASP.NET's own ambient Activity.
builder.Logging.Configure(o => o.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

// Ship ILogger<T> events to yobalog via its Seq-compat endpoint. Empty ServerUrl =
// provider not registered (console-only logging) — this is what integration tests see:
// user-secrets only load in Development, there's no appsettings.Testing.json, and
// appsettings.json ships an empty YobaLog:ServerUrl. Dev wiring: `dotnet user-secrets
// set YobaLog:ServerUrl ...` + `YobaLog:ApiKey ...`. Prod: env vars `YobaLog__ServerUrl`
// / `YobaLog__ApiKey` pushed into `docker run -e` from GitHub secrets.
//
// `enrichers:` is the MEL-native equivalent of Serilog's Enrich.WithProperty — each
// lambda runs on every CLEF event (startup, background, request-pipeline — all of it,
// no middleware-scope limitation). `App` comes from appsettings.json
// `YobaLog:Properties:App`; consumer projects copy this policy and change only that
// value. Env/Ver/Sha/Host are runtime-computed. See doc/logging-policy.md.
var seqUrl = builder.Configuration["YobaLog:ServerUrl"];
var seqKey = builder.Configuration["YobaLog:ApiKey"];
if (!string.IsNullOrWhiteSpace(seqUrl))
{
    var props = builder.Configuration.GetSection("YobaLog:Properties")
        .GetChildren()
        .Where(c => !string.IsNullOrEmpty(c.Value))
        .ToDictionary(c => c.Key, c => (object)c.Value!);
    props["Env"] = builder.Environment.EnvironmentName;
    props["Ver"] = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev";
    props["Sha"] = Environment.GetEnvironmentVariable("GIT_SHORT_SHA") ?? "local";
    props["Host"] = Environment.MachineName;

    builder.Logging.AddSeq(
        serverUrl: seqUrl,
        apiKey: seqKey,
        enrichers:
        [
            .. props.Select(kv => (Action<EnrichingEvent>)(evt => evt.AddOrUpdateProperty(kv.Key, kv.Value))),
        ]);
}

YobaConfApp.ConfigureServices(builder);

var app = builder.Build();
YobaConfApp.Configure(app);
app.Run();

public partial class Program;
