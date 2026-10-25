using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace TiaMcp.OpcUa.Runtime.Tests;

/// <summary>Opaque TCP relay; only this fixture's owned loopback connections can be dropped.</summary>
internal sealed class LoopbackRelay : IAsyncDisposable
{
    private readonly TcpListener listener=new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource stop=new();
    private readonly ConcurrentDictionary<TcpClient,TcpClient> pairs=new();
    private readonly List<Task> connections=[];
    private readonly Task accept;
    internal string Endpoint {get;}
    internal int Dropped {get;private set;}
    internal LoopbackRelay(int targetPort)
    {
        listener.Start();Endpoint=$"opc.tcp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        accept=Accept(targetPort);
    }
    private async Task Accept(int port)
    {
        try {while(!stop.IsCancellationRequested)connections.Add(Forward(await listener.AcceptTcpClientAsync(stop.Token),port));}
        catch(OperationCanceledException) when(stop.IsCancellationRequested){}
    }
    private async Task Forward(TcpClient incoming,int port)
    {
        using(incoming)using(var outgoing=new TcpClient())
        {
            try
            {
                await outgoing.ConnectAsync(IPAddress.Loopback,port,stop.Token);pairs.TryAdd(incoming,outgoing);
                using var ended=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                async Task Pump(Stream source,Stream destination)
                {try {await source.CopyToAsync(destination,ended.Token);}catch(Exception e) when(e is IOException or OperationCanceledException or ObjectDisposedException){}finally{ended.Cancel();}}
                await Task.WhenAll(Pump(incoming.GetStream(),outgoing.GetStream()),Pump(outgoing.GetStream(),incoming.GetStream()));
            }
            catch(Exception e) when(e is SocketException or IOException or OperationCanceledException or ObjectDisposedException){}
            finally {pairs.TryRemove(incoming,out _);}
        }
    }
    internal void DropActive()
    {
        foreach(var pair in pairs.ToArray()){pair.Key.Dispose();pair.Value.Dispose();Dropped++;}
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();listener.Stop();DropActive();await accept;await Task.WhenAll(connections);stop.Dispose();
    }
}
