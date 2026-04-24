using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using YobaConf.Core.Audit;
using YobaConf.Core.Bindings;
using YobaConf.Core.Security;
using YobaConf.Core.Storage;

namespace YobaConf.Web.Pages.Bindings;

public sealed class IndexModel : PageModel
{
    readonly IBindingStore _store;
    readonly IBindingStoreAdmin _admin;
    readonly ISecretEncryptor? _encryptor;
    readonly IAuditLogStore _audit;
    readonly IMemoryCache _cache;

    public IndexModel(
        IBindingStore store,
        IBindingStoreAdmin admin,
        ISecretEncryptor? encryptor = null,
        IAuditLogStore audit = null!,
        IMemoryCache cache = null!)
    {
        _store = store;
        _admin = admin;
        _encryptor = encryptor;
        _audit = audit;
        _cache = cache;
    }

    public string? KeyQuery { get; private set; }
    public IReadOnlyDictionary<string, string> TagFilter { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<string> FacetKeys { get; private set; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> FacetValues { get; private set; } =
        new Dictionary<string, IReadOnlyList<string>>();

    public IReadOnlyList<Row> Rows { get; private set; } = [];
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    public sealed record Row(Binding Binding);

    public void OnGet(string? q)
    {
        KeyQuery = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var flash = Request.Query["deleteSuccess"].FirstOrDefault();
        if (!string.IsNullOrEmpty(flash))
            SuccessMessage = "Binding deleted.";

        var all = _store.ListActive();

        var facetKeys = new SortedSet<string>(StringComparer.Ordinal);
        var facetValues = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var b in all)
            foreach (var (k, v) in b.TagSet)
            {
                facetKeys.Add(k);
                if (!facetValues.TryGetValue(k, out var set))
                {
                    set = new SortedSet<string>(StringComparer.Ordinal);
                    facetValues[k] = set;
                }
                set.Add(v);
            }
        FacetKeys = [.. facetKeys];
        FacetValues = facetKeys.ToDictionary(k => k, k => (IReadOnlyList<string>)[.. facetValues[k]]);

        var filter = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in Request.Query)
        {
            if (!kv.Key.StartsWith("t.", StringComparison.Ordinal)) continue;
            var tagKey = kv.Key[2..];
            var tagValue = kv.Value.LastOrDefault();
            if (string.IsNullOrEmpty(tagValue)) continue;
            filter[tagKey] = tagValue;
        }
        TagFilter = filter;

        var matching = all.Where(b => MatchesTagFilter(b, filter) && MatchesKeyQuery(b, KeyQuery));
        Rows = [.. matching.Select(b => new Row(b))];
    }

    public IActionResult OnPostDelete(long id)
    {
        var now = DateTimeOffset.UtcNow;
        var userName = User?.Identity?.Name ?? "system";
        _admin.SoftDelete(id, now, userName);
        return RedirectToPage("Index");
    }

    public IActionResult OnPostReveal(long id)
    {
        var binding = _store.FindById(id);
        if (binding is null) return NotFound();
        if (binding.Kind != BindingKind.Secret) return BadRequest();
        if (_encryptor is null) return StatusCode(500);

        var userName = User?.Identity?.Name ?? "system";
        var cacheKey = "reveal-" + id + "-" + userName;

        string? plaintext;
        if (!_cache.TryGetValue(cacheKey, out plaintext))
        {
            try
            {
                plaintext = _encryptor.Decrypt(binding.Ciphertext!, binding.Iv!, binding.AuthTag!, binding.KeyVersion!);
                _cache.Set(cacheKey, plaintext, TimeSpan.FromSeconds(10));
            }
            catch
            {
                return StatusCode(500);
            }
        }

        // Always audit — cache optimizes CPU, not audit semantics.
        _audit.Append(new AuditLogRow
        {
            Actor = userName,
            Action = AuditAction.Revealed.ToString(),
            EntityType = AuditEntityType.Binding.ToString(),
            TagSetJson = binding.TagSet.CanonicalJson,
            KeyPath = binding.KeyPath,
            OldValue = null,
            NewValue = null,
        });

        return new JsonResult(new { plaintext });
    }

    static bool MatchesTagFilter(Binding b, IReadOnlyDictionary<string, string> filter)
    {
        foreach (var (k, v) in filter)
            if (!b.TagSet.TryGetValue(k, out var bv) || !string.Equals(bv, v, StringComparison.Ordinal))
                return false;
        return true;
    }

    static bool MatchesKeyQuery(Binding b, string? q)
    {
        if (string.IsNullOrEmpty(q)) return true;
        if (q.EndsWith('*'))
            return b.KeyPath.StartsWith(q[..^1], StringComparison.Ordinal);
        return b.KeyPath.Contains(q, StringComparison.Ordinal);
    }
}
