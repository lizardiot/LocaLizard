using System.Text.Json;

namespace LocaLizard.Core;

public sealed record BuildJournalEntry(DateTimeOffset At, string Stage, string Status, string? Detail);
public sealed record BuildJournalDocument(
    int SchemaVersion,
    string ApplicationVersion,
    string SourceIso,
    string OutputIso,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<BuildJournalEntry> Entries);

public sealed class BuildJournal
{
    private readonly string _path;
    private readonly string _source;
    private readonly string _output;
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private readonly List<BuildJournalEntry> _entries = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BuildJournal(string path, string source, string output)
    {
        _path = Path.GetFullPath(path);
        _source = Path.GetFullPath(source);
        _output = Path.GetFullPath(output);
    }

    public async Task RecordAsync(string stage, string status, string? detail = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _entries.Add(new BuildJournalEntry(DateTimeOffset.Now, stage, status, detail));
            var document = new BuildJournalDocument(1, AppInfo.Version, _source, _output,
                _started, DateTimeOffset.Now, _entries.ToArray());
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }), ct);
            File.Move(temporary, _path, true);
        }
        finally { _gate.Release(); }
    }
}