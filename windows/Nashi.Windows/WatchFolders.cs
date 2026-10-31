using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nashi.Core;
namespace Nashi.Windows;
/// Watches user-configured folders and converts newly created/modified files
/// headlessly (DragShift parity / Filee watch folders). Every conversion is
/// written as a new copy next to the source — originals are never touched.
public sealed class WatchFolders:IDisposable
{
    private readonly PreferencesStore preferences;private readonly Toolchain tools;private readonly OfficeRuntime officeRuntime;
    private readonly Dictionary<string,FileSystemWatcher> watchers=new();private readonly Dictionary<string,Timer> debounce=new();
    public WatchFolders(PreferencesStore preferences,Toolchain tools,OfficeRuntime officeRuntime)
    {
        this.preferences=preferences;this.tools=tools;this.officeRuntime=officeRuntime;
        foreach(var wf in preferences.Value.WatchFolders.Where(w=>w.Enabled))Start(wf);
    }
    public void Refresh(IReadOnlyList<WatchFolder> folders)
    {
        foreach(var kv in watchers.ToList()){kv.Value.Dispose();watchers.Remove(kv.Key);}
        foreach(var kv in debounce.ToList()){kv.Value.Dispose();debounce.Remove(kv.Key);}
        foreach(var wf in folders.Where(w=>w.Enabled))Start(wf);
    }
    private void Start(WatchFolder wf)
    {
        if(wf.Path is null||!Directory.Exists(wf.Path))return;
        var watcher=new FileSystemWatcher(wf.Path){NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite|NotifyFilters.CreationTime,EnableRaisingEvents=true};
        watcher.Created+=(_,e)=>ScheduleConvert(e.FullPath,wf.Format);
        watcher.Changed+=(_,e)=>ScheduleConvert(e.FullPath,wf.Format);
        watchers[wf.Path]=watcher;
    }
    private void ScheduleConvert(string path,string format)
    {
        if(debounce.TryGetValue(path,out var old))old.Dispose();
        var timer=new Timer(_=>{debounce.Remove(path);_=ConvertFile(path,format);},null,1000,Timeout.Infinite);
        debounce[path]=timer;
    }
    private async Task ConvertFile(string path,string format)
    {
        try
        {
            await WaitForFileUnlock(path);
            var destination=DestinationFolder(path);Directory.CreateDirectory(destination);
            var inputs=new[]{path};var outputs=new List<string>();
            var progress=new Progress<OperationProgress>(_=>{});
            if(FeatureCatalog.IsImage(path)){outputs.AddRange(ImageEngine.Apply("convert",inputs,destination,new ImageOptions(),format,CancellationToken.None,progress));}
            else if(FeatureCatalog.Formats([path]).Length>0)
            {
                var office=officeRuntime.Executable;
                if(office is null)return;
                var docConversion=new DocumentConversion(officeRuntime,tools);
                outputs.AddRange(await docConversion.ConvertAsync(inputs,format,destination,new DocumentOptions(),progress,CancellationToken.None));
            }
            else if(FeatureCatalog.IsVideo(path)||FeatureCatalog.IsAudio(path))
            {
                if(!tools.Ready)return;
                var mediaEngine=new MediaEngine(tools.Executable("ffmpeg"),tools.Executable("ffprobe"));
                outputs.AddRange(await mediaEngine.ConvertAsync(inputs,format,destination,new MediaOptions(),progress,CancellationToken.None));
            }
            else if(FeatureCatalog.IsArchive(path)){outputs.AddRange(ArchiveEngine.Convert(path,format,destination,null,progress,CancellationToken.None));}
            else return;
            // Record in undo history so the user can send results back to the Recycle Bin
            var undo=new UndoHistory(preferences.DirectoryPath);undo.Record("Watch folder → "+format,inputs,outputs);
        }
        catch(Exception){/* Watch folder must never crash the app. */}
    }
    private static async Task WaitForFileUnlock(string path)
    {
        for(var n=0;n<20;n++)
        {
            try{using(var stream=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.None)){return;}}catch(IOException){await Task.Delay(250);}
        }
    }
    private string DestinationFolder(string firstInput)
    {
        var p=preferences.Value;
        if(p.OutputMode=="folder"&&!string.IsNullOrWhiteSpace(p.OutputFolder))return p.OutputFolder;
        return Path.GetDirectoryName(firstInput)!;
    }
    public void Dispose(){foreach(var w in watchers.Values)w.Dispose();watchers.Clear();foreach(var t in debounce.Values)t.Dispose();debounce.Clear();}
}
