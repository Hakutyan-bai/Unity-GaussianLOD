using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatLOD.Editor
{
    public sealed class GaussianDeploymentWindow:EditorWindow
    {
        string json="Assets/StreamingAssets/GaussianSplatLOD/SUIT-20261027/lod-meta.json",output="",state="Ready",failure;
        float progress;Task export;CancellationTokenSource cancel;
        [MenuItem("Tools/Gaussian Splat LOD/PICO Deployment")]
        static void Open()=>GetWindow<GaussianDeploymentWindow>("PICO Deployment");
        void OnGUI()
        {
            json=EditorGUILayout.TextField("lod-meta.json",json);
            if(GUILayout.Button("Browse JSON")){var path=EditorUtility.OpenFilePanel("Select lod-meta.json","","json");if(!string.IsNullOrEmpty(path))json=path;}
            output=EditorGUILayout.TextField("Cache-only export folder",output);
            if(GUILayout.Button("Browse Export Folder")){var path=EditorUtility.OpenFolderPanel("Choose new or empty folder","","");if(!string.IsNullOrEmpty(path))output=path;}
            bool busy=GaussianDeploymentBuilder.IsBusy||(export!=null&&!export.IsCompleted);
            using(new EditorGUI.DisabledScope(busy)){
                if(GUILayout.Button("Prepare Lossless Cache + Deployment Index")){try{GaussianDeploymentBuilder.BeginPrepare(json);failure=null;}catch(Exception ex){failure=ex.Message;}}
                if(GUILayout.Button("Export Cache-only Dataset")){
                    try{string source=Path.GetFullPath(json),destination=Path.GetFullPath(output);cancel=new CancellationTokenSource();var token=cancel.Token;failure=null;state="Exporting";progress=0;
                        export=Task.Run(()=>{try{GaussianDeploymentBuilder.ExportCacheOnly(source,destination,token,p=>progress=p);state="Completed";}catch(OperationCanceledException){state="Cancelled";}catch(Exception ex){failure=ex.Message;state="Failed";}finally{cancel.Dispose();cancel=null;}});
                    }catch(Exception ex){failure=ex.Message;}
                }
            }
            if(busy&&GUILayout.Button("Cancel")){GaussianDeploymentBuilder.Cancel();cancel?.Cancel();}
            EditorGUILayout.HelpBox(GaussianDeploymentBuilder.IsBusy?GaussianDeploymentBuilder.Status+" "+GaussianDeploymentBuilder.Progress.ToString("P0"):state+" "+progress.ToString("P0"),MessageType.Info);
            string error=failure??GaussianDeploymentBuilder.Error;if(!string.IsNullOrEmpty(error))EditorGUILayout.HelpBox(error,MessageType.Error);
            EditorGUILayout.HelpBox("Lossless SH3 cache keeps the existing GPU layout. Export never clears a non-empty folder and never overwrites source PLY. Cancel before closing or recompiling.",MessageType.None);
            if(!string.IsNullOrEmpty(output)&&Directory.Exists(output)&&GUILayout.Button("Open Export Folder"))EditorUtility.RevealInFinder(output);
        }
        void Update(){if(GaussianDeploymentBuilder.IsBusy||(export!=null&&!export.IsCompleted))Repaint();}
    }
}
