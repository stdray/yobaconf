using YobaConf.Core.Converters;
using YobaConf.Web.Pages;

namespace YobaConf.Tests;

public sealed class ImportModelTests
{
	[Fact]
	public void IsFlatHocon_Detects_Flat_Env_Output()
	{
		var hocon = DotenvToHoconConverter.Convert("DB_HOST=prod-db\nMAX_CONN=200");
		hocon.Should().NotContain("{");
		hocon.Should().NotContain("[");
	}

	[Fact]
	public void ExtractLeaves_Parses_DotenvConverterOutput()
	{
		var hocon = DotenvToHoconConverter.Convert("DB_HOST=prod-db\nMAX_CONN=200\nFLAG=enabled");
		var leaves = ImportModel.ExtractLeaves(hocon).ToArray();
		leaves.Should().HaveCount(3);
		leaves.Should().Contain(kv => kv.Key == "DB_HOST");
		leaves.Should().Contain(kv => kv.Key == "MAX_CONN");
		leaves.Should().Contain(kv => kv.Key == "FLAG");
	}

	[Fact]
	public void UnquoteHoconValue_Strips_Surrounding_Quotes()
	{
		ImportModel.UnquoteHoconValue("\"hello\"").Should().Be("hello");
		ImportModel.UnquoteHoconValue("bare").Should().Be("bare");
		ImportModel.UnquoteHoconValue("  \"trimmed\"  ").Should().Be("trimmed");
	}
}
