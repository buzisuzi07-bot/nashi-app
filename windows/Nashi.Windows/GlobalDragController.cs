using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Nashi.Core;
namespace Nashi.Windows;

/// Reads only the three configured modifiers and primary mouse button. It never
/// records keystrokes or polls the clipboard. Actual files/links arrive through
/// OLE drag/drop after the user chooses a wheel segment.
public sealed class GlobalDragController:IDisposable
{
    [StructLayout(LayoutKind.Sequential)]internal struct NativePoint{public int X,Y;}
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr window,System.Text.StringBuilder text,int count);
    [DllImport("user32.dll")]private static extern bool RegisterHotKey(IntPtr window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")]private static extern bool UnregisterHotKey(IntPtr window,int id);
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct GuiThreadInfo{public int Size,Flags;public IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret;public NativeRect CaretRect;}
    [DllImport("user32.dll")]private static extern bool GetGUIThreadInfo(uint thread,ref GuiThreadInfo info);
    /// True while the foreground thread is in a window move/size loop (title-bar drag).
    /// Those drags belong to window management and never carry files.
    private static bool InMoveSize(){var info=new GuiThreadInfo{Size=Marshal.SizeOf<GuiThreadInfo>()};return GetGUIThreadInfo(0,ref info)&&(info.Flags&0x2)!=0;}
    [DllImport("oleacc.dll")]private static extern int AccessibleObjectFromPoint(NativePoint point,[MarshalAs(UnmanagedType.Interface)]out object accessible,[MarshalAs(UnmanagedType.Struct)]out object child);
    private readonly Window owner;private readonly PreferencesStore preferences;private readonly Action<string,IDataObject,string?> dropped;private readonly Action link;private readonly System.Windows.Threading.DispatcherTimer timer;
    private NativePoint start;private bool down,opened;private Task<string?>? hint;private GlobalDropWheel? wheel;private GlobalDropWheel? quickLaunchWheel;private HwndSource? source;private IntPtr handle;private bool? shortcutEnabled;
    internal GlobalDropWheel? ActiveWheel=>wheel;
    public bool LinkShortcutRegistered{get;private set;}
    private bool OwnerAvailable=>DesktopGeometry.IsInteractive(owner)&&!DesktopGeometry.IsForegroundFullscreen(new WindowInteropHelper(owner).Handle);
    public GlobalDragController(Window owner,PreferencesStore preferences,Action<string,IDataObject,string?> dropped,Action link)
    {
        this.owner=owner;this.preferences=preferences;this.dropped=dropped;this.link=link;timer=new(){Interval=TimeSpan.FromMilliseconds(40)};timer.Tick+=(_,_)=>Poll();
        if(new WindowInteropHelper(owner).Handle!=IntPtr.Zero)Register();else owner.SourceInitialized+=(_,_)=>Register();timer.Start();
    }
    private void Register(){handle=new WindowInteropHelper(owner).Handle;source=HwndSource.FromHwnd(handle);source.AddHook(Hook);UpdateShortcut();}
    private void UpdateShortcut(){if(handle==IntPtr.Zero)return;var enabled=preferences.Value.Enabled;if(shortcutEnabled==enabled)return;shortcutEnabled=enabled;if(LinkShortcutRegistered)UnregisterHotKey(handle,7701);LinkShortcutRegistered=enabled&&RegisterHotKey(handle,7701,0x4000|0x2|0x4,0x44);}
    private IntPtr Hook(IntPtr window,int message,IntPtr w,IntPtr l,ref bool handled){if(message==0x0312&&w.ToInt32()==7701){if(preferences.Value.Enabled&&OwnerAvailable)link();handled=true;}return IntPtr.Zero;}
    private static bool Held(int key)=>(GetAsyncKeyState(key)&0x8000)!=0;
    private string Chord(){var parts=new List<string>();if(Held(0x11))parts.Add("Ctrl");if(Held(0x10))parts.Add("Shift");if(Held(0x12))parts.Add("Alt");return string.Join('+',parts);}
    private void Poll()
    {
        UpdateShortcut();if(!preferences.Value.Enabled||!OwnerAvailable){wheel?.Close();quickLaunchWheel?.Close();wheel=null;quickLaunchWheel=null;down=false;opened=false;hint=null;return;}var held=PrimaryMouseButton.IsPressed;GetCursorPos(out var point);
        if(!held){if(down){down=false;opened=false;hint=null;
                // Quick Launch tap detection (DragShift parity): tap Shift/Ctrl on empty desktop/app
                if(preferences.Value.QuickLaunchEnabled){var qChord=Chord();var qPrefs=preferences.Value;
                    if((qChord==qPrefs.ConvertModifier||qChord==qPrefs.ToolsModifier)&&IsDesktopOrExplorerEmpty(point)){
                        if(qChord==qPrefs.ConvertModifier)OpenQuickLaunchWheel("apps",point);
                        else if(qChord==qPrefs.ToolsModifier)OpenQuickLaunchWheel("sites",point);
                        return;
                    }
                }
                if(wheel is {IsVisible:true}){var old=wheel;var dismiss=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(150)};dismiss.Tick+=(_,_)=>{dismiss.Stop();if(old.IsVisible)old.Close();};dismiss.Start();}wheel=null;}return;}
        if(!down){down=true;start=point;opened=false;hint=null;return;}if(opened||Math.Abs(point.X-start.X)+Math.Abs(point.Y-start.Y)<10)return;
        hint??=Task.Run(()=>LinkAt(start));var chord=Chord();var p=preferences.Value;var kind=chord==p.ToolsModifier?"tools":chord==p.ConvertModifier?"convert":chord==p.ActionsModifier?"actions":null;if(kind is null)return;opened=true;if(InMoveSize())return;
        var sourceHint=hint;wheel=new GlobalDropWheel(kind,p.WheelScale,(data,id)=>dropped(id,data,sourceHint?.IsCompletedSuccessfully==true?sourceHint.Result:null),()=>sourceHint?.IsCompletedSuccessfully==true?sourceHint.Result:null);wheel.Show();wheel.Place(point.X,point.Y);
    }
    private static string? LinkAt(NativePoint point)
    {
        try
        {
            var element=AutomationElement.FromPoint(new Point(point.X,point.Y));for(var n=0;n<7&&element is not null;n++,element=TreeWalker.ControlViewWalker.GetParent(element))
            {
                if(element.Current.IsPassword||element.Current.ControlType==ControlType.Edit)return null;
                if(element.TryGetCurrentPattern(ValuePattern.Pattern,out var value)&&WebMediaPayload.Parse(((ValuePattern)value).Current.Value) is {} result)return result.Url;
            }
            if(AccessibleObjectFromPoint(point,out var accessible,out var child)>=0)
            {
                try{for(var n=0;n<4;n++){var value=accessible.GetType().InvokeMember("accValue",System.Reflection.BindingFlags.GetProperty,null,accessible,[child])?.ToString();if(WebMediaPayload.Parse(value) is {} link)return link.Url;var parent=accessible.GetType().InvokeMember("accParent",System.Reflection.BindingFlags.GetProperty,null,accessible,null);if(parent is null)break;Marshal.FinalReleaseComObject(accessible);accessible=parent;child=0;}}finally{if(Marshal.IsComObject(accessible))Marshal.FinalReleaseComObject(accessible);}
            }
        }catch(ElementNotAvailableException){}catch(InvalidOperationException){}catch(COMException){}catch(System.Reflection.TargetInvocationException){}catch(MissingMethodException){}return null;
    }
    // Quick Launch (DragShift parity): tap Shift/Ctrl on empty desktop or Explorer → app/site wheel
    private bool IsDesktopOrExplorerEmpty(NativePoint point)
    {
        var hwnd=GetForegroundWindow();if(hwnd==IntPtr.Zero)return false;
        var type=new System.Text.StringBuilder(256);if(GetClassName(hwnd,type,256)==0)return false;
        var cls=type.ToString();return cls=="Progman"||cls=="WorkerW"||cls=="CabinetWClass"; // desktop or Explorer
    }
    private void OpenQuickLaunchWheel(string kind,NativePoint point)
    {
        var p=preferences.Value;var items=kind=="apps"?p.QuickApps:p.QuickSites;if(items.Length==0)return;
        var choices=items.Select((item,i)=>new ToolChoice(kind+"-"+i,kind=="apps"?System.IO.Path.GetFileNameWithoutExtension(item):item,kind=="apps"?"📱":"🌐")).ToArray();
        quickLaunchWheel=new GlobalDropWheel("quicklaunch",p.WheelScale,(data,choiceId)=>
        {
            try
            {
                if(kind=="apps"&&int.TryParse(choiceId.Split('-')[1],out var idx)&&idx>=0&&idx<p.QuickApps.Length)
                    Process.Start(new ProcessStartInfo(p.QuickApps[idx]){UseShellExecute=true});
                else if(kind=="sites"&&int.TryParse(choiceId.Split('-')[1],out var idx2)&&idx2>=0&&idx2<p.QuickSites.Length)
                    Process.Start(new ProcessStartInfo(p.QuickSites[idx2]){UseShellExecute=true});
            }
            catch{}quickLaunchWheel=null;
        },()=>null);
        quickLaunchWheel.Show();quickLaunchWheel.Place(point.X,point.Y);
    }
    public void Dispose(){timer.Stop();wheel?.Close();quickLaunchWheel?.Close();if(handle!=IntPtr.Zero)UnregisterHotKey(handle,7701);source?.RemoveHook(Hook);}
}

internal sealed class GlobalDropWheel:Window
{
    internal string State=>$"entered={entered}, over={over}, dropped={dropCount}, choices={choices.Count}, selection={selection}, revealed={Revealed}";private int entered,over,dropCount;
    // The wheel opens almost transparent and only appears once an OLE drag carrying a file or
    // link reaches it. Plain mouse drags with a modifier (text selection, scrollbars) and OLE
    // drags of other data, such as selected text, never show it.
    // Fully transparent layered pixels ignore hit tests, so keep a tiny non-zero opacity.
    private const double HiddenOpacity=.02;
    // At most six slices live on the main ring; anything past that sits on a second
    // ring that opens when "More" is rested on (Circularity/Swivel parity). The canvas
    // and the window only grow at that moment, so the single-ring layout is unchanged.
    internal const int Capacity=6;
    private const double StandardCanvas=540,ExpandedCanvas=820,MainInner=108,MainOuter=250,OverflowInner=276,OverflowOuter=404,ExpandedBackdrop=264;
    internal bool Revealed{get;private set;}
    private void Reveal(){if(Revealed)return;Revealed=true;foreach(var label in canvas.Children.OfType<Border>())label.Visibility=Visibility.Visible;WheelPresentation.Animate(this,HiddenOpacity);if(Opacity<1&&!HasAnimatedProperties)Opacity=1;}
    private readonly string mode;private readonly Action<IDataObject,string> chosen;private readonly Func<string?> hint;private readonly double scale;private readonly Canvas canvas=new(){Background=Brushes.Transparent,AllowDrop=true};private string selection="";private IReadOnlyList<ToolChoice> choices=[];
    private IReadOnlyList<ToolChoice> main=[],overflow=[];private readonly List<System.Windows.Shapes.Path> mainPaths=[],overflowPaths=[];private bool overMore,expanded;private int anchorX,anchorY;private bool anchored;
    public GlobalDropWheel(string mode,double scale,Action<IDataObject,string> chosen,Func<string?> hint)
    {
        this.mode=mode;this.scale=scale;this.chosen=chosen;this.hint=hint;Title="Nashi · "+mode;Width=Height=WheelPresentation.Diameter*scale;WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;ShowActivated=false;Topmost=true;AllowDrop=true;Content=new Viewbox{Child=canvas};canvas.Width=canvas.Height=StandardCanvas;Build();Opacity=HiddenOpacity;
        // Reveal only once the payload offers choices. On entry the pointer sits in the center,
        // so e.Effects is still None even for a valid file drag.
        DragEnter+=(_,e)=>{entered++;Track(e);if(choices.Count>0||noTools)Reveal();};
        DragOver+=(_,e)=>{over++;Track(e);if(choices.Count>0||noTools)Reveal();};
        Drop+=(_,e)=>{dropCount++;Track(e);var id=selection;Close();if(id.Length>0)chosen(e.Data,id);e.Handled=true;};PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
    }
    // The drop gesture anchors the wheel once; expanding to the overflow ring keeps the
    // same anchor so the main ring never slides out from under the pointer.
    internal void Place(int x,int y)
    {
        if(!anchored){anchorX=x;anchorY=y;anchored=true;}
        DesktopGeometry.PlaceWheel(this,anchorX,anchorY,LogicalWidth);
    }
    private double CurrentCanvas=>expanded?ExpandedCanvas:StandardCanvas;
    private double Centre=>CurrentCanvas/2;
    private double LogicalWidth=>CurrentCanvas*WheelPresentation.Diameter/StandardCanvas*scale;
    private void SetExpanded(bool value)
    {
        if(value==expanded)return;expanded=value;canvas.Width=canvas.Height=CurrentCanvas;Width=Height=LogicalWidth;if(anchored)DesktopGeometry.PlaceWheel(this,anchorX,anchorY,LogicalWidth);Build();
    }
    private void Track(DragEventArgs e)
    {
        var files=e.Data.GetData(DataFormats.FileDrop) as string[]??[];var updated=Choices(files,DragPayload.Parse(e.Data),Payload(e.Data));
        // Replacing the hit-test target during DragEnter causes OLE to enter
        // again instead of advancing to DragOver. Keep the visual tree stable
        // until the payload actually changes, and select on entry as well.
        // A file this mode has no tool for (an .ico, for example) still shows the wheel, with a message, so it does not look broken.
        var unsupported=files.Length>0&&files.All(p=>!Directory.Exists(p)||FeatureCatalog.Extension(p) is "pages" or "rtfd")&&updated.Count==0;
        if(!updated.SequenceEqual(choices)||unsupported!=noTools){choices=updated;noTools=unsupported;Split();Build();}
        var p=e.GetPosition(canvas);var centre=Centre;var dx=p.X-centre;var dy=p.Y-centre;var distance=Math.Sqrt(dx*dx+dy*dy);var onOuter=false;var index=-1;
        if(distance<MainInner||distance>(expanded?OverflowOuter:MainOuter)||main.Count==0){SetSelection("",-1,false);overMore=false;}
        else if(!expanded||distance<=MainOuter)
        {
            var count=main.Count;var angle=(Math.Atan2(dy,dx)*180/Math.PI-WheelPresentation.StartAngle(count)+360+180d/count)%360;index=(int)(angle/(360d/count));var choice=main[index];overMore=choice.Id=="more";SetSelection(overMore?"":choice.Id,index,false);
        }
        else if(distance>=OverflowInner){onOuter=true;overMore=false;var count=overflow.Count;if(count>0){var angle=(Math.Atan2(dy,dx)*180/Math.PI+90+360+180d/count)%360;index=(int)(angle/(360d/count));SetSelection(overflow[index].Id,index,true);}else SetSelection("",-1,true);}
        else{SetSelection("",-1,false);overMore=false;} // the gap between the rings keeps the expansion as-is
        // The overflow ring stays open while the pointer rests on "More", sits in the
        // overflow band or crosses the gap between them, so it does not flicker.
        var keep=expanded?(overMore||onOuter||(distance>MainOuter&&distance<OverflowInner)):overMore;if(keep!=expanded)SetExpanded(keep);
        e.Effects=selection.Length>0?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;
    }
    private void SetSelection(string id,int index,bool outer){if(id!=selection){selection=id;if(id.Length>0)Feedback.Play();}Highlight(index,outer);}
    private WebMediaPayload? Payload(IDataObject data)=>DragPayload.Parse(data,hint());
    private IReadOnlyList<ToolChoice> Choices(string[] files,WebMediaPayload? dataLink,WebMediaPayload? web)
    {
        // A link carried by the drag data itself (a browser drag) still wins, as before. A link only guessed from
        // the element under the start point (for example the NVIDIA App overlay) never hides dragged files.
        if(files.Length>0&&dataLink is null)
        {
            if(mode=="convert")return FeatureCatalog.Formats(files).Select(f=>new ToolChoice("convert:"+f,f.ToUpperInvariant(),"↗")).ToArray();
            if(mode=="actions")return [new("share","Share","◉"),new("float","Float","▣"),new("copy","Copy","▱"),new("move","Move","↗"),new("copyPath","Copy Path","↪"),new("terminal","Terminal",">_")];
            return FeatureCatalog.Tools(files).Select(t=>new ToolChoice(t,FeatureCatalog.ToolNames[t],MainWindow.ToolSymbol(t))).ToArray();
        }
        if(web is null)return [];return web.Kind switch{"youtube"=>[new("download:video","Download Video","↓"),new("download:thumbnail","Download Thumbnail","▣"),new("download:audio","Download Audio","♫")],"instagramPhotos"=>[new("download:photos","Download Images","▣")],"instagramReel"=>[new("download:video","Download Video","↓"),new("download:audio","Download Audio","♫")],"image"=>[new("download:image","Download Image","↓")],_=>[new("download:video","Download Video","↓"),new("download:audio","Download Audio","♫") ]};
    }
    private void Highlight(int index,bool outer)
    {
        var list=outer?overflowPaths:mainPaths;var other=outer?mainPaths:overflowPaths;
        for(var n=0;n<list.Count;n++){var on=n==index;list[n].Fill=Theme.Brush(on?"Sage":"Inset");list[n].Stroke=Theme.Brush(on?"Gold":"Line");}
        for(var n=0;n<other.Count;n++){other[n].Fill=Theme.Brush("Inset");other[n].Stroke=Theme.Brush("Line");}
    }
    private bool noTools;
    private void Split()
    {
        if(choices.Count<=Capacity){main=choices;overflow=[];if(expanded)SetExpanded(false);}
        else{main=[..choices.Take(Capacity-1),new ToolChoice("more","More","⋯")];overflow=[..choices.Skip(Capacity-1)];}
    }
    private void Build()
    {
        canvas.Children.Clear();mainPaths.Clear();overflowPaths.Clear();
        var centre=Centre;var backdrop=expanded?ExpandedBackdrop:StandardCanvas/2;
        var ellipse=new Ellipse{Width=backdrop*2,Height=backdrop*2,Fill=WheelPresentation.Backdrop};Canvas.SetLeft(ellipse,centre-backdrop);Canvas.SetTop(ellipse,centre-backdrop);canvas.Children.Add(ellipse);
        BuildRing(main,centre,MainInner,MainOuter,WheelPresentation.StartAngle(main.Count),mainPaths);
        if(expanded)BuildRing(overflow,centre,OverflowInner,OverflowOuter,-90,overflowPaths);
        var center=new Border{Width=180,Height=180,IsHitTestVisible=false,Visibility=Revealed?Visibility.Visible:Visibility.Hidden,Child=L10n.Label(new TextBlock{Text=choices.Count==0?(noTools?"No tools for this file type":"Keep dragging\nDrop a file or link"):mode=="convert"?"Convert":mode=="actions"?"File actions":"Tools",TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center,FontSize=20,FontWeight=FontWeights.SemiBold,Foreground=Theme.Brush("Ink")} )};Canvas.SetLeft(center,centre-90);Canvas.SetTop(center,centre-90);canvas.Children.Add(center);
    }
    private void BuildRing(IReadOnlyList<ToolChoice> tools,double centre,double inner,double outer,double start,List<System.Windows.Shapes.Path> paths)
    {
        var count=tools.Count;
        for(var i=0;i<count;i++)
        {
            var angle=(start+360d*i/count)*Math.PI/180;var half=(180d/count-1)*Math.PI/180;Point P(double r,double a)=>new(centre+r*Math.Cos(a),centre+r*Math.Sin(a));var a=angle-half;var b=angle+half;var geometry=new StreamGeometry();using(var g=geometry.Open()){g.BeginFigure(P(inner,a),true,true);g.LineTo(P(outer,a),true,false);g.ArcTo(P(outer,b),new(outer,outer),0,b-a>Math.PI,SweepDirection.Clockwise,true,false);g.LineTo(P(inner,b),true,false);g.ArcTo(P(inner,a),new(inner,inner),0,b-a>Math.PI,SweepDirection.Counterclockwise,true,false);}var path=new System.Windows.Shapes.Path{Data=geometry,Fill=Theme.Brush("Inset"),Stroke=Theme.Brush("Line")};canvas.Children.Add(path);paths.Add(path);
            var preferred=inner==MainInner?WheelPresentation.LabelRadius(count,half):WheelPresentation.LabelRadius(count,half,inner,outer);
            var (stack,location)=WheelPresentation.Caption(tools[i],geometry,angle,half,count,centre,preferred);var label=new Border{Child=stack,Width=stack.DesiredSize.Width,Height=stack.DesiredSize.Height,IsHitTestVisible=false};Canvas.SetLeft(label,location.X-label.Width/2);Canvas.SetTop(label,location.Y-label.Height/2);canvas.Children.Add(label);
        }
    }
}
