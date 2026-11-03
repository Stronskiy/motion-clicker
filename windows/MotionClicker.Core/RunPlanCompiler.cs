namespace MotionClicker.Core;

/// <summary>Pure whole-project validation and deterministic compilation. Never accesses OS input or media.</summary>
public static class RunPlanCompiler
{
    public static IReadOnlyList<PlannedAction> Compile(MotionProject project)
    {
        var validated = new ValidatedProject(project);
        var result = new List<PlannedAction>();
        var occurrences = project.Clips.GroupBy(c => c.LinkGroupId is { } link ? "link:" + link : "clip:" + c.Id,
            StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var occurrence in occurrences)
        {
            var first = occurrence.First();
            var monitors = new HashSet<string>(StringComparer.Ordinal);
            foreach (var clip in occurrence)
            {
                var monitor = validated.Tracks[clip.TrackId].MonitorSlotId;
                Require(clip.TakeId == first.TakeId && clip.SourceInUs == first.SourceInUs &&
                    clip.SourceOutUs == first.SourceOutUs && clip.TimelineStartUs == first.TimelineStartUs && monitors.Add(monitor),
                    ErrorCode.InvalidRange, "clips." + clip.Id, "Linked clips need identical take/range/offset and distinct monitors.");
            }
            foreach (var (id, group) in validated.Groups.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                if (group.Down.TakeId != first.TakeId || !group.Events.Any(e => monitors.Contains(e.MonitorSlotId)) ||
                    group.Down.TimeUs >= first.SourceOutUs || group.Up.TimeUs < first.SourceInUs) continue;
                Require(group.Events.All(e => monitors.Contains(e.MonitorSlotId) && e.TimeUs >= first.SourceInUs && e.TimeUs < first.SourceOutUs),
                    ErrorCode.AtomicGroupSplit, "clips." + first.Id, $"Selection splits atomic group {id}.");
            }
            foreach (var clip in occurrence)
            {
                var track = validated.Tracks[clip.TrackId];
                foreach (var e in project.Events.Where(e => e.TakeId == clip.TakeId && e.MonitorSlotId == track.MonitorSlotId &&
                    e.TimeUs >= clip.SourceInUs && e.TimeUs < clip.SourceOutUs))
                {
                    var group = e.AtomicGroupId is { } id
                        ? clip.LinkGroupId is { } link ? $"link:{link}:{id}" : $"{clip.Id}:{id}" : null;
                    result.Add(new($"{clip.Id}:{e.Id}", e.Id, clip.Id, e.TakeId, e.MonitorSlotId,
                        checked(clip.TimelineStartUs + (e.TimeUs - clip.SourceInUs)), e.TimeUs, track.Order, e.Order,
                        e.Kind, e.X, e.Y, e.Button, group, e.DeltaX, e.DeltaY));
                }
            }
        }
        var sorted = result.OrderBy(e => e.TimeUs).ThenBy(e => e.TrackOrder).ThenBy(e => e.EventOrder)
            .ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
        string? owner = null;
        foreach (var action in sorted)
        {
            var conflict = action.Kind switch
            {
                ActionKind.ButtonDown => owner is not null,
                ActionKind.ButtonUp or ActionKind.Move => owner != action.AtomicGroupId,
                ActionKind.Scroll => owner is not null,
                _ => true
            };
            Require(!conflict, ErrorCode.MouseConflict, "plan." + action.Id, "One mouse cannot interleave unrelated groups or movement.");
            if (action.Kind == ActionKind.ButtonDown) owner = action.AtomicGroupId;
            if (action.Kind == ActionKind.ButtonUp) owner = null;
        }
        Require(owner is null, ErrorCode.MouseConflict, "plan", "Plan ends with a held button.");
        // Do not expose the mutable construction array as the returned snapshot collection.
        return Array.AsReadOnly(sorted);
    }

    private sealed record AtomicGroup(RecordedAction Down, RecordedAction Up, RecordedAction[] Events);
    private sealed class ValidatedProject
    {
        public Dictionary<string, Track> Tracks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, AtomicGroup> Groups { get; } = new(StringComparer.Ordinal);
        public ValidatedProject(MotionProject project)
        {
            Require(project.SchemaVersion == 1, ErrorCode.UnsupportedVersion, "schemaVersion", "Only version 1 is supported.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            void Register(string id, string path)
            {
                ValidateID(id, path);
                Require(ids.Add(id), ErrorCode.DuplicateID, path, "Entity IDs must be globally unique.");
            }
            Register(project.Id, "id");
            Require(project.Revision >= 0, ErrorCode.InvalidRange, "revision", "Revision cannot be negative.");
            var monitors = new Dictionary<string, MonitorSlot>(StringComparer.Ordinal);
            for (var i = 0; i < project.MonitorSlots.Length; i++)
            {
                var monitor = project.MonitorSlots[i]; var path = $"monitorSlots[{i}]";
                Register(monitor.Id, path + ".id");
                var g = monitor.Geometry;
                Require(CoordinateMapper.Valid(new(g.Origin, g.Size)) && double.IsFinite(g.ScaleFactor) && g.ScaleFactor > 0 &&
                    g.RotationDegrees is 0 or 90 or 180 or 270, ErrorCode.InvalidGeometry, path + ".geometry", "Invalid recorded geometry.");
                monitors.Add(monitor.Id, monitor);
            }
            var takes = new Dictionary<string, Take>(StringComparer.Ordinal);
            for (var i = 0; i < project.Takes.Length; i++)
            {
                var take = project.Takes[i]; var path = $"takes[{i}]";
                Register(take.Id, path + ".id");
                Require(take.DurationUs > 0 && take.MonitorSlotIds.Length > 0, ErrorCode.InvalidRange, path, "Take needs duration and monitors.");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var j = 0; j < take.MonitorSlotIds.Length; j++)
                {
                    var id = take.MonitorSlotIds[j]; var reference = path + $".monitorSlotIds[{j}]";
                    Require(monitors.ContainsKey(id), ErrorCode.MissingReference, reference, "Unknown monitor.");
                    Require(seen.Add(id), ErrorCode.DuplicateID, reference, "Repeated take monitor.");
                }
                var mediaMonitors = new HashSet<string>(StringComparer.Ordinal);
                for (var j = 0; j < take.Media.Length; j++)
                {
                    var media = take.Media[j]; var mp = path + $".media[{j}]";
                    Require(seen.Contains(media.MonitorSlotId), ErrorCode.MissingReference, mp + ".monitorSlotId", "Media monitor must be in take.");
                    Require(mediaMonitors.Add(media.MonitorSlotId), ErrorCode.DuplicateID, mp + ".monitorSlotId", "Repeated media monitor.");
                    Require(SafeMediaPath(media.RelativePath), ErrorCode.UnsafeMediaPath, mp + ".relativePath", "Expected plain path under media/.");
                }
                takes.Add(take.Id, take);
            }
            for (var i = 0; i < project.Tracks.Length; i++)
            {
                var track = project.Tracks[i]; var path = $"tracks[{i}]";
                Register(track.Id, path + ".id");
                Require(monitors.ContainsKey(track.MonitorSlotId), ErrorCode.MissingReference, path + ".monitorSlotId", "Unknown monitor.");
                ValidateOrder(track.Order, path + ".order");
                Tracks.Add(track.Id, track);
            }
            for (var i = 0; i < project.Clips.Length; i++)
            {
                var clip = project.Clips[i]; var path = $"clips[{i}]";
                Register(clip.Id, path + ".id");
                if (clip.LinkGroupId is { } link) ValidateID(link, path + ".linkGroupId");
                if (!Tracks.TryGetValue(clip.TrackId, out var track)) throw new ProjectError(ErrorCode.MissingReference, path + ".trackId", "Unknown track.");
                if (!takes.TryGetValue(clip.TakeId, out var take)) throw new ProjectError(ErrorCode.MissingReference, path + ".takeId", "Unknown take.");
                Require(take.MonitorSlotIds.Contains(track.MonitorSlotId, StringComparer.Ordinal), ErrorCode.MissingReference, path + ".takeId", "Track monitor absent in take.");
                Require(clip.SourceInUs >= 0 && clip.SourceOutUs > clip.SourceInUs && clip.SourceOutUs <= take.DurationUs &&
                    clip.TimelineStartUs >= 0 && clip.Speed == 1, ErrorCode.InvalidRange, path, "Invalid source range, offset or speed.");
                // Subtraction is safe after the contained, nonnegative source range check.
                Require(clip.TimelineStartUs <= long.MaxValue - (clip.SourceOutUs - clip.SourceInUs),
                    ErrorCode.InvalidRange, path + ".timelineStartUs", "Timeline end exceeds Int64.");
            }
            var rawGroups = new Dictionary<string, List<RecordedAction>>(StringComparer.Ordinal);
            for (var i = 0; i < project.Events.Length; i++)
            {
                var e = project.Events[i]; var path = $"events[{i}]";
                Register(e.Id, path + ".id");
                if (!takes.TryGetValue(e.TakeId, out var take)) throw new ProjectError(ErrorCode.MissingReference, path + ".takeId", "Unknown take.");
                if (!monitors.TryGetValue(e.MonitorSlotId, out var monitor) || !take.MonitorSlotIds.Contains(e.MonitorSlotId, StringComparer.Ordinal))
                    throw new ProjectError(ErrorCode.MissingReference, path + ".monitorSlotId", "Event monitor absent in take.");
                Require(e.TimeUs >= 0 && e.TimeUs < take.DurationUs, ErrorCode.InvalidRange, path + ".timeUs", "Event time outside take.");
                ValidateOrder(e.Order, path + ".order");
                Require(CoordinateMapper.Contains(new(e.X, e.Y), monitor.Geometry.Size), ErrorCode.InvalidCoordinate, path, "Event outside finite half-open bounds.");
                if (e.AtomicGroupId is { } group)
                {
                    ValidateID(group, path + ".atomicGroupId");
                    if (!rawGroups.TryGetValue(group, out var events)) rawGroups[group] = events = [];
                    events.Add(e);
                }
                var valid = e.Kind switch
                {
                    ActionKind.ButtonDown or ActionKind.ButtonUp => e.Button is not null && Enum.IsDefined(e.Button.Value) && e.AtomicGroupId is not null && e.DeltaX is null && e.DeltaY is null,
                    ActionKind.Move => e.Button is null && e.DeltaX is null && e.DeltaY is null,
                    ActionKind.Scroll => e.Button is null && e.AtomicGroupId is null && e.DeltaX is { } dx && double.IsFinite(dx) && e.DeltaY is { } dy && double.IsFinite(dy),
                    _ => false
                };
                Require(valid, ErrorCode.InvalidAction, path, "Action fields do not match variant.");
            }
            foreach (var (id, raw) in rawGroups.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var events = raw.OrderBy(e => e.TimeUs).ThenBy(e => e.Order).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
                var downs = events.Where(e => e.Kind == ActionKind.ButtonDown).ToArray();
                var ups = events.Where(e => e.Kind == ActionKind.ButtonUp).ToArray();
                Require(downs.Length == 1 && ups.Length == 1, ErrorCode.InvalidAtomicGroup, "atomicGroups." + id, "Group needs one down and one up.");
                var down = downs[0]; var up = ups[0];
                Require(down.Button == up.Button && events[0].Id == down.Id && events[^1].Id == up.Id && events.All(e => e.TakeId == down.TakeId),
                    ErrorCode.InvalidAtomicGroup, "atomicGroups." + id, "Group must stay in one take, down first/up last with matching button.");
                Groups.Add(id, new(down, up, events));
            }
        }
    }

    private static void Require(bool condition, ErrorCode code, string path, string message)
    {
        if (!condition) throw new ProjectError(code, path, message);
    }
    private static void ValidateOrder(int order, string path) => Require(order >= 0, ErrorCode.InvalidRange, path, "Order must be nonnegative Int32.");
    private static bool AlphaNumeric(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
    private static void ValidateID(string id, string path) => Require(id.Length is > 0 and <= 128 && AlphaNumeric(id[0]) &&
        id.All(c => AlphaNumeric(c) || c is '.' or '_' or '-'), ErrorCode.InvalidID, path, "ID must match bounded ASCII ID syntax.");
    private static bool SafeMediaPath(string path) => path.StartsWith("media/", StringComparison.Ordinal) &&
        path.All(c => AlphaNumeric(c) || c is '.' or '_' or '-' or '/') && path.Split('/').All(p => p.Length > 0 && p is not "." and not "..");
}
