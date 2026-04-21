namespace YobaConf.Tests;

public class HoconVariableRendererTests
{
	static Variable Var(string key, string value, string scope = "") =>
		new(key, value, NodePath.ParseDb(scope), DateTimeOffset.UnixEpoch);

	[Fact]
	public void EmptySet_RendersAsEmptyString()
	{
		HoconVariableRenderer.Render([]).Should().BeEmpty();
	}

	[Fact]
	public void SingleVariable_RendersAsQuotedKeyValuePair()
	{
		HoconVariableRenderer.Render([Var("db_host", "localhost")])
			.Should().Be("\"db_host\" = \"localhost\"\n");
	}

	[Fact]
	public void MultipleVariables_SortedOrdinally_ForDeterminism()
	{
		// Render output must be byte-stable regardless of input enumeration order.
		var a = HoconVariableRenderer.Render([Var("z", "1"), Var("a", "2"), Var("m", "3")]);
		var b = HoconVariableRenderer.Render([Var("a", "2"), Var("m", "3"), Var("z", "1")]);
		a.Should().Be(b);
		a.Should().Be("\"a\" = \"2\"\n\"m\" = \"3\"\n\"z\" = \"1\"\n");
	}

	[Fact]
	public void DoubleQuote_InValue_IsEscaped()
	{
		HoconVariableRenderer.Render([Var("motto", "say \"hi\"")])
			.Should().Be("\"motto\" = \"say \\\"hi\\\"\"\n");
	}

	[Fact]
	public void Backslash_InValue_IsEscaped()
	{
		HoconVariableRenderer.Render([Var("path", "C:\\Users")])
			.Should().Be("\"path\" = \"C:\\\\Users\"\n");
	}

	[Fact]
	public void Newline_InValue_IsEscaped()
	{
		HoconVariableRenderer.Render([Var("banner", "line1\nline2")])
			.Should().Be("\"banner\" = \"line1\\nline2\"\n");
	}

	[Fact]
	public void RenderedFragment_RoundTripsThroughHoconParser()
	{
		// Parse the rendered fragment; original Key/Value must come back identical.
		var original = Var("weird key", "complex \"value\" with \\ and \n");
		var fragment = HoconVariableRenderer.Render([original]);

		var config = Hocon.HoconConfigurationFactory.ParseString(fragment);

		config.GetString("weird key").Should().Be("complex \"value\" with \\ and \n");
	}
}
