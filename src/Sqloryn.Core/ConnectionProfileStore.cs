using System.Text.Json;

namespace Sqloryn.Core;

/// <summary>Non-secret connection settings. Passwords are never part of a profile.</summary>
public sealed record ConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Server { get; init; } = "";
    public int Port { get; init; } = 1433;
    public string Database { get; init; } = "master";
    public string UserId { get; init; } = "";
    public bool Encrypt { get; init; } = true;
    public bool TrustServerCertificate { get; init; }
    public string? ServerCertificatePath { get; init; }
    public bool RememberPassword { get; init; }
    public DateTimeOffset? LastUsedUtc { get; init; }
}

public sealed class ConnectionProfileStore
{
    readonly object _lock = new();
    readonly string _path;

    public ConnectionProfileStore(string? rootDirectory = null)
    {
        _path = Path.Combine(string.IsNullOrWhiteSpace(rootDirectory) ? AtomicFile.DefaultRoot() : rootDirectory, "profiles.json");
    }

    public string FilePath => _path;

    public IReadOnlyList<ConnectionProfile> Load()
    {
        lock (_lock)
            return LoadCore();
    }

    public void Save(IEnumerable<ConnectionProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var list = profiles.ToList();
        foreach (var profile in list)
            ValidateProfile(profile);
        if (list.Select(p => p.Id).Distinct().Count() != list.Count)
            throw new ArgumentException("Duplicate profile id.");
        lock (_lock)
            SaveCore(list);
    }

    public void Upsert(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProfile(profile);
        lock (_lock)
        {
            var list = LoadCore().ToList();
            var index = list.FindIndex(p => p.Id == profile.Id);
            if (index >= 0)
                list[index] = profile;
            else
                list.Add(profile);
            SaveCore(list);
        }
    }

    public bool Remove(Guid id)
    {
        lock (_lock)
        {
            var list = LoadCore().ToList();
            var removed = list.RemoveAll(p => p.Id == id) > 0;
            if (removed)
                SaveCore(list);
            return removed;
        }
    }

    public IReadOnlyList<ConnectionProfile> ListOrderedByRecent()
        => Load()
            .OrderByDescending(p => p.LastUsedUtc.HasValue)
            .ThenByDescending(p => p.LastUsedUtc)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    List<ConnectionProfile> LoadCore()
    {
        if (!File.Exists(_path))
            return [];
        try
        {
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
                return [];
            var file = JsonSerializer.Deserialize<ProfilesFile>(json, AtomicFile.JsonOptions);
            return file?.Profiles?.Where(p => p is not null).ToList() ?? [];
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Connection profile file is not valid JSON.");
        }
    }

    void SaveCore(List<ConnectionProfile> profiles)
    {
        var json = JsonSerializer.Serialize(new ProfilesFile { Version = 1, Profiles = profiles }, AtomicFile.JsonOptions);
        AtomicFile.Write(_path, json);
    }

    static void ValidateProfile(ConnectionProfile profile)
    {
        if (profile.Id == Guid.Empty)
            throw new ArgumentException("Profile id is required.");
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 128)
            throw new ArgumentException("Profile name is required.");
    }

    sealed class ProfilesFile
    {
        public int Version { get; set; } = 1;
        public List<ConnectionProfile> Profiles { get; set; } = [];
    }
}
