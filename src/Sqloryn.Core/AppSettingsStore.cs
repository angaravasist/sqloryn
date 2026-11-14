using System.Text.Json;

namespace Sqloryn.Core;

public sealed record AppSettings
{
    public const int MinFontSize = 8;
    public const int MaxFontSize = 48;
    public const int MinResultsMaxRows = 1;
    /// <summary>The engine never retains more than this per result set, so the display preference cannot exceed it.</summary>
    public const int MaxResultsMaxRows = QueryLimits.MaxRowsPerSet;
    public const double DefaultSidebarWidth = 240;
    public const double MinSidebarWidth = 180;
    public const double MaxSidebarWidth = 480;
    public const int MinConnectTimeoutSeconds = 1;
    public const int MaxConnectTimeoutSeconds = 120;
    public const int DefaultConnectTimeoutSeconds = 15;
    public const int MinPrivateDataRetentionDays = 1;
    public const int MaxPrivateDataRetentionDays = 365;
    public const int DefaultPrivateDataRetentionDays = 30;

    public double EditorFontSize { get; init; } = 13;
    public int ResultsMaxRows { get; init; } = QueryLimits.MaxRowsPerSet;
    public bool ConfirmDestructive { get; init; } = true;
    /// <summary>"System" follows the live OS appearance; "Light" and "Dark" override it.</summary>
    public string Theme { get; init; } = "System";
    /// <summary>Object Explorer width in device-independent pixels.</summary>
    public double SidebarWidth { get; init; } = DefaultSidebarWidth;
    public bool SidebarVisible { get; init; } = true;
    /// <summary>Results pane height in device-independent pixels; 0 means "use the default split".</summary>
    public double ResultsHeight { get; init; }
    /// <summary>Client connect timeout applied the next time a session connects. Not a server setting.</summary>
    public int ConnectTimeoutSeconds { get; init; } = DefaultConnectTimeoutSeconds;
    /// <summary>On by default: copy unsaved query tabs to Application Support so a relaunch can offer them back.</summary>
    public bool KeepUnsavedCopies { get; init; } = true;
    /// <summary>On by default: remember recent batch text, outcome and elapsed time.</summary>
    public bool KeepExecutionHistory { get; init; } = true;
    /// <summary>Unsaved copies and history entries older than this are dropped.</summary>
    public int PrivateDataRetentionDays { get; init; } = DefaultPrivateDataRetentionDays;

    public AppSettings Normalize()
    {
        var theme = Theme is "Light" or "Dark" or "System" ? Theme : "System";
        var size = double.IsNaN(EditorFontSize) ? 13 : Math.Clamp(EditorFontSize, MinFontSize, MaxFontSize);
        var sidebar = double.IsFinite(SidebarWidth) ? Math.Clamp(SidebarWidth, MinSidebarWidth, MaxSidebarWidth) : DefaultSidebarWidth;
        var results = double.IsFinite(ResultsHeight) ? Math.Clamp(ResultsHeight, 0, 4000) : 0;
        return this with
        {
            EditorFontSize = size,
            ResultsMaxRows = Math.Clamp(ResultsMaxRows, MinResultsMaxRows, MaxResultsMaxRows),
            Theme = theme,
            SidebarWidth = sidebar,
            ResultsHeight = results,
            ConnectTimeoutSeconds = Math.Clamp(ConnectTimeoutSeconds, MinConnectTimeoutSeconds, MaxConnectTimeoutSeconds),
            PrivateDataRetentionDays = Math.Clamp(PrivateDataRetentionDays, MinPrivateDataRetentionDays, MaxPrivateDataRetentionDays)
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
