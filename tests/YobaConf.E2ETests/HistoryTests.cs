using Microsoft.Extensions.DependencyInjection;
using YobaConf.Core.Audit;
using YobaConf.Core.Bindings;
using YobaConf.Core.Security;
using YobaConf.E2ETests.Infrastructure;

namespace YobaConf.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class HistoryTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	IBrowserContext? _ctx;
	IPage? _page;

	public async Task InitializeAsync()
	{
		foreach (var b in app.Services.GetRequiredService<Core.Storage.SqliteBindingStore>().ListActive())
			app.BindingAdmin.SoftDelete(b.Id, DateTimeOffset.UtcNow);
		foreach (var k in app.ApiKeyAdmin.ListActive())
			app.ApiKeyAdmin.SoftDelete(k.Id, DateTimeOffset.UtcNow);

		_ctx = await app.NewContextAsync();
		_page = await _ctx.NewPageAsync();
	}

	public async Task DisposeAsync()
	{
		if (_ctx is not null)
		{
			await TraceArtifact.StopAndSaveAsync(_ctx, output);
			await _ctx.CloseAsync();
		}
	}

	static Binding Plain(TagSet t, string k, string v) => new()
	{
		Id = 0,
		TagSet = t,
		KeyPath = k,
		Kind = BindingKind.Plain,
		ValuePlain = v,
		ContentHash = string.Empty,
		UpdatedAt = DateTimeOffset.UtcNow,
	};

	static string FreshActor() => "actor-" + Guid.NewGuid().ToString("N")[..8];

	[Fact]
	public async Task History_Filter_By_Entity_Drops_NonMatching_Rows()
	{
		// Fresh actor isolates this test's rows from sibling-test leftovers in the shared DB.
		var actor = FreshActor();
		app.BindingAdmin.Upsert(Plain(TagSet.Empty, "ent-k1", "\"v1\""), actor);
		app.BindingAdmin.Upsert(Plain(TagSet.Empty, "ent-k2", "\"v2\""), actor);
		app.ApiKeyAdmin.Create(TagSet.Empty, null, "ent-ak", DateTimeOffset.UtcNow, actor);

		// actor filter narrows to exactly these 3 entries regardless of DB history.
		await _page!.GotoAsync($"/History?actor={actor}");
		await Expect(_page.GetByTestId("history-row")).ToHaveCountAsync(3);

		await _page.GotoAsync($"/History?actor={actor}&entity=Binding");
		await Expect(_page.GetByTestId("history-row")).ToHaveCountAsync(2);
	}

	[Fact]
	public async Task History_Filter_By_Actor_Is_Exact_Match()
	{
		var alice = FreshActor() + "-alice";
		var bob = FreshActor() + "-bob";
		app.BindingAdmin.Upsert(Plain(TagSet.Empty, "fba-k1", "\"v\""), alice);
		app.BindingAdmin.Upsert(Plain(TagSet.Empty, "fba-k2", "\"v\""), bob);
		app.BindingAdmin.Upsert(Plain(TagSet.Empty, "fba-k3", "\"v\""), alice);

		await _page!.GotoAsync($"/History?actor={alice}");
		var rows = await _page.GetByTestId("history-row").AllAsync();
		rows.Should().HaveCount(2);
		foreach (var r in rows)
			(await r.GetByTestId("history-actor").TextContentAsync())?.Trim().Should().Be(alice);
	}

	[Fact]
	public async Task Rollback_Plain_Binding_Restores_Prior_Value()
	{
		app.BindingAdmin.Upsert(Plain(TagSet.Empty, "log-level", "\"Info\""), "alice");
		app.BindingAdmin.Upsert(Plain(TagSet.Empty, "log-level", "\"Debug\""), "alice");

		await _page!.GotoAsync("/History?entity=Binding&key=log-level");
		_page.Dialog += (_, dlg) => _ = dlg.AcceptAsync();

		// The topmost row is the most recent Updated entry; its OldValue = "\"Info\"".
		var firstRollback = _page.GetByTestId("history-rollback").First;
		await firstRollback.ClickAsync();

		await Expect(_page.GetByTestId("history-success")).ToBeVisibleAsync();

		var store = app.Services.GetRequiredService<Core.Storage.SqliteBindingStore>();
		var current = store.ListActive().Single(b => b.KeyPath == "log-level");
		current.ValuePlain.Should().Be("\"Info\"");
	}

	[Fact]
	public async Task Rollback_Secret_Binding_Restores_Ciphertext_Not_Plaintext()
	{
		var enc = app.Services.GetRequiredService<ISecretEncryptor>();
		var originalBundle = enc.Encrypt("original-pass");
		app.BindingAdmin.Upsert(new Binding
		{
			Id = 0,
			TagSet = TagSet.Empty,
			KeyPath = "db.password",
			Kind = BindingKind.Secret,
			Ciphertext = originalBundle.Ciphertext,
			Iv = originalBundle.Iv,
			AuthTag = originalBundle.AuthTag,
			KeyVersion = originalBundle.KeyVersion,
			ContentHash = string.Empty,
			UpdatedAt = DateTimeOffset.UtcNow,
		}, "alice");

		var newBundle = enc.Encrypt("rotated-pass");
		app.BindingAdmin.Upsert(new Binding
		{
			Id = 0,
			TagSet = TagSet.Empty,
			KeyPath = "db.password",
			Kind = BindingKind.Secret,
			Ciphertext = newBundle.Ciphertext,
			Iv = newBundle.Iv,
			AuthTag = newBundle.AuthTag,
			KeyVersion = newBundle.KeyVersion,
			ContentHash = string.Empty,
			UpdatedAt = DateTimeOffset.UtcNow,
		}, "alice");

		await _page!.GotoAsync("/History?entity=Binding&key=db.password");
		_page.Dialog += (_, dlg) => _ = dlg.AcceptAsync();

		await _page.GetByTestId("history-rollback").First.ClickAsync();
		await Expect(_page.GetByTestId("history-success")).ToBeVisibleAsync();

		var store = app.Services.GetRequiredService<Core.Storage.SqliteBindingStore>();
		var current = store.ListActive().Single(b => b.KeyPath == "db.password");
		current.Kind.Should().Be(BindingKind.Secret);
		// Decrypt restores the *original* plaintext, not the rotated one.
		enc.Decrypt(current.Ciphertext!, current.Iv!, current.AuthTag!, current.KeyVersion!)
			.Should().Be("original-pass");
	}
}
