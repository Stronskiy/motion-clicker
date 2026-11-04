using MotionClicker.Core;
using MotionClicker.Core.Recording;
using MotionClicker.Windows.Recording;
namespace MotionClicker.Windows.RecordingUI;
internal sealed record BundleActionRow(RecordedAction Action,string Label) { public override string ToString()=>Label; }
internal sealed record BundleInspection(RecordedBundleSnapshot Snapshot,RecordingManifest Manifest,RecordedBundleMedia[] Media,MotionProject Project,BundleActionRow[] Actions,string[] Issues) {
    public bool CanRun=>Snapshot.Availability==RecordedBundleAvailability.RequiresMonitorMapping&&Issues.Length==0&&Media.Length==Manifest.Targets.Length;
    public static async Task<BundleInspection> OpenAsync(string root,CancellationToken cancellation) {
        var snapshot=await Task.Run(()=>NativeRecordingServices.WithScratch(scratch=>RecordedBundleStore.ColdOpen(root,scratch)),cancellation).ConfigureAwait(false);
        List<string> issues=[];
        foreach(var media in snapshot.Media) {
            cancellation.ThrowIfCancellationRequested();
            try {
                var stream=snapshot.Manifest.Streams.Single(s=>s.MonitorSlotId==media.MonitorSlotId);
                var slot=snapshot.Manifest.Targets.Single(s=>s.Id==media.MonitorSlotId);
                var width=checked((int)Math.Round(slot.Geometry.Size.Width*slot.Geometry.ScaleFactor));
                var height=checked((int)Math.Round(slot.Geometry.Size.Height*slot.Geometry.ScaleFactor));
                _=await NativeVideoInspector.InspectAsync(media.File,stream.FrameCount,media.EncodedDurationUs,width,height,cancellation).ConfigureAwait(false);
            } catch(OperationCanceledException){throw;}catch(Exception error){issues.Add(media.MonitorSlotId+": "+error.Message);}
        }
        var project=await Task.Run(()=>snapshot.Project,cancellation).ConfigureAwait(false);
        var actions=await Task.Run(()=>project.Events.Select(a=>new BundleActionRow(a,$"{a.TimeUs/1_000_000d:0.000000}s · {a.Kind} · {project.MonitorSlots.First(s=>s.Id==a.MonitorSlotId).Name} · ({a.X:0.##}, {a.Y:0.##})")).ToArray(),cancellation).ConfigureAwait(false);
        return new(snapshot,snapshot.Manifest,snapshot.Media,project,actions,issues.ToArray());
    }
}
