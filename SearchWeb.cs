using System.Net;
using System.Text;
using System.Text.Json;

/// <summary>search_web: asks SearXNG and returns numbered results ("[1] Title / URL: … / snippet"). Results that share no word with the
/// query are dropped, because engines pad an empty answer with unrelated pages. Card "web_search" makes ARI draw the search card and count
/// the call against her search budget; a result starting "No relevant results for" tells ARI the search found nothing.</summary>
[Tool("search_web", "Search the web for current results (titles, URLs, snippets). Search a thing by its exact name ('Qwen3.6-35B-A3B'), short and one subject at a time, without filler like 'model' or 'vs'. Add site:reddit.com for opinions. Then read the best pages with fetch_page rather than searching again.",
      Access = ToolAccess.Read, Card = "web_search", TimeoutSeconds = 30)]
public class SearchWeb : Tool
{
    [Param("The search query. Short and literal beats descriptive. Supports operators like site:reddit.com, filetype:pdf.")]
    public string Query = "";

    [Param("Number of results to return (1-10). Defaults to 5.", Optional = true)]
    public int NumResults;

    private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(15) };

    public override async Task<string> Run()
    {
        string query = Query.Trim();
        if (query.Length == 0) return "Error: 'query' is required.";
        int count = NumResults > 0 ? Math.Clamp(NumResults, 1, 10) : 5;

        if (ScriptPlugin.Get("websearch") is not WebSearchPlugin { Endpoint: { } endpoint })
            return "Web search isn't available right now: SearXNG is still starting or isn't reachable. See the Web Search plugin's page.";

        string json;
        try { json = await Http.GetStringAsync($"{endpoint}/search?q={Uri.EscapeDataString(query)}&format=json&pageno=1", Cancelled); }
        catch (Exception ex) { return $"Search unavailable: {ex.Message}."; }

        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out JsonElement results)) return "No results returned.";
        string health = EngineHealth(doc.RootElement, results);

        List<string> terms = DistinctiveTerms(query);
        List<(string Title, string Url, string Snippet)> kept = new();
        int dropped = 0;
        foreach (JsonElement r in results.EnumerateArray())
        {
            string title   = r.TryGetProperty("title", out JsonElement t) ? t.GetString() ?? "" : "";
            string url     = r.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? "" : "";
            string snippet = r.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";
            if (terms.Count > 0 && !terms.Any(term => $"{title} {url} {snippet}".Contains(term, StringComparison.OrdinalIgnoreCase))) { dropped++; continue; }
            if (kept.Count < count) kept.Add((title, url, snippet));
        }

        StringBuilder sb = new();
        if (kept.Count == 0)
        {
            sb.AppendLine($"No relevant results for \"{query}\".");
            if (dropped > 0)
                sb.AppendLine($"{dropped} result(s) came back but none mentioned the search terms at all — that is the search engine padding an empty result set, not evidence about the subject.");
            sb.AppendLine();
            sb.AppendLine("Do NOT rewrite this query and search again — rephrasing an unindexed term returns the same filler. " +
                          "Do NOT conclude the term is a typo, misremembered, or non-existent: a search finding nothing is a fact about the index, not about the world, " +
                          "and the term is very likely newer than your training data. Either search the exact canonical identifier if you have not yet " +
                          "(e.g. 'Qwen3.6-35B-A3B' rather than 'Qwen 3.6 35B A3B'), or tell the user plainly that your search found nothing and ask them for a source.");
        }
        else
        {
            for (int i = 0; i < kept.Count; i++)
            {
                sb.AppendLine($"[{i + 1}] {kept[i].Title}");
                sb.AppendLine($"    URL: {kept[i].Url}");
                if (kept[i].Snippet.Length > 0) sb.AppendLine($"    {kept[i].Snippet}");
                sb.AppendLine();
            }
            if (dropped > 0) sb.AppendLine($"({dropped} unrelated result(s) hidden — they matched none of the search terms.)");
        }
        if (health.Length > 0) { sb.AppendLine(); sb.Append(health); }
        return sb.ToString().TrimEnd();
    }

    // Words with no identifying power: without them "Qwen3.6 model" is judged on "qwen3.6" alone.
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","and","are","about","at","best","between","by","can","community","compare","comparison",
        "difference","differences","do","does","for","from","how","in","is","it","latest","new","news","of",
        "on","or","other","review","reviews","sentiment","should","site","that","the","their","this","to",
        "up","use","using","versus","vs","what","when","where","which","who","why","with","model","models",
        "release","released","version","reddit","com","org","net","www","http","https",
    };

    private static List<string> DistinctiveTerms(string query)
    {
        List<string> terms = new();
        foreach (string raw in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string term = raw.Trim('"', '\'', '(', ')', ',', '.', '?', '!', ':', ';');
            if (term.Contains(':') || term.Length < 2 || Filler.Contains(term) || !term.Any(char.IsLetterOrDigit)) continue;
            terms.Add(term);
            if (term.Contains('-') || term.Contains('/'))   // "Qwen3.6-35B-A3B" also matches pages that write the parts apart
                foreach (string part in term.Split('-', '/', StringSplitOptions.RemoveEmptyEntries))
                    if (part.Length >= 2 && !Filler.Contains(part)) terms.Add(part);
        }
        return terms;
    }

    private static string EngineHealth(JsonElement root, JsonElement results)
    {
        if (!root.TryGetProperty("unresponsive_engines", out JsonElement down) || down.ValueKind != JsonValueKind.Array) return "";
        List<string> blocked = new();
        foreach (JsonElement e in down.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() == 0) continue;
            string engine = e[0].GetString() ?? "", reason = e.GetArrayLength() > 1 ? e[1].GetString() ?? "" : "";
            if (engine.Length > 0) blocked.Add(reason.Length > 0 ? $"{engine} ({reason})" : engine);
        }
        if (blocked.Count == 0) return "";
        HashSet<string> working = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement r in results.EnumerateArray())
            if (r.TryGetProperty("engine", out JsonElement en) && en.GetString() is { Length: > 0 } name) working.Add(name);
        int total = working.Count + blocked.Count;
        return $"[Search degraded: {blocked.Count} of {total} engines are rate limited or CAPTCHA'd ({string.Join(", ", blocked)}). " +
               "Results are thinner and lower quality than normal. Treat a missing result as this tool failing, not as proof something does not exist — " +
               "and tell the user your search is rate limited so they know why the answer is thin.]";
    }
}
