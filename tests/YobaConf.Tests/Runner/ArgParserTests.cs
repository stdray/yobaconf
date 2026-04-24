using System.Collections;
using YobaConf.Runner;

namespace YobaConf.Tests.Runner;

public sealed class ArgParserTests
{
    static Dictionary<string, string> Env(params (string Key, string Value)[] pairs)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public void HappyPath_Parses_AllFlags()
    {
        var args = new[]
        {
            "--endpoint", "https://yobaconf.local",
            "--api-key", "token22charsxxxxxxxx22",
            "--tag", "env=prod",
            "--tag", "project=yobapub",
            "--template", "dotnet",
            "--", "dotnet", "MyApp.dll", "--flag",
        };

        var result = ArgParser.Parse(args, Env());
        var ok = result.Should().BeOfType<ArgParser.Ok>().Subject.Options;
        ok.Endpoint.Should().Be("https://yobaconf.local");
        ok.ApiKey.Should().Be("token22charsxxxxxxxx22");
        ok.Template.Should().Be("dotnet");
        ok.Tags.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["env"] = "prod",
            ["project"] = "yobapub",
        });
        ok.ChildArgs.Should().Equal(["dotnet", "MyApp.dll", "--flag"]);
    }

    [Fact]
    public void EqualsSyntax_IsSupported()
    {
        var args = new[]
        {
            "--endpoint=https://yobaconf.local",
            "--api-key=t22",
            "--tag=env=prod",
            "--", "x",
        };
        var ok = ArgParser.Parse(args, Env()) as ArgParser.Ok;
        ok.Should().NotBeNull();
        ok!.Options.Endpoint.Should().Be("https://yobaconf.local");
        ok.Options.Tags["env"].Should().Be("prod");
    }

    [Fact]
    public void EnvVars_Fill_In_Missing_Flags()
    {
        var env = Env(
            ("YOBACONF_ENDPOINT", "https://yoba.internal"),
            ("YOBACONF_API_KEY", "env-token-22charsxxxxx"),
            ("YOBACONF_TEMPLATE", "envvar-deep"));
        var result = ArgParser.Parse(["--tag", "env=prod", "--", "cmd"], env);
        var ok = result.Should().BeOfType<ArgParser.Ok>().Subject.Options;
        ok.Endpoint.Should().Be("https://yoba.internal");
        ok.ApiKey.Should().Be("env-token-22charsxxxxx");
        ok.Template.Should().Be("envvar-deep");
    }

    [Fact]
    public void Missing_Endpoint_Is_Error()
    {
        var result = ArgParser.Parse(["--api-key", "t", "--", "x"], Env());
        result.Should().BeOfType<ArgParser.Invalid>()
            .Which.Message.Should().Contain("endpoint");
    }

    [Fact]
    public void Missing_ApiKey_Is_Error()
    {
        var result = ArgParser.Parse(["--endpoint", "https://x", "--", "y"], Env());
        result.Should().BeOfType<ArgParser.Invalid>()
            .Which.Message.Should().Contain("api-key");
    }

    [Fact]
    public void Missing_DashDash_Is_Error()
    {
        var result = ArgParser.Parse(["--endpoint", "x", "--api-key", "t"], Env());
        result.Should().BeOfType<ArgParser.Invalid>()
            .Which.Message.Should().Contain("child command");
    }

    [Fact]
    public void Bad_Tag_Form_Is_Error() =>
        ArgParser.Parse(["--endpoint", "x", "--api-key", "t", "--tag", "nope", "--", "cmd"], Env())
            .Should().BeOfType<ArgParser.Invalid>()
            .Which.Message.Should().Contain("KEY=VALUE");

    [Fact]
    public void Unknown_Flag_Is_Error() =>
        ArgParser.Parse(["--endpoint", "x", "--api-key", "t", "--wat", "--", "cmd"], Env())
            .Should().BeOfType<ArgParser.Invalid>()
            .Which.Message.Should().Contain("unknown");

    [Fact]
    public void Default_Template_Is_Envvar()
    {
        // Empty env + no --template → default. Flat is intentionally avoided since env-var
        // consumers can't use nested JSON.
        var ok = ArgParser.Parse(["--endpoint", "x", "--api-key", "t", "--", "cmd"], Env()) as ArgParser.Ok;
        ok!.Options.Template.Should().Be("envvar");
    }
}
