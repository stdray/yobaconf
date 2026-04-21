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
YobaConfApp.ConfigureServices(builder);

var app = builder.Build();
YobaConfApp.Configure(app);
app.Run();

public partial class Program;
