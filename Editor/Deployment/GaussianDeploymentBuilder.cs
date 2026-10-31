using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;

namespace GaussianSplatLOD.Editor
{
    /// <summary>Separate, cancellable offline preparation; never part of a Player build.</summary>
    public static class GaussianDeploymentBuilder
    {
        static Task work;
        static CancellationTokenSource cancellation;
        static volatile float progress;
        static volatile string status="Idle",error;
        public static bool IsBusy=>work!=null&&!work.IsCompleted;
        public static float Progress=>progress;
        public static string Status=>status;
        public static string Error=>error;
        public static void BeginPrepare(string jsonPath)
        {
            if(IsBusy)throw new InvalidOperationException("部署缓存正在准备。");
            string path=Path.GetFullPath(jsonPath);cancellation=new CancellationTokenSource();var token=cancellation.Token;
            progress=0;status="Preparing caches";error=null;
            work=Task.Run(()=>{
                var watch=Stopwatch.StartNew();
                try{PrepareDataset(path,token,p=>progress=p);status="Completed in "+watch.Elapsed.TotalSeconds.ToString("F1")+"s";}
                catch(OperationCanceledException){status="Cancelled";}
                catch(Exception ex){error=ex.ToString();status="Failed";}
                finally{cancellation.Dispose();cancellation=null;}
            });
        }
        public static void Cancel(){try{cancellation?.Cancel();}catch(ObjectDisposedException){}}
        [MenuItem("Tools/Gaussian Splat LOD/Prepare PICO Deployment Index")]
        static void Menu()
        {
            string path=EditorUtility.OpenFilePanel("选择 lod-meta.json","","json");if(!string.IsNullOrEmpty(path))BeginPrepare(path);
        }
        public static GaussianDeploymentManifest PrepareDataset(string json,CancellationToken token,Action<float> progress=null)
        {
            json=Path.GetFullPath(json);
            GaussianRuntimeCacheWindow.BuildDataset(json,token,p=>progress?.Invoke(p*.75f));
            var loader=new GaussianLODLoader(json,false);
            var index=new GaussianDeploymentManifest{jsonName=Path.GetFileName(json),jsonSha256=GaussianDeploymentManifest.HashFile(json,token),files=new GaussianDeploymentManifest.FileEntry[loader.Metadata.files.Length]};
            var identity=new StringBuilder(index.jsonSha256);
            for(int file=0;file<index.files.Length;file++)
            {
                token.ThrowIfCancellationRequested();string source=loader.FilePath(file),cache=source+GaussianRuntimeCache.Extension;
                var header=GaussianRuntimeCache.Validate(cache,source,loader.Metadata.shDegree);
                var ranges=new List<GaussianDeploymentManifest.RangeEntry>();
                for(int node=0;node<loader.Metadata.tree.Length;node++)
                    for(int lod=0;lod<loader.Metadata.levels;lod++)
                    {
                        var range=loader.Metadata.tree[node].lods[lod];if(range.file!=file)continue;
                        ranges.Add(new GaussianDeploymentManifest.RangeEntry{node=node,lod=lod,count=range.count,offset=range.offset,byteOffset=checked(GaussianRuntimeCache.HeaderSize+range.offset*GaussianSplat.Stride),bound=loader.Metadata.tree[node].bound});
                    }
                string relative=loader.Metadata.files[file]+GaussianRuntimeCache.Extension;
                string hash=GaussianDeploymentManifest.HashFile(cache,token);
                index.files[file]=new GaussianDeploymentManifest.FileEntry{file=file,source=loader.Metadata.files[file],artifact=relative,sha256=hash,length=new FileInfo(cache).Length,recordCount=header.Count,shDegree=header.SHDegree,ranges=ranges.ToArray()};
                identity.Append('|').Append(relative).Append(':').Append(hash);progress?.Invoke(.75f+.25f*(file+1)/index.files.Length);
            }
            index.datasetId=GaussianDeploymentManifest.HashText(identity.ToString());token.ThrowIfCancellationRequested();
            string target=Path.Combine(loader.Directory,GaussianDeploymentManifest.FileName),partial=target+".partial-"+Guid.NewGuid().ToString("N");
            try{File.WriteAllText(partial,JsonConvert.SerializeObject(index,Formatting.Indented));token.ThrowIfCancellationRequested();if(File.Exists(target))File.Replace(partial,target,null);else File.Move(partial,target);}
            finally{if(File.Exists(partial))File.Delete(partial);}
            return index;
        }
        public static void ExportCacheOnly(string json,string destination,CancellationToken token,Action<float> progress=null)
        {
            json=Path.GetFullPath(json);destination=Path.GetFullPath(destination);
            if(Directory.Exists(destination)&&Directory.GetFileSystemEntries(destination).Length!=0)throw new IOException("导出目录必须为空，不会清空已有数据。");
            var index=PrepareDataset(json,token,p=>progress?.Invoke(p*.5f));Directory.CreateDirectory(destination);
            string root=Path.GetDirectoryName(json);
            foreach(var file in index.files)
            {
                token.ThrowIfCancellationRequested();string source=GaussianDeploymentManifest.SafePath(root,file.artifact),target=GaussianDeploymentManifest.SafePath(destination,file.artifact);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using var input=File.OpenRead(source);using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write);
                byte[] bytes=new byte[1048576];int n;while((n=input.Read(bytes,0,bytes.Length))>0){token.ThrowIfCancellationRequested();output.Write(bytes,0,n);}
                progress?.Invoke(.5f+.5f*(file.file+1)/index.files.Length);
            }
            token.ThrowIfCancellationRequested();File.Copy(Path.Combine(root,GaussianDeploymentManifest.FileName),Path.Combine(destination,GaussianDeploymentManifest.FileName),false);
            // JSON is the commit marker: interrupted exports never publish a loadable dataset.
            token.ThrowIfCancellationRequested();File.Copy(json,Path.Combine(destination,Path.GetFileName(json)),false);
        }
    }
}
