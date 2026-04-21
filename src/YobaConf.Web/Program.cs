using YobaConf.Web;

var builder = WebApplication.CreateBuilder(args);
YobaConfApp.ConfigureServices(builder);

var app = builder.Build();
YobaConfApp.Configure(app);
app.Run();

public partial class Program;
