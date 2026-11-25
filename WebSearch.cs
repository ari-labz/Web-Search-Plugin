using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

/// <summary>
/// Web search for Ari, through SearXNG. By default the plugin runs SearXNG itself in Docker (container "ari-searxng" on
/// 127.0.0.1:8085, settings in this plugin's folder); with "Use my own SearXNG" it sends searches to an address you give instead.
/// Reading pages is not part of it: fetch_page and the browser are built into ARI.
/// </summary>
[Plugin("Web Search", "Lets Ari search the web through SearXNG. Runs SearXNG for you in Docker, or uses one you already have.")]
public class WebSearchPlugin : ScriptPlugin
{
    internal const string Container = "ari-searxng";
    internal const string Image     = "searxng/searxng";
    internal const int    Port      = 8085;

    /// <summary>Where searches go while the plugin runs; null while it is stopped or still starting.</summary>
    internal string? Endpoint { get; private set; }

    public override IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new("own", "Use my own SearXNG", PluginSettingType.Toggle, "Off: the plugin runs SearXNG for you in Docker (Docker must be installed). On: it uses the address below."),
        new("url", "My SearXNG address", PluginSettingType.Text, "Only with \"Use my own SearXNG\" on, e.g. http://192.168.1.50:8080. Its JSON output must be switched on (search: formats: json).", "http://localhost:8080"),
    ];

    public override string ToolsDescription => "search_web: search the web for current results.";

    protected override async Task OnStart()
    {
        string url;
        if (Flag("own"))
        {
            url = Text("url").Trim().TrimEnd('/');
            if (!(Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && (u.Scheme == "http" || u.Scheme == "https")))
                throw new Exception("Set your SearXNG address first, starting with http:// or https://.");
        }
        else
        {
            StatusText = "Starting SearXNG in Docker…";
            await Docker.EnsureSearXng(PluginFolder);
            url = $"http://127.0.0.1:{Port}";
        }

        await WaitForJson(url);
        Endpoint = url;
        StatusText = "Searching through " + url;
    }

    protected override async Task OnStop()
    {
        Endpoint = null;
        if (!Flag("own")) await Docker.Stop();
    }

    private static async Task WaitForJson(string url)
    {
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
        DateTime giveUp = DateTime.UtcNow.AddSeconds(60);   // a fresh container takes a few seconds to answer
        string last = "";
        while (DateTime.UtcNow < giveUp)
        {
            try
            {
                string probe = await http.GetStringAsync(url + "/search?q=test&format=json");
                if (probe.TrimStart().StartsWith("{")) return;
                last = "it didn't answer with JSON. Switch on the json format in its settings (search: formats: json).";
                break;
            }
            catch (Exception e) { last = e.Message; }
            await Task.Delay(2000);
        }
        throw new Exception($"SearXNG at {url} isn't working: {last}");
    }
}

/// <summary>Runs SearXNG in Docker for the plugin: finds Docker (starting Docker Desktop on a Mac or Windows if it isn't running),
/// writes the settings once, and reuses an existing "ari-searxng" container (including one made by older versions of ARI).</summary>
internal static class Docker
{
    public static async Task EnsureSearXng(string pluginFolder)
    {
        string docker = await Find() ?? throw new Exception(
            "Docker isn't installed. Install Docker Desktop (https://www.docker.com/products/docker-desktop/), or switch on \"Use my own SearXNG\".");

        if (!await DaemonRunning(docker) && !await StartDaemon(docker))
            throw new Exception(OperatingSystem.IsLinux()
                ? "Docker isn't running. Start it (sudo systemctl start docker), then switch this plugin off and on."
                : "Docker Desktop didn't start within a minute. Start it, then switch this plugin off and on.");

        (int running, string state, _) = await Run(docker, $"inspect -f {{{{.State.Running}}}} {WebSearchPlugin.Container}");
        if (running == 0 && state.Trim() == "true") return;
        if (running == 0) { await RunOrThrow(docker, $"start {WebSearchPlugin.Container}"); return; }

        string settings = Path.Combine(pluginFolder, "searxng");
        Directory.CreateDirectory(settings);
        WriteSettings(settings);

        await RunOrThrow(docker, $"pull {WebSearchPlugin.Image}");
        await RunOrThrow(docker,
            $"run -d --name {WebSearchPlugin.Container} -p 127.0.0.1:{WebSearchPlugin.Port}:8080 " +
            $"-v \"{settings}:/etc/searxng\" --restart unless-stopped {WebSearchPlugin.Image}");
    }

    public static async Task Stop()
    {
        if (await Find() is not { } docker) return;
        await Run(docker, $"stop {WebSearchPlugin.Container}");
    }

    private static async Task<string?> Find()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? ["docker", @"C:\Program Files\Docker\Docker\resources\bin\docker.exe"]
            : ["/usr/local/bin/docker", "/opt/homebrew/bin/docker", "/usr/bin/docker", "docker"];
        foreach (string candidate in candidates)
        {
            if (Path.IsPathRooted(candidate) && !File.Exists(candidate)) continue;
            try { if ((await Run(candidate, "--version")).Code == 0) return candidate; } catch { }
        }
        return null;
    }

    private static async Task<bool> DaemonRunning(string docker)
    {
        try { return (await Run(docker, "info")).Code == 0; } catch { return false; }
    }

    private static async Task<bool> StartDaemon(string docker)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("open", "-a Docker") { UseShellExecute = false });
            else if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("cmd", "/c start \"\" \"C:\\Program Files\\Docker\\Docker\\Docker Desktop.exe\"") { UseShellExecute = false });
            else return false;   // Linux needs a password to start the daemon
        }
        catch { return false; }

        Stopwatch waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(2000);
            if (await DaemonRunning(docker)) return true;
        }
        return false;
    }

    private static async Task RunOrThrow(string docker, string args)
    {
        (int code, _, string error) = await Run(docker, args);
        if (code != 0) throw new Exception($"docker {args.Split(' ')[0]} failed: {error.Trim()}");
    }

    private static async Task<(int Code, string Output, string Error)> Run(string exe, string args)
    {
        using Process p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        Task<string> output = p.StandardOutput.ReadToEndAsync();
        Task<string> error  = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await output, await error);
    }

    private static void WriteSettings(string dir)
    {
        string path = Path.Combine(dir, "settings.yml");
        if (File.Exists(path)) return;
        string secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(path, $"""
            use_default_settings: true

            server:
              secret_key: "{secret}"
              limiter: false
              image_proxy: false

            search:
              formats:
                - html
                - json

            engines:
              - name: bing
                engine: bing
                disabled: false
              - name: duckduckgo
                engine: duckduckgo
                disabled: false
              - name: brave
                engine: brave
                disabled: false
            """);
    }
}
