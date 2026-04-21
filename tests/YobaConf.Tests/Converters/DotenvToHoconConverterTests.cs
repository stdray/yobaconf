using Hocon;
using YobaConf.Core.Converters;

namespace YobaConf.Tests.Converters;

public class DotenvToHoconConverterTests
{
	[Fact]
	public void SimpleKeyValue_RoundTripsThroughHocon()
	{
		var result = DotenvToHoconConverter.Convert("DB_HOST=localhost\nPORT=5432");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("DB_HOST").Should().Be("localhost");
		// Dotenv treats all values as strings (no type inference); HOCON converts on access.
		config.GetString("PORT").Should().Be("5432");
	}

	[Fact]
	public void DoubleQuoted_Value_DecodesEscapes()
	{
		var result = DotenvToHoconConverter.Convert("""MSG="line1\nline2\t\"quoted\"\\end" """);

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("MSG").Should().Be("line1\nline2\t\"quoted\"\\end");
	}

	[Fact]
	public void SingleQuoted_Value_IsLiteral_NoEscapes()
	{
		var result = DotenvToHoconConverter.Convert("LITERAL='no\\nescape'");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("LITERAL").Should().Be("no\\nescape");
	}

	[Fact]
	public void Comments_AreSkipped()
	{
		var result = DotenvToHoconConverter.Convert("""
			# this is a comment
			KEY=value
			# another comment
			OTHER=thing
			""");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("KEY").Should().Be("value");
		config.GetString("OTHER").Should().Be("thing");
	}

	[Fact]
	public void EmptyLines_AreSkipped()
	{
		var result = DotenvToHoconConverter.Convert("\nKEY=value\n\n\nOTHER=thing\n");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("KEY").Should().Be("value");
		config.GetString("OTHER").Should().Be("thing");
	}

	[Fact]
	public void ExportPrefix_IsStripped()
	{
		var result = DotenvToHoconConverter.Convert("export DB_URL=postgres://localhost");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("DB_URL").Should().Be("postgres://localhost");
	}

	[Fact]
	public void EmptyValue_IsEmptyString()
	{
		var result = DotenvToHoconConverter.Convert("EMPTY=");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("EMPTY").Should().Be(string.Empty);
	}

	[Fact]
	public void ValueWithSpaces_Unquoted_IsTrimmed()
	{
		var result = DotenvToHoconConverter.Convert("MSG=  hello world  ");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("MSG").Should().Be("hello world");
	}

	[Fact]
	public void InvalidKey_Throws_WithLineNumber()
	{
		var act = () => DotenvToHoconConverter.Convert("VALID=ok\n123-BAD=nope");

		act.Should().Throw<ImportException>()
			.WithMessage("*Line 2*invalid key '123-BAD'*");
	}

	[Fact]
	public void LineWithoutEquals_Throws_WithLineNumber()
	{
		var act = () => DotenvToHoconConverter.Convert("NOT_A_PAIR");

		act.Should().Throw<ImportException>()
			.WithMessage("*Line 1*expected `KEY=value`*");
	}

	[Fact]
	public void TrailingBackslash_InDoubleQuotedValue_Throws()
	{
		var act = () => DotenvToHoconConverter.Convert("""BAD="value\" """);

		// The quoted-value scanner sees `value\` before matching quote — runs off the end
		// looking for the escape target.
		act.Should().Throw<ImportException>();
	}

	[Fact]
	public void KeyStartingWithUnderscore_IsValid()
	{
		var result = DotenvToHoconConverter.Convert("_INTERNAL=yes");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("_INTERNAL").Should().Be("yes");
	}

	[Fact]
	public void MultilineInput_PreservesOrder_InRenderedOutput()
	{
		// Render output is line-per-pair; parsed HOCON has all keys.
		var result = DotenvToHoconConverter.Convert("A=1\nB=2\nC=3");

		var config = HoconConfigurationFactory.ParseString(result);
		config.GetString("A").Should().Be("1");
		config.GetString("B").Should().Be("2");
		config.GetString("C").Should().Be("3");
	}
}
