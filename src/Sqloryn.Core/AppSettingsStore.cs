using System.Text.Json;

namespace Sqloryn.Core;

public sealed record AppSettings
{
    public const int MinFontSize = 8;
    public const int MaxFontSize = 48;
    public const int MinResultsMaxRows = 1;
    public const int MaxResultsMaxRows = 1_000_000;

    public double EditorFontSize { get; init; } = 13;
    public bool ShowLineNumbers { get; init; } = true;
    public int ResultsMaxRows { get; init; } = 10_000;
    public bool ConfirmDestructive { get; init; } = true;
    public string Theme { get; init; } = "System";

    public AppSettings Normalize()
    {
        var theme = Theme is "Light" or "Dark" or "System" ? Theme : "System";
        var size = double.IsNaN(EditorFontSize) ? 13 : Math.Clamp(EditorFontSize, MinFontSize, MaxFontSize);
        return this with
        {
            EditorFontSize = size,
            ResultsMaxRows = Math.Clamp(ResultsMaxRows, MinResultsMaxRows, MaxResultsMaxRows),
            Theme = theme
        };
    }
}

public sealed class AppSettingsStore
{
    readonly object _lock = new();
    readonly string _path;

    public AppSettingsStore(string? rootDirectory = null)
    {
        _path = Path.Combine(string.IsNullOrWhiteSpace(rootDirectory) ? AtomicFile.DefaultRoot() : rootDirectory, "settings.json");
    }

    public string FilePath => _path;

    /// <summary>Returns defaults when the file is missing or unreadable; settings are non-critical.</summary>
    public AppSettings Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
                return new AppSettings();
            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), AtomicFile.JsonOptions);
                return (settings ?? new AppSettings()).Normalize();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings.Normalize(), AtomicFile.JsonOptions);
        lock (_lock)
            AtomicFile.Write(_path, json);
    }
}
