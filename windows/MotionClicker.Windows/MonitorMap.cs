using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MotionClicker.Core.Runtime;
using Point=MotionClicker.Core.Point;

namespace MotionClicker.Windows;

internal sealed class MonitorMap : Grid {
    private readonly Canvas canvas=new();
    private IReadOnlyList<RuntimeDisplay> displays=[];
    private HashSet<string> selected=[];
    private IReadOnlyList<(Guid Id,string Display,Point Point)> points=[];
    private Guid? focused;
    private Action<Guid>? selectPoint;
    public MonitorMap() {
        Height=240; CornerRadius=new CornerRadius(12);
        Background=(Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        Children.Add(canvas); SizeChanged+=(_,_)=>Draw();
    }
    public void Update(IReadOnlyList<RuntimeDisplay> screens,HashSet<string> chosen,IReadOnlyList<(Guid Id,string Display,Point Point)> markers,Guid? focus,Action<Guid> select) {
        displays=screens; selected=chosen; points=markers; focused=focus; selectPoint=select; Draw();
    }
    private void Draw() {
        canvas.Children.Clear();
        if(displays.Count==0) { var empty=new TextBlock { Text="No displays available · Refresh to try again",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(24) }; canvas.Children.Add(empty); return; }
        var layout=new MonitorMapLayout(displays,Math.Max(0,ActualWidth-48),Height-48);
        for(var i=0;i<displays.Count;i++) {
            var d=displays[i]; if(!layout.Frames.TryGetValue(d.Id,out var frame)) continue;
            var label=new StackPanel { Spacing=4,Margin=new Thickness(12),VerticalAlignment=VerticalAlignment.Bottom };
            label.Children.Add(new TextBlock { Text=$"Display {i+1}",FontSize=13,TextTrimming=TextTrimming.CharacterEllipsis });
            label.Children.Add(new TextBlock { Text=$"{d.Geometry.Size.Width:0.##} × {d.Geometry.Size.Height:0.##} pt",FontSize=12,Opacity=0.7,TextTrimming=TextTrimming.CharacterEllipsis });
            var border=new Border { Width=frame.Size.Width,Height=frame.Size.Height,CornerRadius=new CornerRadius(12),BorderThickness=new Thickness(selected.Contains(d.Id)?2:1),BorderBrush=(Brush)Application.Current.Resources[selected.Contains(d.Id)?"AccentFillColorDefaultBrush":"ControlStrokeColorDefaultBrush"],Child=label };
            ToolTipService.SetToolTip(border,d.Name); Canvas.SetLeft(border,24+frame.Origin.X); Canvas.SetTop(border,24+frame.Origin.Y); canvas.Children.Add(border);
        }
        for(var i=0;i<points.Count;i++) {
            var p=points[i]; var display=displays.FirstOrDefault(d=>d.Id==p.Display); if(display==null) continue;
            var location=layout.Project(p.Point,display); if(location==null) continue;
            var marker=new Button { Content=$"{i+1}",Width=28,Height=28,MinWidth=28,MinHeight=28,Padding=new Thickness(0),CornerRadius=new CornerRadius(14) };
            if(focused==p.Id) marker.Style=(Style)Application.Current.Resources["AccentButtonStyle"];
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(marker,$"Select point {i+1}");
            marker.Click+=(_,_)=>selectPoint?.Invoke(p.Id);
            Canvas.SetLeft(marker,24+location.X-14); Canvas.SetTop(marker,24+location.Y-14); canvas.Children.Add(marker);
        }
    }
}
