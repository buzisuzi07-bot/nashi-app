using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Nashi.Core;
namespace Nashi.Windows;
/// Converts files without opening the main window (--convert CLI). Routes each
/// input to the engine that owns its format, saves to the preference-chosen
/// destination, shows one toast per run and exits.
public sealed class HeadlessConversion
{
    private readonly PreferencesStore preferences;private readonly Toolchain tools;private readonly OfficeRuntime officeRuntime;private Window? toastOwner;
    public HeadlessConversion()
    {
        var dataDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Nashi");
        Directory.CreateDirectory(dataDir);
        preferences=new PreferencesStore(dataDir);
        var toolsDir=Path.Combine(dataDir,"tools");Directory.CreateDirectory(toolsDir);
        tools=new Toolchain(toolsDir);
        var officeDir=Path.Combine(dataDir,"office");Directory.CreateDirectory(officeDir);
        officeRuntime=new OfficeRuntime(officeDir);
    }
    public async Task<IReadOnlyList<string>> ConvertAsync(IReadOnlyList<string> inputs,string format,CancellationToken token)
    {
        if(inputs.Count==0)return [];
        var destination=DestinationFolder(inputs[0]);Directory.CreateDirectory(destination);
        var outputs=new List<string>();
        var imageInputs=inputs.Where(FeatureCatalog.IsImage).ToList();
        var documentInputs=inputs.Where(p=>!FeatureCatalog.IsImage(p)&&!FeatureCatalog.IsVideo(p)&&!FeatureCatalog.IsAudio(p)&&!FeatureCatalog.IsArchive(p)&&FeatureCatalog.Formats([p]).Length>0).ToList();
        var mediaInputs=inputs.Where(FeatureCatalog.IsVideo).Concat(inputs.Where(FeatureCatalog.IsAudio)).Distinct().ToList();
        var archiveInputs=inputs.Where(FeatureCatalog.IsArchive).ToList();
        var remaining=inputs.Except(imageInputs.Concat(documentInputs).Concat(mediaInputs).Concat(archiveInputs)).ToList();
        if(remaining.Count>0)throw new ArgumentException("Unsupported file type for headless conversion: "+Path.GetFileName(remaining[0]));
        var progress=new Progress<OperationProgress>(p=>Console.WriteLine($"{p.Label} {(p.Fraction is {} f?$"{f:P0}":"")}"));
        if(imageInputs.Count>0)
        {
            var imageOutputs=ImageEngine.Apply("convert",imageInputs,destination,new ImageOptions(),format,token,progress);
            outputs.AddRange(imageOutputs);
        }
        if(documentInputs.Count>0)
        {
            var office=officeRuntime.Executable;
            if(office is null)throw new InvalidOperationException("Office tools are not set up. Run Nashi once and complete media/office setup.");
            var docConversion=new DocumentConversion(officeRuntime,tools);
            var docOutputs=await docConversion.ConvertAsync(documentInputs,format,destination,new DocumentOptions(),progress,token);
            outputs.AddRange(docOutputs);
        }
        if(mediaInputs.Count>0)
        {
            if(!tools.Ready)throw new InvalidOperationException("Media tools are not set up. Run Nashi once and complete media tool setup.");
            var mediaEngine=new MediaEngine(tools.Executable("ffmpeg"),tools.Executable("ffprobe"));
            var mediaOptions=new MediaOptions();
            var mediaOutputs=await mediaEngine.ConvertAsync(mediaInputs,format,destination,mediaOptions,progress,token);
            outputs.AddRange(mediaOutputs);
        }
        if(archiveInputs.Count>0)
        {
            foreach(var input in archiveInputs)
            {
                token.ThrowIfCancellationRequested();
                var archiveOutputs=ArchiveEngine.Convert(input,format,destination,null,progress,token);
                outputs.AddRange(archiveOutputs);
            }
        }
        await ShowToastAsync(outputs,format);return outputs;
    }
    private string DestinationFolder(string firstInput)
    {
        var p=preferences.Value;
        if(p.OutputMode=="folder"&&!string.IsNullOrWhiteSpace(p.OutputFolder))return p.OutputFolder;
        if(p.OutputMode=="beside")return Path.GetDirectoryName(firstInput)!;
        // "ask" without a UI: fall back to beside the first file.
        return Path.GetDirectoryName(firstInput)!;
    }
    private Task ShowToastAsync(IReadOnlyList<string> outputs,string format)
    {
        var tcs=new TaskCompletionSource<bool>();
        // Headless: a hidden owner window anchors the toast at the bottom-right
        // of the primary screen so it is visible even when Nashi's main window
        // is not open.
        toastOwner=new Window{WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=System.Windows.Media.Brushes.Transparent,ShowInTaskbar=false,ShowActivated=false,Topmost=true,Width=0,Height=0,Left=0,Top=0};
        toastOwner.Show();
        var toast=new ToastWindow(toastOwner,null);
        var primaryW=SystemParameters.PrimaryScreenWidth;var primaryH=SystemParameters.PrimaryScreenHeight;
        toast.Left=primaryW-510-32;toast.Top=primaryH-200-32;
        toast.Show();
        var label=outputs.Count==0?"Dönüştürülmedi.":$"{outputs.Count} dosya {format.ToUpperInvariant()} olarak dönüştürüldü.";
        toast.Update(new(null,label));
        toast.Complete(outputs);
        // Auto-close: toast fades after 3 s, owner closes after 5 s, then the
        // task completes and ConvertAsync returns.
        var wait=new DispatcherTimer{Interval=TimeSpan.FromSeconds(5)};wait.Tick+=(_,_)=>{wait.Stop();toastOwner?.Close();tcs.TrySetResult(true);};wait.Start();
        return tcs.Task;
    }
}
