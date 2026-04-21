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

// Ship ILogger<T> events to yobalog via its Seq-compat endpoint. Empty ServerUrl =
// provider not registered (console-only logging) — this is what integration tests see:
// user-secrets only load in Development, there's no appsettings.Testing.json, and
// appsettings.json ships an empty YobaLog section. Dev wiring: `dotnet user-secrets
// set YobaLog:ServerUrl ...` + `YobaLog:ApiKey ...`. Prod: env vars `YobaLog__ServerUrl`
// / `YobaLog__ApiKey` pushed into `docker run -e` from GitHub secrets.
var seqUrl = builder.Configuration["YobaLog:ServerUrl"];
var seqKey = builder.Configuration["YobaLog:ApiKey"];
if (!string.IsNullOrWhiteSpace(seqUrl))
{
	builder.Logging.AddSeq(seqUrl, apiKey: seqKey);
}

YobaConfApp.ConfigureServices(builder);

var app = builder.Build();
YobaConfApp.Configure(app);
app.Run();

public partial class Program;
