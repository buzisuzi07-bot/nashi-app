using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Nashi.Core;

// Bir öğretici bölümü: başlık, açıklama ve kullanıcıyı yönlendiren eylem.
public sealed record TutorialStep(string Id, string Title, string Body, string? DemoAction = null)
{
    public string Key => Id;
}

/// Kullanıcının öğretici ilerlemesini %LocalAppData%\Nashi\tutorial.json'da saklar.
public sealed class TutorialStore
{
    private const int MaxSteps = 13;
    private readonly string file;private TutorialState state;
    public TutorialStore(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nashi");
        Directory.CreateDirectory(DirectoryPath);
        file = Path.Combine(DirectoryPath, "tutorial.json");
        if (!File.Exists(file)) { state = new TutorialState(new List<string>()); return; }
        try
        {
            if (new FileInfo(file).Length > 256 * 1024) throw new InvalidDataException("Tutorial too large.");
            var loaded = JsonSerializer.Deserialize<TutorialState>(File.ReadAllText(file)) ?? throw new InvalidDataException("Empty tutorial state.");
            state = new TutorialState(loaded.CompletedSteps.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList());
        }
        catch { state = new TutorialState(new List<string>()); }
    }
    public string DirectoryPath { get; }
    public IReadOnlyList<string> CompletedSteps => state.CompletedSteps;
    public bool IsCompleted(string stepId) => state.CompletedSteps.Contains(stepId);
    public int CompletedCount => state.CompletedSteps.Count;
    public bool IsComplete => CompletedCount >= MaxSteps;
    public void MarkCompleted(string stepId)
    {
        if (string.IsNullOrWhiteSpace(stepId) || state.CompletedSteps.Contains(stepId)) return;
        var next = new List<string>(state.CompletedSteps) { stepId };
        state = new TutorialState(next);
        Save();
    }
    public void Reset()
    {
        state = new TutorialState(new List<string>());
        Save();
    }
    private void Save()
    {
        try
        {
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, file, true);
        }
        catch { /* Tutorial kaydı başarısız olsa uygulama çökmesin. */ }
        finally { if (File.Exists(file + ".tmp")) File.Delete(file + ".tmp"); }
    }
    private sealed record TutorialState(List<string> CompletedSteps);
}
