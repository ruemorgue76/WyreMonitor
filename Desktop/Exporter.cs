using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace WyreMonitor.Desktop;

/// <summary>
/// Saves the last 15 minutes of every sensor as a CSV file: one row per second, one column per
/// sensor, with the user's names and temperature unit. The window can't download files (it is
/// locked down), so the page asks the app, and the user picks the file in the usual dialog.
/// </summary>
internal static class Exporter
{
    public static async Task ExportAsync(IWin32Window owner, PageSettings page)
    {
        try
        {
            var url = await ServiceLink.FindUrlAsync();
            if (url is null)
            {
                MessageBox.Show(owner, "The WyreMonitor service isn't responding, so there is nothing to export.",
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var json = await ServiceLink.GetStringAsync($"{url}/api/export");
            var csv = BuildCsv(json, page);

            using var dialog = new SaveFileDialog
            {
                Title = "Export the last 15 minutes",
                Filter = "CSV file (*.csv)|*.csv",
                FileName = $"WyreMonitor {DateTime.Now:yyyy-MM-dd HH.mm}.csv",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(owner) != DialogResult.OK) return;
            // With a byte order mark, so Excel reads the degree sign correctly.
            await File.WriteAllTextAsync(dialog.FileName, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex)
        {
            AppInfo.LogError(ex);
            MessageBox.Show(owner, $"The export failed.\n\n{ex.Message}", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal static string BuildCsv(string json, PageSettings page)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var end = root.GetProperty("timestamp").GetDateTimeOffset().ToLocalTime();
        var fahrenheit = page.Unit == "F";

        var columns = new List<(string Header, double[] Values, bool Temperature)>();
        foreach (var s in root.GetProperty("sensors").EnumerateArray())
        {
            var id = s.GetProperty("id").GetString() ?? "";
            var group = s.GetProperty("group").GetString() ?? "";
            var name = page.Names.TryGetValue(id, out var custom) ? custom : s.GetProperty("name").GetString() ?? "";
            var type = s.GetProperty("type").GetString() ?? "";
            var temperature = type == "Temperature";
            var unit = temperature && fahrenheit ? "°F" : s.GetProperty("unit").GetString() ?? "";
            var values = s.GetProperty("history").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            if (values.Length == 0) continue;
            columns.Add(($"{group} / {name}{(unit.Length > 0 ? $" [{unit}]" : "")}", values, temperature));
        }

        var rows = columns.Count == 0 ? 0 : columns.Max(c => c.Values.Length);
        var sb = new StringBuilder();
        sb.Append("Time");
        foreach (var c in columns) sb.Append(',').Append(Quote(c.Header));
        sb.Append("\r\n");
        for (var r = 0; r < rows; r++)
        {
            // One sample per second, the last one at the reading's time; shorter histories are
            // sensors that appeared later, so they are aligned to the end.
            sb.Append(end.AddSeconds(r - rows + 1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            foreach (var c in columns)
            {
                sb.Append(',');
                var i = r - (rows - c.Values.Length);
                if (i < 0) continue;
                var v = c.Temperature && fahrenheit ? c.Values[i] * 9 / 5 + 32 : c.Values[i];
                sb.Append(Math.Round(v, 2).ToString(CultureInfo.InvariantCulture));
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    // Quotes always, doubles inner quotes, and keeps a spreadsheet from reading a name such as
    // "=cmd|..." as a formula.
    private static string Quote(string s)
    {
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return $"\"{s.Replace("\"", "\"\"")}\"";
    }
}
