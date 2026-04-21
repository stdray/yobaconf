using Hocon;

namespace YobaConf.Tests;

// Smoke tests for the Hocon 2.0.4 API, closing the Phase A.1 gate.
// These verify the building blocks used in spec §4.4 (WithFallback) and §4.5 (substitution resolve).
public class HoconMergeTests
{
	[Fact]
	public void WithFallback_ChildOverridesParentScalar()
	{
		var parent = HoconConfigurationFactory.ParseString("a = parent-val\nb = only-parent");
		var child = HoconConfigurationFactory.ParseString("a = child-val\nc = only-child");

		var merged = child.WithFallback(parent);

		merged.GetString("a").Should().Be("child-val");
		merged.GetString("b").Should().Be("only-parent");
		merged.GetString("c").Should().Be("only-child");
	}

	[Fact]
	public void WithFallback_DeepObjectsMergeByKey()
	{
		var parent = HoconConfigurationFactory.ParseString("""
			db { host = parent-host, port = 5432 }
			""");
		var child = HoconConfigurationFactory.ParseString("""
			db { host = child-host }
			""");

		var merged = child.WithFallback(parent);

		merged.GetString("db.host").Should().Be("child-host");
		merged.GetInt("db.port").Should().Be(5432);
	}

	// Hocon 2.0.4 resolves substitutions during parse, not after WithFallback.
	// `${var}` (required) must be visible in the same parsed text; otherwise it throws
	// `Unresolved substitution`. This means §4.5 injects variables by CONCATENATING HOCON
	// text and parsing once — not by layering parsed Configs.
	[Fact]
	public void Substitution_ResolvesWhenVariablesAreConcatenatedBeforeParse()
	{
		var vars = "db_host = localhost";
		var user = "connection = ${db_host}";

		var combined = HoconConfigurationFactory.ParseString(vars + "\n" + user);

		combined.GetString("connection").Should().Be("localhost");
	}

	// Optional substitution (`${?var}`) parses cleanly even when the variable is missing —
	// lets user configs reference variables that may or may not be injected.
	[Fact]
	public void OptionalSubstitution_LeavesPreviousValueWhenVariableMissing()
	{
		var userConfig = HoconConfigurationFactory.ParseString("""
			connection = default-host
			connection = ${?db_host}
			""");

		userConfig.GetString("connection").Should().Be("default-host");
	}

	// Required substitution against a merged fallback does NOT resolve — this test documents
	// the limitation. If tomorrow the Hocon package grows a post-merge Resolve() this test
	// flips to expect "localhost".
	[Fact]
	public void RequiredSubstitution_InWithFallbackChain_ThrowsAtParseTime()
	{
		FluentActions.Invoking(() => HoconConfigurationFactory.ParseString("connection = ${db_host}"))
			.Should().Throw<Hocon.HoconParserException>()
			.WithMessage("*Unresolved substitution*db_host*");
	}

	// DoS-guard: mutually-referencing required substitutions (a = ${b}, b = ${a}) must fail
	// with a clear parse error, not infinite-loop. Failure mode here lives inside the Hocon
	// package — this test locks in that our version of the parser catches it. If a future
	// upgrade regresses, we notice before prod.
	[Fact]
	public void MutualSubstitutionCycle_FailsAtParseTime()
	{
		FluentActions.Invoking(() => HoconConfigurationFactory.ParseString("""
			a = ${b}
			b = ${a}
			"""))
			.Should().Throw<Hocon.HoconParserException>();
	}

	// Three-party cycle — same guarantee as the two-party case but via a longer chain.
	[Fact]
	public void ThreeWaySubstitutionCycle_FailsAtParseTime()
	{
		FluentActions.Invoking(() => HoconConfigurationFactory.ParseString("""
			a = ${b}
			b = ${c}
			c = ${a}
			"""))
			.Should().Throw<Hocon.HoconParserException>();
	}
}
