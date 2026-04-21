using Hocon;
using YobaConf.Core.Converters;

namespace YobaConf.Tests.Converters;

public class JsonToHoconConverterTests
{
	[Fact]
	public void ValidJson_IsReturned_AsPrettyHoconText()
	{
		var result = JsonToHoconConverter.Convert("""{"name":"yoba","port":8080}""");

		// Pretty-printed: indented + newlines.
		result.Should().Contain("\"name\"");
		result.Should().Contain("\"port\"");
		result.Should().Contain("\n");
	}

	[Fact]
	public void ValidJson_Result_ParsesAsHocon_EquivalentContent()
	{
		// The whole point: JSON is valid HOCON, so the converter output must parse through
		// the HOCON parser and reproduce the same key/value structure.
		var result = JsonToHoconConverter.Convert("""{"name":"yoba","port":8080,"enabled":true}""");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("name").Should().Be("yoba");
		config.GetInt("port").Should().Be(8080);
		config.GetBoolean("enabled").Should().BeTrue();
	}

	[Fact]
	public void NestedJson_Preserves_Structure()
	{
		var result = JsonToHoconConverter.Convert("""{"db":{"host":"localhost","port":5432}}""");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("db.host").Should().Be("localhost");
		config.GetInt("db.port").Should().Be(5432);
	}

	[Fact]
	public void ArrayJson_RoundTrips()
	{
		var result = JsonToHoconConverter.Convert("""{"servers":["a","b","c"]}""");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetStringList("servers").Should().Equal("a", "b", "c");
	}

	[Fact]
	public void MalformedJson_ThrowsImportException_WithDescriptiveMessage()
	{
		var act = () => JsonToHoconConverter.Convert("{not-json");

		act.Should().Throw<ImportException>()
			.WithMessage("Invalid JSON:*");
	}

	[Fact]
	public void EmptyObject_RoundTrips()
	{
		var result = JsonToHoconConverter.Convert("{}");

		var config = HoconConfigurationFactory.ParseString(result);
		config.AsEnumerable().Should().BeEmpty();
	}
}
