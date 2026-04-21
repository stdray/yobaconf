using Hocon;
using YobaConf.Core.Serialization;

namespace YobaConf.Tests.Serialization;

public class HoconJsonSerializerTests
{
	static string Serialize(string hocon) =>
		HoconJsonSerializer.SerializeToJson(HoconConfigurationFactory.ParseString(hocon));

	[Fact]
	public void Scalars_String_Int_Double_Bool_Produce_TypedJson()
	{
		var json = Serialize("""
			name = yoba
			port = 8080
			ratio = 0.75
			enabled = true
			""");

		json.Should().Be("""{"enabled":true,"name":"yoba","port":8080,"ratio":0.75}""");
	}

	[Fact]
	public void Nested_Objects_Serialise_As_Nested_Json()
	{
		var json = Serialize("""
			db {
				host = localhost
				port = 5432
			}
			""");

		json.Should().Be("""{"db":{"host":"localhost","port":5432}}""");
	}

	[Fact]
	public void DottedPath_Keys_Flatten_Into_Nested_Objects()
	{
		// HOCON auto-nesting: `a.b.c = 1` parses as {a: {b: {c: 1}}}.
		var json = Serialize("a.b.c = 1");

		json.Should().Be("""{"a":{"b":{"c":1}}}""");
	}

	[Fact]
	public void Arrays_Preserve_Insertion_Order()
	{
		var json = Serialize("""
			servers = [first, second, third]
			ports = [80, 443, 8080]
			""");

		json.Should().Be("""{"ports":[80,443,8080],"servers":["first","second","third"]}""");
	}

	[Fact]
	public void Object_Keys_Are_Sorted_Alphabetically_For_Etag_Stability()
	{
		// Insertion order is reverse-alpha; serializer must emit alpha.
		var json = Serialize("""
			z = 1
			m = 2
			a = 3
			""");

		json.Should().Be("""{"a":3,"m":2,"z":1}""");
	}

	[Fact]
	public void Same_Input_Different_Key_Order_Yields_Identical_Json()
	{
		// Two HOCON texts defining the same keys in different order must serialise
		// to byte-identical JSON — the core determinism invariant for ETag.
		var a = Serialize("""
			a = 1
			b = 2
			c = 3
			""");
		var b = Serialize("""
			c = 3
			a = 1
			b = 2
			""");

		a.Should().Be(b);
	}

	[Fact]
	public void Substitutions_Resolve_To_Concrete_Values_In_Output()
	{
		// Substitution is resolved at parse-time; by serialisation we see the concrete value.
		var json = Serialize("""
			db_host = prod-db
			connection = ${db_host}
			""");

		json.Should().Be("""{"connection":"prod-db","db_host":"prod-db"}""");
	}

	[Fact]
	public void Null_Literal_Emits_Json_Null_Not_The_String_null()
	{
		var json = Serialize("maybe = null");

		json.Should().Be("""{"maybe":null}""");
	}

	[Fact]
	public void Quoted_String_That_Says_null_Stays_A_String()
	{
		// `key = "null"` is a user-authored string that happens to spell "null" — distinct
		// from the null literal. We preserve the distinction.
		var json = Serialize("""key = "null" """);

		json.Should().Be("""{"key":"null"}""");
	}

	[Fact]
	public void Empty_Array_Serialises_As_JsonEmptyArray()
	{
		var json = Serialize("""
			tags = []
			""");

		json.Should().Be("""{"tags":[]}""");
	}

	[Fact]
	public void Nested_Array_Of_Objects_Preserves_Structure()
	{
		var json = Serialize("""
			items = [
				{ name = one, weight = 1 }
				{ name = two, weight = 2 }
			]
			""");

		json.Should().Be("""{"items":[{"name":"one","weight":1},{"name":"two","weight":2}]}""");
	}

	[Fact]
	public void String_With_Special_Chars_Gets_Properly_Escaped()
	{
		// Newline, tab, quote, backslash — must be JSON-escaped in output.
		var json = Serialize(""" label = "line1\nline2\t\"quoted\"\\" """);

		// System.Text.Json escapes " as " by default (safe default) — accept either
		// raw \" or " as long as it round-trips to the same string content.
		var parsed = System.Text.Json.Nodes.JsonNode.Parse(json)!;
		parsed["label"]!.GetValue<string>().Should().Be("line1\nline2\t\"quoted\"\\");
	}

	[Fact]
	public void Pretty_Flag_Emits_Indented_Json()
	{
		var config = HoconConfigurationFactory.ParseString("""
			a = 1
			b = 2
			""");

		var pretty = HoconJsonSerializer.SerializeToJson(config, pretty: true);

		pretty.Should().Contain("\n");
		pretty.Should().Contain("  \"a\": 1");
	}

	[Fact]
	public void Negative_And_Float_Numbers_Round_Trip_Correctly()
	{
		var json = Serialize("""
			temp = -5
			tax = 0.075
			big = 1000000000000
			""");

		var parsed = System.Text.Json.Nodes.JsonNode.Parse(json)!;
		parsed["temp"]!.GetValue<long>().Should().Be(-5);
		parsed["tax"]!.GetValue<double>().Should().Be(0.075);
		parsed["big"]!.GetValue<long>().Should().Be(1_000_000_000_000);
	}

	[Fact]
	public void SerializeToNode_Returns_Manipulable_Tree()
	{
		var config = HoconConfigurationFactory.ParseString("a = 1");
		var node = HoconJsonSerializer.SerializeToNode(config);

		node.Should().NotBeNull();
		node!["a"]!.GetValue<long>().Should().Be(1);
	}

	[Fact]
	public void EmptyObject_Literal_Serialises_As_EmptyJsonObject()
	{
		// Pure empty text throws HoconParserException in 2.0.4 — callers must write `{}`
		// to mean "empty config". That's the realistic empty-node case: a node exists but
		// its RawContent is the literal `{}` string, not an empty string.
		var json = Serialize("{}");

		json.Should().Be("{}");
	}
}
