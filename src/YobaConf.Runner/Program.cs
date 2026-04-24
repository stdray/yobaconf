using YobaConf.Runner;

var parsed = ArgParser.Parse(args, Environment.GetEnvironmentVariables());
if (parsed is ArgParser.Invalid err)
{
    Console.Error.WriteLine(err.Message);
    Console.Error.WriteLine(ArgParser.UsageText);
    return ExitCodes.InvalidArgs;
}

var options = ((ArgParser.Ok)parsed).Options;

// Cancel on SIGINT / SIGTERM and propagate into the HTTP fetch + child wait.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;  // let the child handle shutdown; don't let OS kill us before we forward.
    cts.Cancel();
};

using var http = new HttpClient
{
    // Generous default — resolve-endpoint is usually local-ish or over VPN. User can
    // bias via OS-level TCP timeout if they really need tighter SLAs.
    Timeout = TimeSpan.FromSeconds(30),
};

var runner = new Runner(http, new ProcessChildExec(), Console.Error);
return await runner.RunAsync(options, cts.Token).ConfigureAwait(false);
