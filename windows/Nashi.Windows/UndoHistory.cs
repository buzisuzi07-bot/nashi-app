using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace Nashi.Windows;
// Undo history: every completed conversion is recorded here so the user can
// send its outputs back to the Recycle Bin (FO_ALLOWUNDO — never deleted
// outright). Kept in %LocalAppData%\Nashi, outside the portable installation.
public sealed class UndoHistory:IDisposable
{
    private const int Limit=20;
    private readonly string file;private State state=new([]);
    public UndoHistory(string directory)
    {
        Directory.CreateDirectory(directory);file=Path.Combine(directory,"undo-history.json");
        if(!File.Exists(file))return;
        try
        {
            if(new FileInfo(file).Length>4*1024*1024)throw new InvalidDataException("Undo history is too large.");
            var loaded=JsonSerializer.Deserialize<State>(File.ReadAllText(file))??throw new InvalidDataException("Empty undo history.");
            if(loaded.Records is null||loaded.Records.Any(r=>r is null||string.IsNullOrWhiteSpace(r.Action)||r.Sources is null||r.Outputs is null||r.Sources.Length==0||r.Outputs.Length==0||r.Sources.Any(p=>string.IsNullOrWhiteSpace(p)||!Path.IsPathFullyQualified(p))||r.Outputs.Any(p=>string.IsNullOrWhiteSpace(p)||!Path.IsPathFullyQualified(p))))throw new InvalidDataException("Invalid undo record.");
            state=new(loaded.Records.OrderByDescending(r=>r.Created).Take(Limit).ToList());
        }
        catch(Exception error)when(error is JsonException or InvalidDataException)
        {
            try{File.Copy(file,file+".recovered-"+Guid.NewGuid().ToString("N"));}catch(Exception backupError)when(backupError is IOException or UnauthorizedAccessException){}
            state=new([]);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){state=new([]);}
    }
    public IReadOnlyList<UndoRecord> Records=>state.Records;
    public void Record(string action,IReadOnlyList<string> sources,IReadOnlyList<string> outputs)
    {
        var src=sources.Where(p=>File.Exists(p)||Directory.Exists(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var outs=outputs.Where(p=>File.Exists(p)||Directory.Exists(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(src.Length==0||outs.Length==0)return;
        state=new(new[]{new UndoRecord(Guid.NewGuid(),DateTimeOffset.Now,action,src,outs)}.Concat(state.Records).Take(Limit).ToList());Save();
    }
    public bool Undo(Guid id)
    {
        var record=state.Records.FirstOrDefault(r=>r.Id==id);if(record is null)return false;
        var anyMoved=false;
        foreach(var output in record.Outputs)if(File.Exists(output)||Directory.Exists(output)){if(MoveToRecycleBin(output))anyMoved=true;}
        state=new(state.Records.Where(r=>r.Id!=id).ToList());Save();return anyMoved||record.Outputs.Length==0;
    }
    public void Delete(Guid id){state=new(state.Records.Where(r=>r.Id!=id).ToList());Save();}
    private void Save(){var temporary=file+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllText(temporary,JsonSerializer.Serialize(state));File.Move(temporary,file,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}}
    public void Dispose(){}
    // FO_DELETE + FOF_ALLOWUNDO sends the path to the Recycle Bin instead of
    // deleting it outright — the user can always get it back from there.
    private static bool MoveToRecycleBin(string path)
    {
        try
        {
            var from=path+"\0\0";var op=new SHFILEOPSTRUCT{hwnd=IntPtr.Zero,wFunc=3,pFrom=from,pTo="",fFlags=0x40,fAnyOperationsAborted=0,hNameMappings=IntPtr.Zero,lpszProgressTitle=""};return SHFileOperation(ref op)==0;
        }
        catch{return false;}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct SHFILEOPSTRUCT{public IntPtr hwnd;public uint wFunc;public string pFrom;public string pTo;public ushort fFlags;public int fAnyOperationsAborted;public IntPtr hNameMappings;public string lpszProgressTitle;}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
    private sealed record State(List<UndoRecord> Records);
}
public sealed record UndoRecord(Guid Id,DateTimeOffset Created,string Action,string[] Sources,string[] Outputs)
{
    public string Name=>Outputs.Length==1?Path.GetFileName(Outputs[0]):$"{Outputs.Length} files · {Path.GetFileName(Outputs[0])}";
}
