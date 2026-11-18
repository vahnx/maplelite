using MapleLite.Plugins.Loot;
using MapleLite.Sdk;
using Xunit;

namespace MapleLite.Tests;

public class DailyFilesTests
{
    static string TempDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ml-daily-" + Guid.NewGuid().ToString("N"))).FullName;

    // Noon UTC is the same calendar day in every time zone from UTC-11 to UTC+11.
    static string Loot(int day, string text) =>
        $"{{\"Time\":\"2026-11-{day:00}T12:00:00+00:00\",\"Kind\":\"Mesos\",\"Name\":\"Mesos\",\"Quantity\":5,\"Text\":\"{text}\",\"Source\":\"chat\"}}";

    [Fact]
    public void Monthly_files_are_split_into_days_and_kept_as_a_backup()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllLines(Path.Combine(dir, "loot-2026-11.jsonl"), [Loot(3, "a"), Loot(3, "b"), Loot(17, "c"), "not json"]);
            File.WriteAllText(Path.Combine(dir, "loot-2026-11.jsonl.bak-kpq"), "left alone");

            var log = new LootLog(dir);

            Assert.Equal(3, log.Records.Count);
            Assert.Equal(1, log.UnreadableLines);
            Assert.Equal(2, File.ReadAllLines(Path.Combine(dir, "loot-2026-11-03.jsonl")).Length);
            Assert.Single(File.ReadAllLines(Path.Combine(dir, "loot-2026-11-17.jsonl")));
            Assert.Equal(["not json"], File.ReadAllLines(Path.Combine(dir, "loot-2026-11-unsorted.jsonl")));
            Assert.False(File.Exists(Path.Combine(dir, "loot-2026-11.jsonl")));
            Assert.Equal(4, File.ReadAllLines(Path.Combine(dir, DailyFiles.MonthlyBackupDir, "loot-2026-11.jsonl")).Length);
            Assert.True(File.Exists(Path.Combine(dir, "loot-2026-11.jsonl.bak-kpq")));

            // Opening again changes nothing, and new records go to their own day's file.
            Assert.Equal(3, new LootLog(dir).Records.Count);
            log.Add(new LootRecord(DateTimeOffset.Parse("2026-11-18T12:00:00Z"), "Mesos", "Mesos", 9, "d", "chat"));
            Assert.True(File.Exists(Path.Combine(dir, "loot-2026-11-18.jsonl")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_split_that_was_interrupted_adds_nothing_twice()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllLines(Path.Combine(dir, "loot-2026-11.jsonl"), [Loot(3, "a"), Loot(4, "b")]);
            File.WriteAllLines(Path.Combine(dir, "loot-2026-11-03.jsonl"), [Loot(3, "a")]); // already copied before a crash
            Assert.Equal(2, new LootLog(dir).Records.Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}
