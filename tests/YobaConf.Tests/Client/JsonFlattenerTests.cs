using System.Text.Json;
using YobaConf.Client;

namespace YobaConf.Tests.Client;

public class JsonFlattenerTests
{
    // JsonFlattener is `internal`; test access via `InternalsVisibleTo("YobaConf.Tests")`
    // in YobaConf.Client/AssemblyInfo.cs.
    static Dictionary<string, string?> Flatten(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonFlattener.Flatten(doc);
    }

    [Fact]
    public void FlatObject_YieldsKeysAtTopLevel()
    {
        var r = Flatten("""{"name":"yoba","port":8080}""");

        r.Should().ContainKey("name").WhoseValue.Should().Be("yoba");
        r.Should().ContainKey("port").WhoseValue.Should().Be("8080");
    }

    [Fact]
    public void NestedObject_UsesColonSeparator()
    {
        var r = Flatten("""{"db":{"host":"localhost","port":5432}}""");

        r.Should().ContainKey("db:host").WhoseValue.Should().Be("localhost");
        r.Should().ContainKey("db:port").WhoseValue.Should().Be("5432");
    }

    [Fact]
    public void Array_UsesColonIndex()
    {
        var r = Flatten("""{"features":["a","b","c"]}""");

        r["features:0"].Should().Be("a");
        r["features:1"].Should().Be("b");
        r["features:2"].Should().Be("c");
    }

    [Fact]
    public void ArrayOfObjects_FlattensDeepKeys()
    {
        var r = Flatten("""{"listeners":[{"port":8080,"ssl":false},{"port":8443,"ssl":true}]}""");

        r["listeners:0:port"].Should().Be("8080");
        r["listeners:0:ssl"].Should().Be("false");
        r["listeners:1:port"].Should().Be("8443");
        r["listeners:1:ssl"].Should().Be("true");
    }

    [Fact]
    public void Booleans_RenderAsLowercase()
    {
        var r = Flatten("""{"enabled":true,"debug":false}""");

        r["enabled"].Should().Be("true");
        r["debug"].Should().Be("false");
    }

    [Fact]
    public void Null_StaysNull_InTheMap()
    {
        var r = Flatten("""{"optional":null}""");

        r.Should().ContainKey("optional");
        r["optional"].Should().BeNull();
    }

    [Fact]
    public void Number_PreservesRawJsonText()
    {
        // Raw text kept so downstream GetValue<T> parses with its own semantics.
        var r = Flatten("""{"ratio":0.75,"count":-42,"big":1e6}""");

        r["ratio"].Should().Be("0.75");
        r["count"].Should().Be("-42");
        r["big"].Should().Be("1e6");
    }

    [Fact]
    public void EmptyObject_YieldsEmptyMap()
    {
        Flatten("{}").Should().BeEmpty();
    }

    [Fact]
    public void CaseInsensitive_Lookup()
    {
        // Microsoft.Extensions.Configuration is case-insensitive by convention. Flattener
        // builds the dict with StringComparer.OrdinalIgnoreCase to match.
        var r = Flatten("""{"DbHost":"localhost"}""");

        r["dbhost"].Should().Be("localhost");
        r["DBHOST"].Should().Be("localhost");
    }
}
