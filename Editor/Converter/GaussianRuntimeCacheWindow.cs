using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatLOD.Editor
{
    public sealed class GaussianRuntimeCacheWindow : EditorWindow
    {
        string jsonPath, message;
        CancellationTokenSource cancellation;
        Task work;
        volatile float progress;
        Stopwatch timer;
        [MenuItem("Tools/Gaussian Splat LOD/Prepare Fast Loading Cache")]
        static void Menu() => Open(null);
        public static void Open(string json)
        {
            var window=GetWindow<GaussianRuntimeCacheWindow>("Gaussian Fast Cache");
            if(window.work==null && !string.IsNullOrEmpty(json))window.jsonPath=json;
        }
        void OnGUI()
        {
            EditorGUILayout.HelpBox("离线生成无损 GPU 预解码缓存，跳过运行时的协方差/SH 转换。保留原 PLY 和 Aholo JSON；额外磁盘占用为每高斯 256 字节。转换逐文件、分批进行，可取消。",MessageType.Info);
            using(new EditorGUI.DisabledScope(work!=null))
            {
                jsonPath=EditorGUILayout.TextField("lod-meta.json",jsonPath);
                if(GUILayout.Button("Browse…")){string p=EditorUtility.OpenFilePanel("选择 lod-meta.json","","json");if(!string.IsNullOrEmpty(p))jsonPath=p;}
                if(GUILayout.Button("Prepare Cache"))StartBuild();
                if(GUILayout.Button("Export Cache-Only Dataset…")) {
                    string destination=EditorUtility.OpenFolderPanel("选择新的或空的模型输出目录","","");
                    if(!string.IsNullOrEmpty(destination))StartExport(destination);
                }
            }
            if(work!=null){EditorGUI.ProgressBar(GUILayoutUtility.GetRect(1,22),progress,$"{progress:P0} / {timer.Elapsed.TotalSeconds:F1}s");if(GUILayout.Button("Cancel"))cancellation.Cancel();}
            if(!string.IsNullOrEmpty(message))EditorGUILayout.HelpBox(message,MessageType.Info);
        }
        void StartExport(string destination)
        {
            try {
                string path=Path.GetFullPath(jsonPath);cancellation=new CancellationTokenSource();var token=cancellation.Token;timer=Stopwatch.StartNew();progress=0;message=null;
                work=Task.Run(()=>ExportDataset(path,destination,token,p=>progress=p),token);
            }catch(Exception e){message=e.Message;cancellation?.Dispose();cancellation=null;}
        }
        public static void ExportDataset(string json,string destination,CancellationToken token,Action<float> progress=null)
        {
            destination=Path.GetFullPath(destination);
            if(Directory.Exists(destination)&&Directory.GetFileSystemEntries(destination).Length!=0)throw new IOException("导出目录必须为空，不会清空已有数据。");
            var loader=new GaussianLODLoader(json,false);BuildDataset(json,token);
            Directory.CreateDirectory(destination);
            // JSON is the commit marker. A cancelled export never publishes a loadable manifest.
            for(int i=0;i<loader.Metadata.files.Length;i++) {
                token.ThrowIfCancellationRequested();
                string source=loader.FilePath(i)+GaussianRuntimeCache.Extension,target=Path.Combine(destination,loader.Metadata.files[i]+GaussianRuntimeCache.Extension);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using(var input=File.OpenRead(source))using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write)) {
                    var bytes=new byte[1024*1024];int n;
                    while((n=input.Read(bytes,0,bytes.Length))>0){token.ThrowIfCancellationRequested();output.Write(bytes,0,n);}
                }
                progress?.Invoke((i+1f)/loader.Metadata.files.Length);
            }
            token.ThrowIfCancellationRequested();File.Copy(json,Path.Combine(destination,Path.GetFileName(json)),false);
        }
        void StartBuild()
        {
            try {
                string path=Path.GetFullPath(jsonPath);cancellation=new CancellationTokenSource();var token=cancellation.Token;timer=Stopwatch.StartNew();progress=0;message=null;
                work=Task.Run(()=>BuildDataset(path,token,p=>progress=p),token);
            }catch(Exception e){message=e.Message;cancellation?.Dispose();cancellation=null;}
        }
        public static void BuildDataset(string json, CancellationToken token, Action<float> progress=null)
        {
            var loader=new GaussianLODLoader(json,false);
            for(int i=0;i<loader.Metadata.files.Length;i++)
            {
                token.ThrowIfCancellationRequested();int file=i;string source=loader.FilePath(i),cache=source+GaussianRuntimeCache.Extension;
                bool valid=false;
                try {GaussianRuntimeCache.Validate(cache,source,loader.Metadata.shDegree);valid=true;}catch(IOException){}catch(InvalidDataException){}
                if(!valid)GaussianRuntimeCache.Build(source,cache,token,p=>progress?.Invoke((file+p)/loader.Metadata.files.Length));
                progress?.Invoke((i+1f)/loader.Metadata.files.Length);
            }
        }
        void Update()
        {
            if(work==null)return;
            Repaint();if(!work.IsCompleted)return;
            message=work.IsCanceled?"已取消；完成的缓存保留，原 PLY 不变。":work.IsFaulted?work.Exception.GetBaseException().Message:$"缓存已准备完成，耗时 {timer.Elapsed.TotalSeconds:F1}s。Renderer Reload 后优先使用缓存。";
            work=null;cancellation.Dispose();cancellation=null;AssetDatabase.Refresh();
        }
        void OnDisable()
        {
            cancellation?.Cancel();
            if(work!=null){var c=cancellation;_=work.ContinueWith(t=>{if(t.IsFaulted){var ignored=t.Exception;}c.Dispose();},TaskScheduler.Default);work=null;cancellation=null;}
        }
    }
}
