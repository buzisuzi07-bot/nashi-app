using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Nashi.Core;
namespace Nashi.Windows;
public partial class App:Application
{
    private SingleInstance? instance;
    internal static bool IsSmokeTest{get;private set;}
    private WindowManagement? windowManagement;
    protected override async void OnStartup(StartupEventArgs e)
    {
        if(Environment.GetEnvironmentVariable("NASHI_SMOKE_DIRECTORY") is {} diagnostics)
        {
            Directory.CreateDirectory(diagnostics);
            AppDomain.CurrentDomain.UnhandledException+=(_,error)=>{
                CrashGuard.Record(error.ExceptionObject as Exception ?? new Exception(error.ExceptionObject.ToString()));
                File.WriteAllText(Path.Combine(diagnostics,$"unhandled-{Environment.ProcessId}.txt"),error.ExceptionObject.ToString());
            };
            Exit+=(_,exit)=>File.WriteAllText(Path.Combine(diagnostics,$"process-exit-{Environment.ProcessId}.json"),System.Text.Json.JsonSerializer.Serialize(new{mode=e.Args.FirstOrDefault(),code=exit.ApplicationExitCode,environmentCode=Environment.ExitCode}));
        }
        DispatcherUnhandledException+=(_,error)=>{
            CrashGuard.Record(error.Exception);
            error.Handled=true;
            if(Environment.GetEnvironmentVariable("NASHI_SMOKE_DIRECTORY") is {} folder){
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder,"dispatcher-error.txt"),error.Exception.ToString());
            }
            var result=MessageBox.Show($"Otto beklenmedik bir hatayla karşılaştı:\n{error.Exception.Message}\n\nUygulamayı yeniden başlatmak ister misiniz?","Otto — Çöküş",MessageBoxButton.YesNo,MessageBoxImage.Error);
            if(result==MessageBoxResult.Yes){
                try{Process.Start(new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true})?.Dispose();}catch{}
            }
            Shutdown(1);
        };
        base.OnStartup(e);
        if(e.Args.Contains("--smoke-test-interactions"))
        {
            IsSmokeTest=true;ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var directory=Environment.GetEnvironmentVariable("NASHI_SMOKE_DIRECTORY")??Path.Combine(Path.GetTempPath(),"Nashi-Interactions-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            var failures=new List<string>();var checks=0;var finished=0;
            void Report(IEnumerable<string> errors)=>File.WriteAllText(Path.Combine(directory,"interactions.json"),System.Text.Json.JsonSerializer.Serialize(new{checks=Volatile.Read(ref checks),failures=errors}));
            using var watchdog=new System.Threading.Timer(_=>
            {
                if(Interlocked.CompareExchange(ref finished,1,0)!=0)return;
                try{Report(["Focused native interaction checks timed out."]);}
                finally
                {
                    try{DesktopInput.HoldMouse(false);DesktopInput.Hold(System.Windows.Input.Key.LeftCtrl,false);}catch{}
                    Environment.Exit(1);
                }
            },null,Timeout.Infinite,Timeout.Infinite);
            try
            {
                var timeout=120;var option=Array.IndexOf(e.Args,"--timeout-seconds");
                if(option>=0&&(option+1>=e.Args.Length||!int.TryParse(e.Args[option+1],out timeout)||timeout is <1 or >120))throw new ArgumentException("Interaction timeout must be between 1 and 120 seconds.");
                watchdog.Change(TimeSpan.FromSeconds(timeout),Timeout.InfiniteTimeSpan);
                InteractionChecks.Run(directory,(passed,message)=>{Interlocked.Increment(ref checks);if(!passed)failures.Add(message);});
            }
            catch(Exception error){failures.Add(error.ToString());}
            if(Interlocked.CompareExchange(ref finished,1,0)==0){Report(failures);Shutdown(failures.Count==0?0:1);}return;
        }
        if(e.Args.Contains("--ui-audit"))
        {
            IsSmokeTest=true;ShutdownMode=ShutdownMode.OnExplicitShutdown;
            try
            {
                var index=Array.IndexOf(e.Args,"--ui-audit");
                if(index+1>=e.Args.Length||e.Args[index+1].StartsWith("--"))throw new ArgumentException("Use --ui-audit OUTPUT [--scenes scene,...] [--appearance light|dark|both] [--language en|tr|both]");
                string[]? scenes=null,appearances=null,languages=null;
                for(var next=index+2;next<e.Args.Length;next+=2)
                {
                    if(next+1>=e.Args.Length)throw new ArgumentException("Missing value for "+e.Args[next]);
                    var value=e.Args[next+1];
                    switch(e.Args[next])
                    {
                        case "--scenes":scenes=value.Split(',');break;
                        case "--appearance":appearances=value=="both"?null:[value];break;
                        case "--language":languages=value=="both"?null:[value];break;
                        default:throw new ArgumentException("Unknown audit argument: "+e.Args[next]);
                    }
                }
                Shutdown(UIAudit.Run(e.Args[index+1],scenes,appearances,languages));
            }catch(Exception error){Console.Error.WriteLine(error.Message);Shutdown(64);}
            return;
        }
        if(e.Args.Contains("--window-management-fixture"))
        {
            var key=Array.IndexOf(e.Args,"--minimum-window-width");var minimum=key>=0&&key+1<e.Args.Length&&int.TryParse(e.Args[key+1],out var parsed)?Math.Clamp(parsed,160,1200):160;
            new Window{Title=minimum>620?"Nashi Constrained Layout Fixture":"Nashi Window Layout Fixture",Width=Math.Max(620,minimum),MinWidth=minimum,Height=420,Left=80,Top=90,WindowStartupLocation=WindowStartupLocation.Manual,
                Content=new System.Windows.Controls.TextBlock{Text="Nashi window arrangement test fixture",Margin=new Thickness(30),FontSize=22}}.Show();return;
        }
        if(e.Args.Length==2&&e.Args[0]=="--apply-update"){try{Shutdown(UpdateInstaller.Apply(e.Args[1]));}catch(Exception error){UseSavedLanguage();MessageBox.Show(L10n.Text(error.Message),L10n.Text("Otto update could not finish"));Shutdown(1);}return;}
        if(e.Args.Contains("--verify-update-state")){try{ShutdownMode=ShutdownMode.OnExplicitShutdown;SmokeTests.VerifyUpdateState();Shutdown(0);}catch(Exception error){Console.Error.WriteLine(error);Shutdown(1);}return;}
        if(e.Args.Contains("--smoke-test")){IsSmokeTest=true;try{NativeExceptionTrace.Install();SmokeTests.Run();Shutdown(0);}catch(Exception error){Console.Error.WriteLine(error);var directory=Environment.GetEnvironmentVariable("NASHI_SMOKE_DIRECTORY");if(directory is not null){Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"error.txt"),error.ToString());}Shutdown(1);}return;}
        if(e.Args.Length==1&&e.Args[0]=="--install-update"){ShutdownMode=ShutdownMode.OnExplicitShutdown;_ = InstallUpdateAsync();return;}
        // Headless conversion: Nashi.exe --convert FORMAT [--files-from LIST] FILE...
        // Converts without opening the main window, uses saved preferences, shows a toast,
        // and exits when done. --wheel opens the tools wheel at the cursor.
        var convertIndex=Array.IndexOf(e.Args,"--convert");var wheelIndex=Array.IndexOf(e.Args,"--wheel");
        if(convertIndex>=0||wheelIndex>=0)
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;instance=new SingleInstance();Exit+=(_,_)=>instance.Dispose();
            if(!instance.Primary){Shutdown(0);return;}
            if(convertIndex>=0)
            {
                try
                {
                    if(convertIndex+1>=e.Args.Length||e.Args[convertIndex+1].StartsWith("--"))throw new ArgumentException("Use --convert FORMAT FILE... [--files-from LIST].");
                    var format=e.Args[convertIndex+1];var inputs=new List<string>();string? filesFrom=null;
                    for(var i=convertIndex+2;i<e.Args.Length;i++)
                    {
                        if(e.Args[i]=="--files-from"&&i+1<e.Args.Length){filesFrom=e.Args[++i];continue;}
                        inputs.Add(e.Args[i]);
                    }
                    if(filesFrom is not null)
                    {
                        foreach(var line in File.ReadAllLines(filesFrom)){var p=line.Trim();if(p.Length>0&&!p.StartsWith("#"))inputs.Add(p);}
                    }
                    if(inputs.Count==0)throw new ArgumentException("No input files for --convert.");
                    var headless=new HeadlessConversion();var outputs=await headless.ConvertAsync(inputs,format,CancellationToken.None);
                    Shutdown(outputs.Count>0?0:1);return;
                }
                catch(Exception error){Console.Error.WriteLine(error.Message);Shutdown(1);return;}
            }
            if(wheelIndex>=0)
            {
                // --wheel: open the tools wheel at the cursor and exit.
                try
                {
                    var wheelPrefs=new PreferencesStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Otto"));
                    var wheel=new GlobalDropWheel("tools",wheelPrefs.Value.WheelScale,(_,_)=>{},()=>null);
                    wheel.Show();wheel.UpdateLayout();
                    var closed=false;wheel.Closed+=(_,_)=>{closed=true;Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);};
                    var close=new DispatcherTimer{Interval=TimeSpan.FromSeconds(30)};close.Tick+=(_,_)=>{close.Stop();if(!closed){wheel.Close();}};close.Start();
                    while(!closed)DesktopInput.Pump();Shutdown(0);return;
                }
                catch(Exception error){Console.Error.WriteLine(error.Message);Shutdown(1);return;}
            }
        }
        instance=new SingleInstance();Exit+=(_,_)=>instance.Dispose();var paths=e.Args.Where(p=>File.Exists(p)||Directory.Exists(p)).ToArray();
        if(!instance.Primary){ShutdownMode=ShutdownMode.OnExplicitShutdown;_ = ForwardAndExit(paths);return;}
        // Float shelves have no owner. Closing Nashi (not hiding it to the tray) must end the
        // process instead of leaving it behind a shelf with a closed main window.
        var window=new MainWindow(paths,offerToolSetup:!e.Args.Contains("--background"));MainWindow=window;ShutdownMode=ShutdownMode.OnMainWindowClose;instance.Start(incoming=>Dispatcher.BeginInvoke(()=>window.AcceptLaunch(incoming)));window.Show();
        windowManagement=new WindowManagement(notice:message=>MessageBox.Show(L10n.Text(message),L10n.Text("Window arrangement"),MessageBoxButton.OK,MessageBoxImage.Information),isEnabled:()=>window.WindowManagementEnabled);
    }
    internal static void UseSavedLanguage(Func<string>? readLanguage=null)
    {
        L10n.Language="en";
        try{L10n.Language=readLanguage is null?new Nashi.Core.PreferencesStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Otto")).Value.Language:readLanguage();}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException){/* Preserve the original error dialog in English. */}
    }
    private async Task ForwardAndExit(string[] paths)
    {
        try{await instance!.Notify(paths);Shutdown(0);}catch(Exception error)when(error is IOException or OperationCanceledException or UnauthorizedAccessException){UseSavedLanguage();MessageBox.Show(L10n.Text("Otto is already running but is still starting. Open it from the system tray or try again in a moment."),"Otto");Shutdown(1);}
    }
    protected override void OnExit(ExitEventArgs e){windowManagement?.Dispose();Feedback.Stop();PdfRasterDocument.Shutdown();base.OnExit(e);}
    private async Task InstallUpdateAsync()
    {
        try
        {
            var update=await Nashi.Core.PublicUpdates.CheckAsync(UpdateInstaller.Current);
            if(update is not null)await UpdateInstaller.StageAsync(update,new Progress<Nashi.Core.OperationProgress>(),CancellationToken.None);
            Shutdown(0);
        }
        catch(Exception error)
        {
            var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Otto");Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"update-error.txt"),error.ToString());Shutdown(1);
        }
    }
}
