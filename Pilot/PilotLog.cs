using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NecoArcDesktopPet.Pilot;

internal sealed class PilotLog : IDisposable
{
    private readonly StreamWriter? writer;
    private readonly bool optional;
    private readonly Channel<object> queue=Channel.CreateBounded<object>(new BoundedChannelOptions(2048){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly Task drain=Task.CompletedTask;
    private bool disposed;
    public string Path { get; }="";
    public bool IsEnabled => writer!=null && !disposed && !drain.IsCompleted;
    public PilotLog() : this(null) { }
    public PilotLog(string? path) : this(path,false) { }
    // Product diagnostics are best effort. Pilot's public constructors retain
    // their original strict evidence and default-directory contract.
    internal static PilotLog CreateOptional(string? path)
    {
        try { return new PilotLog(path,true); }
        catch (Exception) { return new PilotLog(null,true); }
    }
    private PilotLog(string? path,bool optional)
    {
        this.optional=optional;
        if(optional && path==null)return;
        var directory=System.IO.Path.Combine(AppContext.BaseDirectory,"pilot-logs");
        if(!optional)Directory.CreateDirectory(directory);
        Path=path==null ? System.IO.Path.Combine(directory,$"pilot-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}.jsonl") : System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        writer=new StreamWriter(new FileStream(Path,FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false));
        drain=Task.Run(async ()=> {
            try {
            var options=new JsonSerializerOptions{IncludeFields=true};int batch=0;
            await foreach(var row in queue.Reader.ReadAllAsync()) {
                await writer.WriteLineAsync(JsonSerializer.Serialize(row,options));
                if(++batch>=64 || !queue.Reader.TryPeek(out _)){await writer.FlushAsync();batch=0;}
            }
            await writer.FlushAsync();
            } catch (Exception) when(optional) { /* Disable this diagnostic sink. */ }
        });
    }
    public void Write(string kind,object? data,string verdict="OBSERVED") {
        if(optional) {
            if(IsEnabled)queue.Writer.TryWrite(new{utc=DateTime.UtcNow,kind,verdict,data});
            return;
        }
        if(disposed || drain.IsFaulted || !queue.Writer.TryWrite(new{utc=DateTime.UtcNow,kind,verdict,data}))
            throw new InvalidOperationException("Pilot evidence writer failed/full/closed; fail-stop instead of dropping a critical event.",drain.Exception);
    }
    public void Dispose() {
        if(disposed)return;disposed=true;queue.Writer.TryComplete();
        try {drain.GetAwaiter().GetResult();} catch(Exception) when(optional) { }
        finally {try {writer?.Dispose();} catch(Exception) when(optional) { }}
    }
}
