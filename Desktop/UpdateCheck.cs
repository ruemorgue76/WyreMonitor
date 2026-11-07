using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;

namespace WyreMonitor.Desktop;

/// <summary>
/// Asks GitHub for the latest release. The only request WyreMonitor makes outside this PC, made
/// when the user asks or, if they turned it on, once a day. A newer version is offered as a link
/// to its release page: nothing is downloaded or run by the app.
/// </summary>
internal static class UpdateCheck
{
    public const string RepoUrl = "https://github.com/ruemorgue76/WyreMonitor";
    private const string LatestApi = "https://api.github.com/repos/ruemorgue76/WyreMonitor/releases/latest";
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub requires a User-Agent; it carries only the app's name and version.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public enum Outcome { UpToDate, NewVersion, Unavailable }

    public sealed record Result(Outcome Outcome, string? Version = null, string? PageUrl = null);

    public static async Task<Result> CheckAsync()
    {
        try
        {
            using var response = await Http.GetAsync(LatestApi);
            if (!response.IsSuccessStatusCode) return new(Outcome.Unavailable);
            var body = await response.Content.ReadAsStringAsync();
            if (body.Length > 512 * 1024) return new(Outcome.Unavailable);
            return Evaluate(body, AppInfo.Version);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(Outcome.Unavailable);
        }
    }

    /// <summary>Compares GitHub's answer with the running version.</summary>
    internal static Result Evaluate(string json, string current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var page = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || !Version.TryParse(current, out var running))
            return new(Outcome.Unavailable);
        // Only this repository's pages are ever opened.
        if (!page.StartsWith(RepoUrl + "/releases/", StringComparison.Ordinal)) page = RepoUrl + "/releases/latest";
        return latest > running ? new(Outcome.NewVersion, latest.ToString(3), page) : new(Outcome.UpToDate, latest.ToString(3));
    }

    public static void OpenPage(string url)
    {
        if (!url.StartsWith(RepoUrl + "/", StringComparison.Ordinal)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AppInfo.LogError(ex); }
    }

    /// <summary>The check the user asked for from the menu: always says what it found.</summary>
    public static async Task CheckAndTellAsync()
    {
        var result = await CheckAsync();
        switch (result.Outcome)
        {
            case Outcome.NewVersion:
                if (MessageBox.Show($"WyreMonitor {result.Version} is available (you have {AppInfo.Version}).\n\nOpen its download page?",
                        AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    OpenPage(result.PageUrl!);
                break;
            case Outcome.UpToDate:
                MessageBox.Show($"You have the latest version, {AppInfo.Version}.", AppInfo.Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
            default:
                MessageBox.Show("Couldn't reach the release page on GitHub. Check your internet connection and try again later.",
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
        }
    }
}
