using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MotionClicker.Core;
using MotionClicker.Core.Recording;
using global::Windows.Media.Core;
using global::Windows.Media.Playback;
using global::Windows.Storage;
namespace MotionClicker.Windows.RecordingUI;

/// <summary>Source-only inspection. This type has no runtime/input backend reference.</summary>
internal sealed class SavedVideoPanel : StackPanel,IDisposable {
    private readonly BundleInspection inspection;
    private readonly CompleteGapLookup gaps;
    private readonly ComboBox monitor=new() { Header="Source display",DisplayMemberPath="Name",HorizontalAlignment=HorizontalAlignment.Stretch };
    private readonly Slider seek=new() { Minimum=0,Header="Source time (seconds)",HorizontalAlignment=HorizontalAlignment.Stretch };
    private readonly MediaPlayerElement surface=new() { AreTransportControlsEnabled=false,AutoPlay=false,MinHeight=240,MaxHeight=420,HorizontalAlignment=HorizontalAlignment.Stretch };
    private readonly TextBlock unavailable=new() { TextWrapping=TextWrapping.Wrap,MinHeight=40 };
    private readonly TextBlock position=new() { FontSize=12,TextWrapping=TextWrapping.Wrap };
    private readonly Button play=new() { Content="Play source",IsEnabled=false },pause=new() { Content="Pause video",IsEnabled=false };
    private readonly CancellationTokenSource closing=new();
    private MediaPlayer? player;
    private MediaSource? source;
    private SourceGapWindow? window;
    private RecordedBundleMedia? currentMedia;
    private long sourceUs,generation;
    private bool settingControls,working,disposed;
    private Query? latest;
    private sealed record Query(long Generation,RecordedBundleMedia Media,long SourceUs,bool Play);
    public SavedVideoPanel(BundleInspection inspection) {
        this.inspection=inspection;Spacing=12;
        var snapshot=inspection.Snapshot;gaps=new(snapshot.GapsFile,inspection.Manifest.DurationUs,inspection.Manifest.Streams);
        Children.Add(new TextBlock { Text=inspection.CanRun?"Original source video · native H.264 validated. Map every display explicitly before Run.":"Inspection only · "+(inspection.Issues.Length>0?string.Join("\n",inspection.Issues):"Partial capture cannot run."),TextWrapping=TextWrapping.Wrap });
        monitor.ItemsSource=inspection.Project.MonitorSlots;Children.Add(monitor);Children.Add(surface);Children.Add(unavailable);Children.Add(position);
        seek.Maximum=inspection.Manifest.DurationUs/1_000_000d;Children.Add(seek);
        var transport=new StackPanel { Orientation=Orientation.Horizontal,Spacing=8 };transport.Children.Add(play);transport.Children.Add(pause);Children.Add(transport);
        var actions=new ListView { Height=180,SelectionMode=ListViewSelectionMode.Single,ItemsSource=inspection.Actions };
        Children.Add(new TextBlock { Text="Recorded actions · source time · zero input",FontSize=13 });Children.Add(actions);
        monitor.SelectionChanged+=(_,_)=>{if(!settingControls)Request(0,false);};
        seek.ValueChanged+=(_,args)=>{if(!settingControls)Request(FromSeconds(args.NewValue),false);};
        play.Click+=(_,_)=>Request(sourceUs,true);pause.Click+=(_,_)=>player?.Pause();
        actions.SelectionChanged+=(_,_)=> {
            if(actions.SelectedItem is not BundleActionRow row)return;
            settingControls=true;monitor.SelectedItem=inspection.Project.MonitorSlots.FirstOrDefault(s=>s.Id==row.Action.MonitorSlotId);settingControls=false;
            Request(row.Action.TimeUs,false);
        };
        if(inspection.Project.MonitorSlots.Length>0)monitor.SelectedIndex=0;
    }
    private static long FromSeconds(double seconds) {
        if(!double.IsFinite(seconds)||seconds<=0)return 0;var value=seconds*1_000_000;
        return value>=long.MaxValue?long.MaxValue:(long)value;
    }
    private void Mask(string message) { player?.Pause();surface.Visibility=Visibility.Collapsed;unavailable.Text=message;unavailable.Visibility=Visibility.Visible;play.IsEnabled=false;pause.IsEnabled=false; }
    private void SetPosition(long value) {
        sourceUs=value;settingControls=true;seek.Value=value/1_000_000d;settingControls=false;
        position.Text=$"Source {value/1_000_000d:0.000000}s · file PTS {(currentMedia is null?0:Math.Max(0,value-currentMedia.MediaTimeZeroUs))/1_000_000d:0.000000}s";
    }
    private void Request(long value,bool autoPlay) {
        if(disposed)return;
        var selected=monitor.SelectedItem as MonitorSlot;currentMedia=inspection.Media.FirstOrDefault(m=>m.MonitorSlotId==selected?.Id);
        generation++;SetPosition(value);Mask("Inspecting complete video gap history…");
        if(currentMedia is null){latest=null;Mask("No successful video for this display. Inspect the manifest failure details.");return;}
        if(inspection.Issues.Any(i=>i.StartsWith(currentMedia.MonitorSlotId+":",StringComparison.Ordinal))){latest=null;Mask("Native media validation failed for this display. Playback is unavailable.");return;}
        if(value<currentMedia.MediaTimeZeroUs){latest=null;Mask($"No source image yet · first image at {currentMedia.MediaTimeZeroUs/1_000_000d:0.000000}s");return;}
        if(value>=inspection.Manifest.DurationUs||value-currentMedia.MediaTimeZeroUs>=currentMedia.EncodedDurationUs){latest=null;Mask("No source image after the valid video prefix.");return;}
        latest=new(generation,currentMedia,value,autoPlay);if(!working)_=ProcessAsync();
    }
    private async Task ProcessAsync() {
        working=true;
        try {
            while(latest is Query query&&!disposed) {
                latest=null;
                var inspected=await gaps.WindowAsync(query.Media.MonitorSlotId,query.SourceUs,closing.Token);
                if(query.Generation!=generation)continue;
                window=inspected;
                if(!SourcePlayback.TryRange(query.Media,inspected,query.SourceUs,out var pts,out var endpoint)) { Mask(inspected.Kind is null?"Source time exceeds native playback limits.":$"Known video gap: {inspected.Kind} · no image is displayed");continue; }
                ReleasePlayer();
                var file=await StorageFile.GetFileFromPathAsync(query.Media.File);
                if(query.Generation!=generation||disposed)continue;
                source=MediaSource.CreateFromStorageFile(file);
                var item=new MediaPlaybackItem(source,TimeSpan.Zero,endpoint);
                var native=new MediaPlayer { AutoPlay=false,IsMuted=true,IsLoopingEnabled=false };player=native;
                native.CommandManager.IsEnabled=false;
                var opened=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                native.MediaOpened+=(_,_)=>opened.TrySetResult();
                native.MediaFailed+=(_,args)=> {
                    var error=new RecordingException(args.Error+": "+args.ErrorMessage);opened.TrySetException(error);
                    DispatcherQueue.TryEnqueue(()=>{if(query.Generation==generation&&!disposed)Mask("Native video unavailable: "+error.Message);});
                };
                native.MediaEnded+=(_,_)=>DispatcherQueue.TryEnqueue(()=> {
                    if(query.Generation!=generation||disposed)return;
                    SetPosition(checked(query.Media.MediaTimeZeroUs+endpoint.Ticks/10));
                    Mask("Playback reached the end of the inspected source interval. Seek deliberately to inspect another time.");
                });
                surface.SetMediaPlayer(native);native.Source=item;
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(20),closing.Token);
                if(query.Generation!=generation||disposed){ReleasePlayer();continue;}
                native.PlaybackSession.Position=pts;
                surface.Visibility=Visibility.Visible;unavailable.Visibility=Visibility.Collapsed;play.IsEnabled=true;pause.IsEnabled=true;
                if(query.Play)native.Play();
            }
        } catch(OperationCanceledException) { }
        catch(Exception error) { if(!disposed)Mask("Complete source inspection unavailable. Video is hidden: "+error.Message); }
        finally {working=false;if(latest is not null&&!disposed)_=ProcessAsync();}
    }
    public void PollPosition() {
        if(disposed||working||player is null||currentMedia is null||surface.Visibility!=Visibility.Visible)return;
        var value=checked(currentMedia.MediaTimeZeroUs+player.PlaybackSession.Position.Ticks/10);
        if(window is null||!window.Contains(currentMedia.MonitorSlotId,value)){SetPosition(value);Mask("End of the inspected interval · seek to inspect another time");return;}
        SetPosition(value);
    }
    public void StopPlayback(){player?.Pause();}
    private void ReleasePlayer(){surface.SetMediaPlayer(null);if(player is not null){player.Source=null;player.Dispose();player=null;}source?.Dispose();source=null;}
    public void Dispose(){if(disposed)return;disposed=true;generation++;latest=null;closing.Cancel();ReleasePlayer();}
}
