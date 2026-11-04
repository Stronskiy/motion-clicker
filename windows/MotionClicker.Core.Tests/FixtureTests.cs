using System.Text.Json;
using MotionClicker.Fixture;
using Xunit;

public sealed class FixtureTests {
    [Fact] public void NegativeOriginUsesCanvasScaleWithoutClamping() {
        var g = new FixtureGeometry(-1920, -300, 1280, 720, 1.5);
        Assert.Equal((-1740d, -180d), g.NativePoint(120, 80));
        Assert.True(g.Contains(0, 0));
        foreach(var p in new[] { (1280d, 0d), (0d, 720d), (-1d, 0d), (double.NaN, 0d), (0d, double.PositiveInfinity) })
            Assert.False(g.Contains(p.Item1, p.Item2));
    }
    [Fact] public void LogIsExclusiveOrderedExactAndClosedWithoutChangingExistingBytes() {
        string path = Path.Combine(Path.GetTempPath(), $"synthetic-fixture-{Guid.NewGuid()}.jsonl");
        try {
            var g = new FixtureGeometry(-1920, -300, 1280, 720, 1.5);
            var journal = new FixtureJournal(path, "synthetic", "test", 10_000_000);
            journal.Append(9007199254740993, "down", g, 0, 120, 80);
            journal.Append(9007199254740994, "up", g, 0, 1400, 80);
            Assert.ThrowsAny<Exception>(() => journal.Append(9007199254740992, "marker", g));
            journal.Dispose();
            Assert.True(File.Exists(path));
            if(!File.Exists(path)) return;
            var bytes = File.ReadAllBytes(path);
            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            Assert.Contains("9007199254740993", lines[1]);
            using var row = JsonDocument.Parse(lines[2]);
            Assert.Equal(2, row.RootElement.GetProperty("sequence").GetInt64());
            Assert.False(row.RootElement.GetProperty("inside").GetBoolean());
            Assert.Equal(180, row.RootElement.GetProperty("nativeX").GetDouble());
            Assert.ThrowsAny<Exception>(() => new FixtureJournal(path, "again", "test", 1));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.ThrowsAny<Exception>(() => journal.Append(9007199254740995, "marker", g));
        } finally { File.Delete(path); }
    }
}
