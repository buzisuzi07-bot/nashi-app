using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Nashi.Core;

public enum QueueStatus { Pending, Converting, Done, Failed }

/// A single item waiting to be converted. Kept in %LocalAppData%\Nashi so it
/// survives restarts like the Library.
public sealed record QueueEntry(Guid Id, DateTimeOffset Created, string Action, string SourcePath, string? OutputPath, QueueStatus Status)
{
    public string Name => Path.GetFileName(SourcePath);
}

/// Queue of conversions waiting to run. Entries are persisted to queue.json so
/// a restart does not lose them. Only one entry is converting at a time.
public sealed class QueueStore
{
    public const int Limit = 100;
    public string DirectoryPath { get; }
    public IReadOnlyList<QueueEntry> Entries => state.Entries;
    private State state = new([]);
    private string FilePath => Path.Combine(DirectoryPath, "queue.json");
    private sealed record State(List<QueueEntry> Entries);

    public QueueStore(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nashi");
        if (!File.Exists(FilePath)) return;
        try
        {
            if (new FileInfo(FilePath).Length > 4 * 1024 * 1024) throw new InvalidDataException("Queue is too large.");
            var loaded = JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Empty queue.");
            if (loaded.Entries is null || loaded.Entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.SourcePath))) throw new InvalidDataException("Invalid queue entry.");
            state = new(loaded.Entries.OrderByDescending(e => e.Created).Take(Limit).ToList());
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            try { File.Copy(FilePath, FilePath + ".recovered-" + Guid.NewGuid().ToString("N")); }
            catch { }
            state = new([]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { state = new([]); }
    }

    public void Enqueue(string action, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return;
        state = new(new[] { new QueueEntry(Guid.NewGuid(), DateTimeOffset.Now, action, sourcePath, null, QueueStatus.Pending) }.Concat(state.Entries).Take(Limit).ToList());
        Save();
    }

    public QueueEntry? Dequeue()
    {
        var pending = state.Entries.FirstOrDefault(e => e.Status == QueueStatus.Pending);
        if (pending is null) return null;
        state = new(state.Entries.Select(e => e.Id == pending.Id ? pending with { Status = QueueStatus.Converting } : e).ToList());
        Save();
        return pending;
    }

    public void Complete(Guid id, string? outputPath)
    {
        state = new(state.Entries.Select(e => e.Id == id ? e with { Status = QueueStatus.Done, OutputPath = outputPath } : e).ToList());
        Save();
    }

    public void Fail(Guid id)
    {
        state = new(state.Entries.Select(e => e.Id == id ? e with { Status = QueueStatus.Failed } : e).ToList());
        Save();
    }

    public void Cancel(Guid id)
    {
        state = new(state.Entries.Where(e => e.Id != id).ToList());
        Save();
    }

    public int PendingCount => state.Entries.Count(e => e.Status == QueueStatus.Pending || e.Status == QueueStatus.Converting);

    private void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(state)); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
