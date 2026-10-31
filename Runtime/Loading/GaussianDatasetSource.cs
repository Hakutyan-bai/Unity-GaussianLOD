using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace GaussianSplatLOD
{
    /// <summary>
    /// File acquisition only, not a second LOD manager. Tick/Dispose are main-thread operations;
    /// OpenAsync/EnsureFileAsync queue shared downloads and perform ordinary file validation on workers.
    /// APK members are staged by whole file; Aholo offsets remain record offsets in the staged file.
    /// </summary>
    public sealed class GaussianDatasetSource : IDisposable
    {
        sealed class Download
        {
            public string Relative, Target, Temporary;
            public bool Optional;
            public GaussianDeploymentManifest.FileEntry Expected;
            public CancellationToken Cancellation;
            public readonly TaskCompletionSource<bool> Completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public UnityWebRequest Request;
            public Task Verification;
            public long VerifiedLength;
        }
        sealed class SharedFile
        {
            public CancellationTokenSource Cancellation;
            public Task Task;
            public int Consumers;
        }
        readonly ConcurrentQueue<Download> pending=new ConcurrentQueue<Download>();
        readonly List<Download> active=new List<Download>();
        readonly Dictionary<int,SharedFile> files=new Dictionary<int,SharedFile>();
        readonly object sync=new object();
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        readonly CancellationToken lifetimeToken;
        readonly int mainThread;
        readonly string sourceJson,baseUrl,jsonName,localDirectory;
        readonly bool preferCache,remote;
        GaussianLODMetadata metadata;
        GaussianDeploymentManifest manifest;
        string contentIdentity;
        volatile string status="Preparing metadata";
        volatile float progress;
        int pendingCount,disposed;
        long completedTransfers,transferredBytes,verifiedCacheHits;
        public long CompletedTransfers=>Interlocked.Read(ref completedTransfers);
        public long TransferredBytes=>Interlocked.Read(ref transferredBytes);
        public long VerifiedCacheHits=>Interlocked.Read(ref verifiedCacheHits);
        public string Status=>status;
        public float Progress=>progress;
        public int PendingFiles=>Math.Max(0,Volatile.Read(ref pendingCount));
        public int FileTransfers=>active.Count;
        public string LocalJsonPath=>Path.Combine(localDirectory,jsonName);
        public int MaxConcurrentTransfers { get; set; }=2;
        internal int ConsumerCount(int file){lock(sync)return files.TryGetValue(file,out var shared)?shared.Consumers:0;}

        public GaussianDatasetSource(string sourceJson,string persistentRoot,bool preferRuntimeCache=true)
        {
            if(string.IsNullOrWhiteSpace(sourceJson))throw new ArgumentException("JSON source is empty.");
            mainThread=Thread.CurrentThread.ManagedThreadId;lifetimeToken=lifetime.Token;this.sourceJson=sourceJson;preferCache=preferRuntimeCache;
            remote=sourceJson.Contains("://")||sourceJson.StartsWith("jar:",StringComparison.OrdinalIgnoreCase);
            if(!remote){var local=Path.GetFullPath(sourceJson);localDirectory=Path.GetDirectoryName(local);jsonName=Path.GetFileName(local);return;}
            int slash=sourceJson.LastIndexOf('/');if(slash<0)throw new ArgumentException("Source JSON must have a directory URL.");
            baseUrl=sourceJson.Substring(0,slash+1);jsonName=Uri.UnescapeDataString(sourceJson.Substring(slash+1));
            // Android's randomized APK installation directory is deliberately excluded from the cache key.
            int assets=sourceJson.IndexOf("!/assets/",StringComparison.Ordinal);
            string identity=assets>=0?sourceJson.Substring(assets+9):sourceJson;
            localDirectory=Path.Combine(Path.GetFullPath(persistentRoot),GaussianDeploymentManifest.HashText(identity).Substring(0,20));
            GaussianDeploymentManifest.SafePath(localDirectory,jsonName);
            Directory.CreateDirectory(localDirectory);
        }
        public Task<GaussianLODLoader> OpenAsync(CancellationToken cancellation)
        {
            return Task.Run(async()=>{
                if(!remote)return new GaussianLODLoader(LocalJsonPath,preferCache);
                using var linked=CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken,cancellation);
                var token=linked.Token;
                await DownloadAsync(jsonName,false,null,token,true).ConfigureAwait(false);
                bool hasIndex=await DownloadAsync(GaussianDeploymentManifest.FileName,true,null,token,true).ConfigureAwait(false);
                string index=Path.Combine(localDirectory,GaussianDeploymentManifest.FileName);
                if(!hasIndex&&File.Exists(index))File.Delete(index);
                metadata=GaussianLODMetadata.Parse(File.ReadAllText(LocalJsonPath));
                if(hasIndex){manifest=GaussianDeploymentManifest.Read(index);manifest.Validate(metadata,LocalJsonPath);contentIdentity=manifest.datasetId;}
                else contentIdentity=GaussianDeploymentManifest.HashFile(LocalJsonPath,token);
                token.ThrowIfCancellationRequested();status="Metadata ready; staging requested files";
                return new GaussianLODLoader(LocalJsonPath,preferCache,true,EnsureFileAsync);
            },cancellation);
        }
        async Task EnsureFileAsync(int file,CancellationToken cancellation)
        {
            SharedFile shared;
            lock(sync)
            {
                if(disposed!=0)throw new ObjectDisposedException(nameof(GaussianDatasetSource));
                if(!files.TryGetValue(file,out shared)||shared.Task.IsCanceled||shared.Task.IsFaulted||shared.Cancellation?.IsCancellationRequested==true)
                {
                    // Cancelling the final consumer precedes Download's next main-thread Tick.
                    // A new consumer must not inherit that still-pending, already-doomed task.
                    shared=new SharedFile{Cancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken)};
                    shared.Task=StageFileAsync(file,shared.Cancellation.Token);
                    var queued=shared;
                    _=shared.Task.ContinueWith(t=>{
                        if(t.IsFaulted){var observed=t.Exception;}
                        // Cancelled consumers can leave before the download resolves; they then
                        // have no finally block left to dispose this linked source on completion.
                        lock(sync)if(queued.Consumers==0){queued.Cancellation?.Dispose();queued.Cancellation=null;}
                    },TaskScheduler.Default);files[file]=shared;
                }
                shared.Consumers++;
            }
            try{await WithCancellation(shared.Task,cancellation).ConfigureAwait(false);}
            finally
            {
                lock(sync)
                {
                    shared.Consumers--;
                    if(shared.Consumers==0&&!shared.Task.IsCompleted)shared.Cancellation?.Cancel();
                    if(shared.Consumers==0&&shared.Task.IsCompleted){shared.Cancellation?.Dispose();shared.Cancellation=null;}
                }
            }
        }
        static async Task WithCancellation(Task task,CancellationToken token)
        {
            if(!token.CanBeCanceled){await task.ConfigureAwait(false);return;}
            var cancelled=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using(token.Register(()=>cancelled.TrySetResult(true)))
            {
                if(await Task.WhenAny(task,cancelled.Task).ConfigureAwait(false)!=task)throw new OperationCanceledException(token);
                await task.ConfigureAwait(false);token.ThrowIfCancellationRequested();
            }
        }
        async Task StageFileAsync(int file,CancellationToken cancellation)
        {
            if(file<0||file>=metadata.files.Length)throw new InvalidDataException("部署 file 索引越界。");
            string name=metadata.files[file];
            if(manifest!=null)
            {
                var entry=manifest.files[file];
                if(!preferCache&&entry.artifact.EndsWith(GaussianRuntimeCache.Extension,StringComparison.Ordinal))throw new InvalidDataException("此部署只提供 .gsbin，请启用 Prefer Runtime Cache。");
                await DownloadAsync(entry.artifact,false,entry,cancellation,false).ConfigureAwait(false);
                ValidateRecordFile(entry.artifact,file);return;
            }
            // Legacy datasets can be staged without the optional index; generated indexed exports
            // provide strong content validation and avoid repeat work across installation updates.
            if(preferCache&&await DownloadAsync(name+GaussianRuntimeCache.Extension,true,null,cancellation,false).ConfigureAwait(false))
            {
                try{ValidateRecordFile(name+GaussianRuntimeCache.Extension,file);return;}
                catch(IOException){}catch(InvalidDataException){}
                // Incompatible optional legacy caches fall back to the authoritative PLY.
                string stale=GaussianDeploymentManifest.SafePath(localDirectory,name+GaussianRuntimeCache.Extension);
                if(File.Exists(stale))File.Delete(stale);
            }
            await DownloadAsync(name,false,null,cancellation,false).ConfigureAwait(false);ValidateRecordFile(name,file);
        }
        void DeleteLegacyCache(string name)
        {
            string path=GaussianDeploymentManifest.SafePath(localDirectory,name+GaussianRuntimeCache.Extension);
            try{if(File.Exists(path))File.Delete(path);if(File.Exists(path+".verified"))File.Delete(path+".verified");}catch(IOException){}catch(UnauthorizedAccessException){}
        }
        void ValidateRecordFile(string artifact,int file)
        {
            string path=GaussianDeploymentManifest.SafePath(localDirectory,artifact);long count;
            if(artifact.EndsWith(GaussianRuntimeCache.Extension,StringComparison.Ordinal))count=GaussianRuntimeCache.Validate(path,null,metadata.shDegree).Count;
            else{var h=GaussianPLYReader.ReadHeader(path);if(h.SHDegree!=metadata.shDegree)throw new InvalidDataException("部署 PLY SH degree 不匹配。");count=h.VertexCount;}
            foreach(var node in metadata.tree)foreach(var lod in node.lods)if(lod.file==file&&lod.offset>count-lod.count)throw new InvalidDataException("部署文件记录数不足: "+artifact);
        }
        async Task<bool> DownloadAsync(string relative,bool optional,GaussianDeploymentManifest.FileEntry expected,CancellationToken cancellation,bool force)
        {
            cancellation.ThrowIfCancellationRequested();string target=GaussianDeploymentManifest.SafePath(localDirectory,relative);
            if(!force&&File.Exists(target))
            {
                if(expected==null)return true;
                string receipt=Receipt(expected,target);
                string marker=target+".verified";
                if(File.Exists(marker)&&File.ReadAllText(marker)==receipt){Interlocked.Increment(ref verifiedCacheHits);return true;}
                if(new FileInfo(target).Length==expected.length&&string.Equals(await GaussianDeploymentManifest.HashFileAsync(target,cancellation).ConfigureAwait(false),expected.sha256,StringComparison.OrdinalIgnoreCase))
                {File.WriteAllText(marker,receipt);Interlocked.Increment(ref verifiedCacheHits);return true;}
            }
            var item=new Download{Relative=relative,Target=target,Temporary=target+".partial-"+Guid.NewGuid().ToString("N"),Expected=expected,Optional=optional,Cancellation=cancellation};
            lock(sync)
            {
                if(disposed!=0)throw new ObjectDisposedException(nameof(GaussianDatasetSource));
                cancellation.ThrowIfCancellationRequested();pending.Enqueue(item);Interlocked.Increment(ref pendingCount);
            }
            return await item.Completion.Task.ConfigureAwait(false);
        }
        string Receipt(GaussianDeploymentManifest.FileEntry entry,string path)
        {
            return contentIdentity+":"+entry.sha256+":"+entry.length+":"+File.GetLastWriteTimeUtc(path).Ticks;
        }
        static string EscapeRelative(string relative)
        {
            var parts=relative.Split('/');for(int i=0;i<parts.Length;i++)parts[i]=Uri.EscapeDataString(parts[i]);return string.Join("/",parts);
        }
        public void Tick()
        {
            CheckMainThread();if(disposed!=0)return;
            for(int i=active.Count-1;i>=0;i--)
            {
                var item=active[i];
                if(item.Verification!=null)
                {
                    if(!item.Verification.IsCompleted)continue;
                    if(item.Verification.IsCanceled)item.Completion.TrySetCanceled();
                    else if(item.Verification.IsFaulted)item.Completion.TrySetException(item.Verification.Exception.GetBaseException());
                    else {Interlocked.Increment(ref completedTransfers);Interlocked.Add(ref transferredBytes,item.VerifiedLength);item.Completion.TrySetResult(true);}
                    DeletePartial(item);active.RemoveAt(i);continue;
                }
                if(item.Cancellation.IsCancellationRequested)
                {
                    item.Request.Abort();item.Request.Dispose();item.Request=null;DeletePartial(item);item.Completion.TrySetCanceled();active.RemoveAt(i);continue;
                }
                if(!item.Request.isDone)continue;
                bool success=item.Request.result==UnityWebRequest.Result.Success;
                string error=item.Request.error;long code=item.Request.responseCode;var result=item.Request.result;
                item.Request.Dispose();item.Request=null;
                if(!success)
                {
                    DeletePartial(item);active.RemoveAt(i);
                    if(item.Optional&&(code==404||(code==0&&result==UnityWebRequest.Result.ConnectionError)))item.Completion.TrySetResult(false);
                    else item.Completion.TrySetException(new IOException("获取部署文件失败: "+item.Relative+" ("+code+") "+error));
                    continue;
                }
                item.Verification=Task.Run(async()=>{
                    try
                    {
                        item.Cancellation.ThrowIfCancellationRequested();
                        if(item.Expected!=null)
                        {
                            if(new FileInfo(item.Temporary).Length!=item.Expected.length||!string.Equals(await GaussianDeploymentManifest.HashFileAsync(item.Temporary,item.Cancellation).ConfigureAwait(false),item.Expected.sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("部署文件 SHA256/长度校验失败: "+item.Relative);
                        }
                        item.Cancellation.ThrowIfCancellationRequested();
                        // The partial and final path share a filesystem. Publish only after complete validation.
                        if(File.Exists(item.Target))File.Replace(item.Temporary,item.Target,null);else File.Move(item.Temporary,item.Target);
                        if(item.Expected!=null)File.WriteAllText(item.Target+".verified",Receipt(item.Expected,item.Target));
                        // File-system failures belong to this observed task, never to main-thread Tick.
                        // In particular, a removed/inaccessible target must resolve the waiting request.
                        item.VerifiedLength=new FileInfo(item.Target).Length;
                    }
                    finally{DeletePartial(item);}
                },item.Cancellation);
            }
            while(active.Count<Math.Max(1,Math.Min(8,MaxConcurrentTransfers))&&pending.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref pendingCount);
                if(item.Cancellation.IsCancellationRequested){item.Completion.TrySetCanceled();continue;}
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Target));
                    if(item.Expected!=null)CheckDiskSpace(item.Target,item.Expected.length);
                    // UWR is created, started and disposed only on the main thread. No multi-GB buffers.
                    item.Request=new UnityWebRequest(baseUrl+EscapeRelative(item.Relative),UnityWebRequest.kHttpVerbGET,new DownloadHandlerFile(item.Temporary){removeFileOnAbort=true},null);
                    item.Request.SendWebRequest();active.Add(item);
                }
                catch(Exception ex){item.Request?.Dispose();DeletePartial(item);item.Completion.TrySetException(ex);}
            }
            float sum=0;foreach(var item in active)sum+=item.Request!=null?Math.Max(0,item.Request.downloadProgress):1;
            progress=active.Count==0?1:sum/active.Count;
            if(active.Count>0)status="Staging "+active.Count+" file(s), queued "+PendingFiles;
            else if(metadata!=null)status="Local cache ready";
        }
        static void CheckDiskSpace(string target,long bytes)
        {
            long available=-1;
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                using var stats=new UnityEngine.AndroidJavaObject("android.os.StatFs",Path.GetDirectoryName(target));
                available=stats.Call<long>("getAvailableBytes");
#else
                var drive=new DriveInfo(Path.GetPathRoot(target));if(drive.IsReady)available=drive.AvailableFreeSpace;
#endif
            }
            catch(NotSupportedException){}catch(UnauthorizedAccessException){}catch(IOException){}
#if UNITY_ANDROID && !UNITY_EDITOR
            catch(UnityEngine.AndroidJavaException){}
#endif
            if(available>=0&&available<bytes+16L*1024*1024)throw new IOException("部署缓存磁盘空间不足，当前文件需要 "+bytes+" 字节及 16 MiB 安全余量。");
        }
        static void DeletePartial(Download item){try{if(File.Exists(item.Temporary))File.Delete(item.Temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
        void CheckMainThread(){if(Thread.CurrentThread.ManagedThreadId!=mainThread)throw new InvalidOperationException("GaussianDatasetSource.Tick/Dispose must run on the Unity main thread.");}
        public void Dispose()
        {
            CheckMainThread();
            lock(sync)
            {
                if(Interlocked.Exchange(ref disposed,1)!=0)return;lifetime.Cancel();
                while(pending.TryDequeue(out var queued)){Interlocked.Decrement(ref pendingCount);queued.Completion.TrySetCanceled();DeletePartial(queued);}
            }
            foreach(var item in active)
            {
                item.Request?.Abort();item.Request?.Dispose();item.Request=null;item.Completion.TrySetCanceled();
                if(item.Verification!=null)_=item.Verification.ContinueWith(t=>{if(t.IsFaulted){var observed=t.Exception;}DeletePartial(item);},TaskScheduler.Default);
                else DeletePartial(item);
            }
            active.Clear();status="Unloaded";
            lifetime.Dispose();
        }
    }
}
