using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatLOD.Editor
{
    public sealed class GaussianModelConverterWindow : EditorWindow
    {
        string source="",output="",executable="";
        int levels=5,maxPoints=400000;
        bool generate=true,reported;
        Vector2 scroll;
        readonly StringBuilder log=new StringBuilder();
        AholoConversionJob job;
        [MenuItem("Tools/Gaussian Splat LOD/Model Converter")]
        public static void Open()=>GetWindow<GaussianModelConverterWindow>("Gaussian Model Converter");
        void OnEnable(){minSize=new Vector2(620,470);executable=GaussianLODEditorUtility.NativeExecutable;EditorApplication.update+=Poll;AssemblyReloadEvents.beforeAssemblyReload+=Cancel;}
        void OnDisable(){EditorApplication.update-=Poll;AssemblyReloadEvents.beforeAssemblyReload-=Cancel;Cancel();}
        void Cancel()=>job?.Dispose();
        void Poll()
        {
            if(job==null)return;
            while(job.Logs.TryDequeue(out string line)){if(log.Length>2*1024*1024)log.Remove(0,1024*1024);log.AppendLine(line);}
            if(job.Work.IsCompleted&&!reported){reported=true;if(job.Work.IsFaulted)log.AppendLine(job.Work.Exception.GetBaseException().ToString());if(!job.Work.IsFaulted&&!job.Canceled)AssetDatabase.Refresh();}
            if(!reported)Repaint();
        }
        static void PathField(string label,ref string path,bool directory)
        {
            EditorGUILayout.BeginHorizontal();path=EditorGUILayout.TextField(label,path);
            if(GUILayout.Button("Browse",GUILayout.Width(70))){string p=directory?EditorUtility.OpenFolderPanel(label,path,""):EditorUtility.OpenFilePanel(label,Path.GetDirectoryName(path)??"","ply");if(!string.IsNullOrEmpty(p))path=p;}
            EditorGUILayout.EndHorizontal();
        }
        void OnGUI()
        {
            EditorGUILayout.LabelField("Aholo AutoChunkLod — Native Windows x64",EditorStyles.boldLabel);
            bool running=job!=null&&!job.Work.IsCompleted;
            using(new EditorGUI.DisabledScope(running))
            {
                PathField("Source PLY",ref source,false);PathField("Output Directory",ref output,true);
                EditorGUILayout.HelpBox("可填写尚不存在的输出目录。已有目录必须为空；不会自动清空。转换失败或取消时，.partial 临时数据保留供检查。",MessageType.Info);
                levels=EditorGUILayout.IntSlider("LOD Levels",levels,1,8);maxPoints=Mathf.Max(1,EditorGUILayout.IntField("Max Chunk Points",maxPoints));
                EditorGUILayout.Popup("Output Format",0,new[]{"PLY (binary, full SH)"});generate=EditorGUILayout.Toggle("Generate LOD",generate);
                EditorGUILayout.LabelField("Precision","100%, 50%, 25%, 5%, 1% (随后 0.5%, 0.25%, 0.1%)");
                executable=EditorGUILayout.TextField("Native Executable",executable);
                if(GUILayout.Button("Convert",GUILayout.Height(30)))try{job?.Dispose();log.Clear();reported=false;job=new AholoConversionJob(executable,source,output,levels,maxPoints,generate);}catch(Exception e){log.AppendLine(e.Message);}
            }
            using(new EditorGUI.DisabledScope(!running))if(GUILayout.Button("Cancel"))job.Cancel();
            if(job!=null){Rect rect=EditorGUILayout.GetControlRect(false,20);EditorGUI.ProgressBar(rect,job.Progress,$"{job.ElapsedSeconds:F1}s — {(job.Work.IsCompleted?(job.Canceled?"Canceled":job.Work.IsFaulted?"Failed":"Complete"):"Processing (stage estimate)")}");}
            scroll=EditorGUILayout.BeginScrollView(scroll);EditorGUILayout.TextArea(log.ToString(),GUILayout.ExpandHeight(true));EditorGUILayout.EndScrollView();
            using(new EditorGUI.DisabledScope(running||!File.Exists(Path.Combine(output,"lod-meta.json"))))
            {
                EditorGUILayout.BeginHorizontal();
                if(GUILayout.Button("Open Output Folder"))EditorUtility.RevealInFinder(Path.Combine(output,"lod-meta.json"));
                if(GUILayout.Button("Import Into Project"))try{string imported=GaussianLODEditorUtility.ImportDataset(output);log.AppendLine("Imported: "+imported);}catch(Exception e){log.AppendLine(e.Message);}
                if(GUILayout.Button("Create Renderer In Scene"))GaussianLODEditorUtility.CreateRenderer(Path.Combine(output,"lod-meta.json"));
                EditorGUILayout.EndHorizontal();
            }
        }
    }
}
