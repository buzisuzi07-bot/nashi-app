using System;
using System.IO;
using System.Text.Json;

namespace Nashi.Core;

// Crash durumunda uygulama durumunu ve exception detaylarını kaydeder.
// Kullanıcıya "Uygulama çöktü, yeniden başlatılsın mı?" sorulur; evetse yeni örnek başlatılır.
public sealed record CrashReport(Guid Id, DateTimeOffset OccurredAt, string ExceptionType, string Message, string StackTrace, string? SourceFile, int? SourceLine, string? AppVersion, string[]? RecentFiles)
{
    public string FileName => $"crash-{Id:N}.json";
}

/// Crash guard: çöktüğü anı kaydeder ve kullanıcıya yeniden başlatma seçeneği sunar.
public static class CrashGuard
{
    private const int MaxReports = 25;

    public static CrashReport Record(Exception exception, string? sourceFile = null, int? sourceLine = null, string[]? recentFiles = null)
    {
        var report = new CrashReport(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            exception.StackTrace ?? "",
            sourceFile,
            sourceLine,
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
            recentFiles
        );
        Save(report);
        return report;
    }

    private static void Save(CrashReport report)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nashi", "crashes");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, report.FileName);
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

            // Eski raporları temizle (en son 25 tanesini tut)
            var all = Directory.GetFiles(dir, "crash-*.json");
            if (all.Length > MaxReports)
            {
                foreach (var old in all.OrderByDescending(f => File.GetCreationTime(f)).Skip(MaxReports))
                {
                    try { File.Delete(old); } catch { }
                }
            }
        }
        catch { /* Crash raporu yazılmasa da uygulama çökmesin. */ }
    }

    public static CrashReport[] GetAll()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nashi", "crashes");
            if (!Directory.Exists(dir)) return Array.Empty<CrashReport>();
            var reports = new System.Collections.Generic.List<CrashReport>();
            foreach (var file in Directory.GetFiles(dir, "crash-*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var report = JsonSerializer.Deserialize<CrashReport>(json);
                    if (report is not null) reports.Add(report);
                }
                catch { }
            }
            reports.Sort((a, b) => b.OccurredAt.CompareTo(a.OccurredAt));
            return reports.ToArray();
        }
        catch { return Array.Empty<CrashReport>(); }
    }
}
