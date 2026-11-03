using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MotionClicker.Core;
using Xunit;

namespace MotionClicker.Core.Tests;

public sealed class CoreTests
{
    private static string Fixture(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", file));
    private static MotionProject Sample() => new(1, "project", 0,
        [new("main", "Main", new(new(0, 0), new(1920, 1080), 2, 0))],
        [new("take", 1000, ["main"], [])], [new("track", "Track", "main", 0)],
        [new("clip", "track", "take", 0, 1000, 0, 1)], [Move("move", 100)]);
    private static RecordedAction Move(string id, long time, int order = 0) => new(id, "take", "main", time, order, ActionKind.Move, 100, 50);
    private static RecordedAction[] Click() =>
        [Move("down", 100) with { Kind = ActionKind.ButtonDown, Button = MouseButton.Left, AtomicGroupId = "hold" },
         Move("up", 200, 1) with { Kind = ActionKind.ButtonUp, Button = MouseButton.Left, AtomicGroupId = "hold" }];
    private static void Error(MotionProject p, ErrorCode code) => Assert.Equal(code, Assert.Throws<ProjectError>(() => RunPlanCompiler.Compile(p)).Code);

    [Fact]
    public void EverySharedProjectFixtureMatchesPlanOrTypedError()
    {
        using var manifest = JsonDocument.Parse(Fixture("cases.json"));
        foreach (var entry in manifest.RootElement.GetProperty("projectCases").EnumerateArray())
        {
            var p = ProjectJson.Deserialize<MotionProject>(Fixture(entry.GetProperty("input").GetString()!));
            if (entry.TryGetProperty("expectedPlan", out var file))
                Assert.Equal(ProjectJson.Deserialize<PlannedAction[]>(Fixture(file.GetString()!)), RunPlanCompiler.Compile(p));
            else
            {
                using var error = JsonDocument.Parse(Fixture(entry.GetProperty("expectedError").GetString()!));
                var actual = Assert.Throws<ProjectError>(() => RunPlanCompiler.Compile(p));
                Assert.Equal(error.RootElement.GetProperty("code").GetString(), JsonNamingPolicy.CamelCase.ConvertName(actual.Code.ToString()));
                Assert.Equal(error.RootElement.GetProperty("path").GetString(), actual.Path);
            }
        }
    }

    private sealed record GeometryCase(Size SourceSize, Point DisplayOrigin, Rect Viewport, Point Local,
        Point ExpectedDesktop, Point ExpectedPreview, Rect ExpectedFit, Point LetterboxPoint);
    [Fact]
    public void EverySharedGeometryFixtureMatchesHandDerivedCoordinates()
    {
        using var manifest = JsonDocument.Parse(Fixture("cases.json"));
        foreach (var file in manifest.RootElement.GetProperty("geometryCases").EnumerateArray())
        {
            var f = ProjectJson.Deserialize<GeometryCase>(Fixture(file.GetString()!));
            Assert.Equal(f.ExpectedDesktop, CoordinateMapper.DesktopPoint(f.Local, f.DisplayOrigin));
            Assert.Equal(f.ExpectedFit, CoordinateMapper.AspectFitRect(f.SourceSize, f.Viewport));
            Assert.Equal(f.ExpectedPreview, CoordinateMapper.PreviewPoint(f.Local, f.SourceSize, f.Viewport));
            Assert.Equal(f.Local, CoordinateMapper.LocalPoint(f.ExpectedPreview, f.SourceSize, f.Viewport));
            Assert.Throws<ProjectError>(() => CoordinateMapper.LocalPoint(f.LetterboxPoint, f.SourceSize, f.Viewport));
        }
    }

    public static IEnumerable<object[]> InvalidProjects()
    {
        var p = Sample();
        yield return [p with { SchemaVersion = 2 }, ErrorCode.UnsupportedVersion];
        yield return [p with { Revision = -1 }, ErrorCode.InvalidRange];
        foreach (var id in new[] { "", "bad:id", "é", "_first", "a/b", "a\n", new string('a', 129) })
            yield return [p with { Id = id }, ErrorCode.InvalidID];
        yield return [p with { Tracks = [p.Tracks[0] with { Id = "main" }] }, ErrorCode.DuplicateID];
        yield return [p with { Tracks = [p.Tracks[0] with { MonitorSlotId = "missing" }] }, ErrorCode.MissingReference];
        yield return [p with { Tracks = [p.Tracks[0] with { Order = -1 }] }, ErrorCode.InvalidRange];
        yield return [p with { Takes = [p.Takes[0] with { DurationUs = 0 }] }, ErrorCode.InvalidRange];
        yield return [p with { Takes = [p.Takes[0] with { MonitorSlotIds = [] }] }, ErrorCode.InvalidRange];
        yield return [p with { Takes = [p.Takes[0] with { MonitorSlotIds = ["main", "main"] }] }, ErrorCode.DuplicateID];
        yield return [p with { Takes = [p.Takes[0] with { MonitorSlotIds = ["missing"] }] }, ErrorCode.MissingReference];
        yield return [p with { Clips = [p.Clips[0] with { TrackId = "missing" }] }, ErrorCode.MissingReference];
        yield return [p with { Clips = [p.Clips[0] with { TakeId = "missing" }] }, ErrorCode.MissingReference];
        yield return [p with { Clips = [p.Clips[0] with { SourceInUs = -1 }] }, ErrorCode.InvalidRange];
        yield return [p with { Clips = [p.Clips[0] with { SourceOutUs = 0 }] }, ErrorCode.InvalidRange];
        yield return [p with { Clips = [p.Clips[0] with { SourceOutUs = 1001 }] }, ErrorCode.InvalidRange];
        yield return [p with { Clips = [p.Clips[0] with { TimelineStartUs = long.MaxValue }] }, ErrorCode.InvalidRange];
        yield return [p with { Clips = [p.Clips[0] with { TimelineStartUs = long.MinValue }] }, ErrorCode.InvalidRange];
        yield return [p with { Clips = [p.Clips[0] with { Speed = 2 }] }, ErrorCode.InvalidRange];
        yield return [p with { Clips = [p.Clips[0] with { LinkGroupId = "bad:link" }] }, ErrorCode.InvalidID];
        foreach (var geometry in new[] {
            new DisplayGeometry(new(0, 0), new(0, 1), 1, 0), new(new(double.PositiveInfinity, 0), new(1, 1), 1, 0),
            new(new(0, 0), new(1, 1), -1, 0), new(new(0, 0), new(1, 1), 1, 45),
            new(new(double.MaxValue, 0), new(double.MaxValue, 1), 1, 0) })
            yield return [p with { MonitorSlots = [p.MonitorSlots[0] with { Geometry = geometry }] }, ErrorCode.InvalidGeometry];
        foreach (var e in new[] { p.Events[0] with { TakeId = "missing" }, p.Events[0] with { MonitorSlotId = "missing" } })
            yield return [p with { Events = [e] }, ErrorCode.MissingReference];
        foreach (var t in new[] { -1L, 1000L }) yield return [p with { Events = [p.Events[0] with { TimeUs = t }] }, ErrorCode.InvalidRange];
        yield return [p with { Events = [p.Events[0] with { Order = -1 }] }, ErrorCode.InvalidRange];
        foreach (var point in new[] { new Point(-1, 0), new(1920, 0), new(0, 1080), new(double.NaN, 0), new(0, double.PositiveInfinity) })
            yield return [p with { Events = [p.Events[0] with { X = point.X, Y = point.Y }] }, ErrorCode.InvalidCoordinate];
        foreach (var e in new[] { p.Events[0] with { Kind = ActionKind.ButtonDown }, p.Events[0] with { Button = MouseButton.Left },
            p.Events[0] with { DeltaX = 1 }, p.Events[0] with { Kind = ActionKind.Scroll },
            p.Events[0] with { Kind = ActionKind.Scroll, DeltaX = 1, DeltaY = double.PositiveInfinity },
            p.Events[0] with { Kind = (ActionKind)99 } })
            yield return [p with { Events = [e] }, ErrorCode.InvalidAction];
        yield return [p with { Events = [Click()[0]] }, ErrorCode.InvalidAtomicGroup];
        yield return [p with { Events = [Click()[0], Click()[1] with { Button = MouseButton.Right }] }, ErrorCode.InvalidAtomicGroup];
        yield return [p with { Events = [Click()[0], Click()[1] with { TimeUs = 99 }] }, ErrorCode.InvalidAtomicGroup];
        yield return [p with { Events = [Move("orphan", 100) with { AtomicGroupId = "hold" }] }, ErrorCode.InvalidAtomicGroup];
        yield return [p with { Events = [.. Click(), Move("early", 99) with { AtomicGroupId = "hold" }] }, ErrorCode.InvalidAtomicGroup];
        foreach (var clip in new[] { p.Clips[0] with { SourceOutUs = 200 }, p.Clips[0] with { SourceInUs = 150 },
            p.Clips[0] with { SourceInUs = 150, SourceOutUs = 180 } })
            yield return [p with { Events = Click(), Clips = [clip] }, ErrorCode.AtomicGroupSplit];
        yield return [p with { Events = Click(), Clips = [p.Clips[0], p.Clips[0] with { Id = "clone", TimelineStartUs = 50 }] }, ErrorCode.MouseConflict];
        yield return [p with { Events = [.. Click(), Move("interleave", 150)] }, ErrorCode.MouseConflict];
        yield return [p with { Events = [.. Click(), Move("scroll", 150) with { Kind = ActionKind.Scroll, DeltaX = 1, DeltaY = 1 }] }, ErrorCode.MouseConflict];
        foreach (var path in new[] { "/tmp/v.mp4", "../v.mp4", "media/../v.mp4", "media//v.mp4", "media/./v.mp4", "C:\\v.mp4", "media/%2e/v.mp4", "https://x/v.mp4", "media/v.mp4\n" })
            yield return [p with { Takes = [p.Takes[0] with { Media = [new("main", path)] }] }, ErrorCode.UnsafeMediaPath];
    }
    [Theory, MemberData(nameof(InvalidProjects))]
    public void RejectsWholeProjectInvalidValues(MotionProject p, ErrorCode code) => Error(p, code);

    [Fact]
    public void ExactInt64MappingAndRoundTripAboveBinary64Precision()
    {
        const long large = 9007199254740993;
        var p = Sample() with { Revision = large, Takes = [new("take", long.MaxValue, ["main"], [])],
            Clips = [new("clip", "track", "take", large, large + 10, long.MaxValue - 10, 1)],
            Events = [Move("move", large + 1)] };
        var decoded = ProjectJson.Deserialize<MotionProject>(ProjectJson.Serialize(p));
        Assert.Equal(large, decoded.Revision);
        var action = Assert.Single(RunPlanCompiler.Compile(decoded));
        Assert.Equal(long.MaxValue - 9, action.TimeUs);
        Assert.Equal(large + 1, action.SourceTimeUs);
        Assert.Contains("9007199254740993", ProjectJson.Serialize(decoded));
    }

    [Fact]
    public void HalfOpenRangesAndReusedSourceIdentity()
    {
        var p = Sample() with { Clips = [new("clip", "track", "take", 100, 200, 1000, 1), new("clone", "track", "take", 100, 200, 2000, 1)],
            Events = [Move("before", 99), Move("first", 100), Move("last", 199), Move("outside", 200)] };
        var result = RunPlanCompiler.Compile(p);
        Assert.Equal(new[] { "clip:first", "clip:last", "clone:first", "clone:last" }, result.Select(a => a.Id));
        Assert.Equal(new long[] { 1000, 1099, 2000, 2099 }, result.Select(a => a.TimeUs));
        Assert.Equal(new long[] { 100, 199, 100, 199 }, result.Select(a => a.SourceTimeUs));
    }

    [Fact]
    public void OrdinalSchedulerIgnoresArrayPositionAndCurrentCulture()
    {
        var p = Sample() with { Events = [Move("a", 100), Move("Z", 100), Move("A", 100), Move("priority", 100, 0)] };
        p.Events[0] = p.Events[0] with { Order = 1 };
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(new[] { "A", "Z", "priority", "a" }, RunPlanCompiler.Compile(p).Select(a => a.SourceEventId));
            Assert.Equal(RunPlanCompiler.Compile(p), RunPlanCompiler.Compile(p with { Events = p.Events.Reverse().ToArray() }));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    private static MotionProject CrossDisplay()
    {
        var p = Sample();
        return p with { MonitorSlots = [.. p.MonitorSlots, p.MonitorSlots[0] with { Id = "other" }],
            Takes = [p.Takes[0] with { MonitorSlotIds = ["main", "other"] }],
            Tracks = [.. p.Tracks, new("track-other", "Other", "other", 1)],
            Clips = [p.Clips[0] with { LinkGroupId = "link" }, p.Clips[0] with { Id = "clip-other", TrackId = "track-other", LinkGroupId = "link" }],
            Events = [Click()[0], Move("drag", 150) with { AtomicGroupId = "hold", MonitorSlotId = "other" }, Click()[1] with { MonitorSlotId = "other" }] };
    }
    [Fact]
    public void LinkedCrossDisplayDragRetainsWholeAtomicGroupAndClones()
    {
        var p = CrossDisplay();
        Assert.Equal(new[] { "main", "other", "other" }, RunPlanCompiler.Compile(p).Select(a => a.MonitorSlotId));
        Assert.All(RunPlanCompiler.Compile(p), a => Assert.Equal("link:link:hold", a.AtomicGroupId));
        Error(p with { Clips = [p.Clips[0]] }, ErrorCode.AtomicGroupSplit);
        Error(p with { Clips = [p.Clips[0], p.Clips[1] with { TimelineStartUs = 1 }] }, ErrorCode.InvalidRange);
        Error(p with { Clips = [p.Clips[0], p.Clips[1] with { TrackId = "track" }] }, ErrorCode.InvalidRange);
        var clones = p.Clips.Select(c => c with { Id = c.Id + "-clone", LinkGroupId = "clone", TimelineStartUs = 2000 }).ToArray();
        var plan = RunPlanCompiler.Compile(p with { Clips = [.. p.Clips, .. clones] });
        Assert.Equal(6, plan.Count);
        Assert.Equal("link:clone:hold", plan[3].AtomicGroupId);
    }
    [Fact]
    public void EqualSourceTimeIsOrderedAndPlanOwnershipStillApplies()
    {
        var p = CrossDisplay() with { Events = [Click()[0], Click()[1] with { TimeUs = 100, MonitorSlotId = "other" }] };
        Assert.Equal(new[] { ActionKind.ButtonDown, ActionKind.ButtonUp }, RunPlanCompiler.Compile(p).Select(a => a.Kind));
        Error(p with { Tracks = [p.Tracks[0] with { Order = 1 }, p.Tracks[1] with { Order = 0 }] }, ErrorCode.MouseConflict);
        Error(p with { Events = [p.Events[0] with { Order = 2 }, p.Events[1]] }, ErrorCode.InvalidAtomicGroup);
    }
    [Fact]
    public void DisjointTrimOmitsHoldAndSeparateTakeGroupIsRejected()
    {
        var p = Sample() with { Events = Click() };
        Assert.Empty(RunPlanCompiler.Compile(p with { Clips = [p.Clips[0] with { SourceInUs = 201 }] }));
        Error(p with { Takes = [.. p.Takes, new("second", 1000, ["main"], [])], Events = [p.Events[0], p.Events[1] with { TakeId = "second" }] }, ErrorCode.InvalidAtomicGroup);
    }

    [Theory]
    [InlineData("\"move\"", "\"Move\"")]
    [InlineData("\"move\"", "\"teleport\"")]
    [InlineData("\"move\"", "0")]
    [InlineData("\"revision\":0", "\"revision\":9223372036854775808")]
    [InlineData("\"revision\":0", "\"revision\":\"0\"")]
    [InlineData("\"revision\":0", "\"revision\":1.1")]
    public void WireRejectsWrongTypesEnumsAndOverflow(string from, string to) =>
        Assert.Throws<JsonException>(() => ProjectJson.Deserialize<MotionProject>(ProjectJson.Serialize(Sample()).Replace(from, to, StringComparison.Ordinal)));

    [Theory]
    [InlineData("1.0", 1L)]
    [InlineData("1e0", 1L)]
    [InlineData("9007199254740993.0", 9007199254740993L)]
    [InlineData("9.223372036854775807e18", long.MaxValue)]
    [InlineData("0e999999999999999999999999", 0L)]
    public void IntegralJsonNumbersDecodeExactlyWithoutBinary64(string literal, long expected)
    {
        var json = ProjectJson.Serialize(Sample()).Replace("\"revision\":0", "\"revision\":" + literal, StringComparison.Ordinal);
        Assert.Equal(expected, ProjectJson.Deserialize<MotionProject>(json).Revision);
    }

    [Fact]
    public void WireRequiresAllFieldsAndRejectsNullReferencesIncludingCollectionElements()
    {
        void Check(JsonNode node)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj.ToArray())
                {
                    var saved = pair.Value!.DeepClone();
                    obj.Remove(pair.Key);
                    Assert.Throws<JsonException>(() => ProjectJson.Deserialize<MotionProject>(node.Root.ToJsonString()));
                    obj[pair.Key] = null;
                    Assert.Throws<JsonException>(() => ProjectJson.Deserialize<MotionProject>(node.Root.ToJsonString()));
                    obj[pair.Key] = saved;
                    Check(obj[pair.Key]!);
                }
            else if (node is JsonArray array)
                for (var i = 0; i < array.Count; i++)
                {
                    var saved = array[i]!.DeepClone();
                    array[i] = null;
                    Assert.Throws<JsonException>(() => ProjectJson.Deserialize<MotionProject>(node.Root.ToJsonString()));
                    array[i] = saved;
                    Check(array[i]!);
                }
        }
        Check(JsonNode.Parse(ProjectJson.Serialize(Sample() with { Takes = [Sample().Takes[0] with { Media = [new("main", "media/main.mp4")] }] }))!);
    }
    [Fact]
    public void TypedErrorWireContainsOnlyContractFieldsAndRoundTrips()
    {
        var error = new ProjectError(ErrorCode.InvalidRange, "clips.clip", "Invalid range.");
        const string expected = "{\"code\":\"invalidRange\",\"path\":\"clips.clip\",\"message\":\"Invalid range.\"}";
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(ProjectJson.Serialize(error))));
        var decoded = ProjectJson.Deserialize<ProjectError>(expected);
        Assert.Equal(error.Code, decoded.Code);
        Assert.Equal(error.Path, decoded.Path);
        Assert.Equal(error.Message, decoded.Message);
    }

    [Fact]
    public void OptionalNullAndUnknownMetadataRoundTripKnownFields()
    {
        var node = JsonNode.Parse(ProjectJson.Serialize(Sample()))!;
        node["metadata"] = new JsonObject { ["future"] = true };
        node["monitorSlots"]![0]!["deviceHint"] = null;
        node["events"]![0]!["button"] = null;
        node["events"]![0]!["atomicGroupId"] = null;
        var p = ProjectJson.Deserialize<MotionProject>(node.ToJsonString());
        Assert.Single(RunPlanCompiler.Compile(p));
        Assert.DoesNotContain("metadata", ProjectJson.Serialize(p));
        Assert.DoesNotContain("deviceHint", ProjectJson.Serialize(p));
    }

    [Fact]
    public void GeometryHandlesPortraitRoundTripAndRejectsBarsEdgesAndOverflow()
    {
        var size = new Size(100, 200);
        var view = new Rect(new(-10, 30), new(200, 100));
        Assert.Equal(new Rect(new(65, 30), new(50, 100)), CoordinateMapper.AspectFitRect(size, view));
        var local = new Point(31.25, 72.125);
        var back = CoordinateMapper.LocalPoint(CoordinateMapper.PreviewPoint(local, size, view), size, view);
        Assert.Equal(local.X, back.X, 10); Assert.Equal(local.Y, back.Y, 10);
        Assert.Throws<ProjectError>(() => CoordinateMapper.PreviewPoint(new(100, 0), size, view));
        Assert.Throws<ProjectError>(() => CoordinateMapper.LocalPoint(new(115, 80), size, view));
        Assert.Throws<ProjectError>(() => CoordinateMapper.LocalPoint(new(0, 80), size, view));
        Assert.Throws<ProjectError>(() => CoordinateMapper.DesktopPoint(new(double.MaxValue, 0), new(double.MaxValue, 0)));
        Assert.Throws<ProjectError>(() => CoordinateMapper.AspectFitRect(new(0, 1), view));
        Assert.Throws<ProjectError>(() => CoordinateMapper.AspectFitRect(new(double.Epsilon, double.MaxValue), view));
        Assert.Throws<ProjectError>(() => CoordinateMapper.AspectFitRect(size, new(new(double.NaN, 0), size)));
    }
}
