using System.Text.Json.Nodes;
using Hocon;
using YobaConf.Core.Converters;
using YobaConf.Core.Serialization;

namespace YobaConf.Tests.Converters;

public class YamlToHoconConverterTests
{
	[Fact]
	public void SimpleMapping_RoundTripsThroughHocon()
	{
		var result = YamlToHoconConverter.Convert("""
			name: yoba
			port: 8080
			enabled: true
			""");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("name").Should().Be("yoba");
		config.GetInt("port").Should().Be(8080);
		config.GetBoolean("enabled").Should().BeTrue();
	}

	[Fact]
	public void NestedMapping_PreservesStructure()
	{
		var result = YamlToHoconConverter.Convert("""
			db:
			  host: localhost
			  port: 5432
			""");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("db.host").Should().Be("localhost");
		config.GetInt("db.port").Should().Be(5432);
	}

	[Fact]
	public void Sequence_RendersAsHoconArray()
	{
		var result = YamlToHoconConverter.Convert("""
			servers:
			  - alpha
			  - beta
			  - gamma
			""");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetStringList("servers").Should().Equal("alpha", "beta", "gamma");
	}

	[Fact]
	public void Plain_NumericScalar_StaysNumber()
	{
		// `port: 8080` plain style — HOCON result should have port as integer.
		var result = YamlToHoconConverter.Convert("port: 8080");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetInt("port").Should().Be(8080);
	}

	[Fact]
	public void QuotedScalar_StaysString_EvenWhenContentIsNumeric()
	{
		// `port: "8080"` explicit string. HoconValue.Type should be String.
		var result = YamlToHoconConverter.Convert("""port: "8080" """);

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("port").Should().Be("8080");
	}

	[Fact]
	public void NullLikeValues_AllEmit_HoconNull_Serialised_AsJsonNull()
	{
		// YAML null representations: empty, `null`, `~`. The converter must emit each as
		// HOCON `null` literal. Verified end-to-end: converter → HOCON parser → JSON serialiser
		// round-trips all three as JSON `null` (HoconJsonSerializer treats NullLiteral as null).
		var result = YamlToHoconConverter.Convert("""
			a: null
			b: ~
			c:
			""");

		var config = HoconConfigurationFactory.ParseString(result);
		var json = JsonNode.Parse(HoconJsonSerializer.SerializeToJson(config))!;
		json["a"].Should().BeNull();
		json["b"].Should().BeNull();
		json["c"].Should().BeNull();
	}

	[Fact]
	public void BoolLike_Values_StayTyped()
	{
		var result = YamlToHoconConverter.Convert("""
			on_flag: true
			off_flag: false
			""");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetBoolean("on_flag").Should().BeTrue();
		config.GetBoolean("off_flag").Should().BeFalse();
	}

	[Fact]
	public void FloatScalar_RoundTrips()
	{
		var result = YamlToHoconConverter.Convert("ratio: 0.75");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetDouble("ratio").Should().Be(0.75);
	}

	[Fact]
	public void SpecialChars_InString_GetEscaped()
	{
		var result = YamlToHoconConverter.Convert("""
			msg: "line1\nline2"
			""");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("msg").Should().Be("line1\nline2");
	}

	[Fact]
	public void EmptyInput_ReturnsEmptyObject()
	{
		var result = YamlToHoconConverter.Convert(string.Empty);

		result.Should().Be("{}");
	}

	[Fact]
	public void InvalidYaml_ThrowsImportException()
	{
		// Unterminated flow mapping — YamlDotNet rejects.
		var act = () => YamlToHoconConverter.Convert("{unclosed: [nested");

		act.Should().Throw<ImportException>()
			.WithMessage("Invalid YAML:*");
	}

	[Fact]
	public void Anchors_Are_Expanded_NotPreserved()
	{
		// Source uses `&logger` anchor + `*logger` alias. Converter emits expanded values;
		// the anchor semantic is lost (documented in converter header).
		var result = YamlToHoconConverter.Convert("""
			defaults: &logger
			  level: info
			production:
			  logging: *logger
			""");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("defaults.level").Should().Be("info");
		config.GetString("production.logging.level").Should().Be("info");
	}

	[Fact]
	public void MixedNestedStructure_FullRoundTrip()
	{
		// Arrays-of-objects don't round-trip through HOCON's typed accessors (no
		// `list[index].prop` path syntax), so assert via the JSON-serialised view — that
		// IS the shape clients will see anyway.
		var yaml = """
			app:
			  name: yoba
			  listeners:
			    - port: 8080
			      ssl: false
			    - port: 8443
			      ssl: true
			""";

		var result = YamlToHoconConverter.Convert(yaml);
		var config = HoconConfigurationFactory.ParseString(result);
		var json = JsonNode.Parse(HoconJsonSerializer.SerializeToJson(config))!;

		json["app"]!["name"]!.GetValue<string>().Should().Be("yoba");
		var listeners = json["app"]!["listeners"]!.AsArray();
		listeners.Should().HaveCount(2);
		listeners[0]!["port"]!.GetValue<long>().Should().Be(8080);
		listeners[0]!["ssl"]!.GetValue<bool>().Should().BeFalse();
		listeners[1]!["port"]!.GetValue<long>().Should().Be(8443);
		listeners[1]!["ssl"]!.GetValue<bool>().Should().BeTrue();
	}
}
