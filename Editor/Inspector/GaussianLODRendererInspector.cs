using System;
using System.IO;
using UnityEditor;
using UnityEngine;
namespace GaussianSplatLOD.Editor
{
    [CustomEditor(typeof(GaussianLODRenderer))]
    public sealed class GaussianLODRendererInspector : UnityEditor.Editor
    {
        string error;
        void Fields(params string[] names){foreach(string n in names)EditorGUILayout.PropertyField(serializedObject.FindProperty(n),true);}
        public override bool RequiresConstantRepaint()=>true;
        public override void OnInspectorGUI()
        {
            serializedObject.Update();var r=(GaussianLODRenderer)target;
            EditorGUILayout.LabelField("Model Configuration",EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();var json=(TextAsset)EditorGUILayout.ObjectField("JSON Configuration",r.jsonConfiguration,typeof(TextAsset),false);
            if(EditorGUI.EndChangeCheck()) {
                Undo.RecordObject(r,"Select Gaussian JSON");r.jsonConfiguration=json;
                if(json!=null)GaussianLODEditorUtility.AssignJson(r,AssetDatabase.GetAssetPath(json));serializedObject.Update();
            }
            Fields("dataLocation","jsonPath","loadOnEnable","targetCamera");
            if(GUILayout.Button("Browse Local JSON…")){string path=EditorUtility.OpenFilePanel("选择 lod-meta.json",r.DataDirectory,"json");if(!string.IsNullOrEmpty(path)){Undo.RecordObject(r,"Select Gaussian JSON");GaussianLODEditorUtility.AssignJson(r,path);serializedObject.Update();}}
            EditorGUILayout.LabelField("Data Directory",r.DataDirectory,EditorStyles.wordWrappedLabel);
            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.BeginHorizontal();if(GUILayout.Button("Load"))r.Load();if(GUILayout.Button("Reload"))r.Reload();if(GUILayout.Button("Unload"))r.Unload();EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Status",r.Status);
            if(r.Metadata!=null)EditorGUILayout.LabelField($"原始点数 {r.Metadata.counts:N0} | {r.Metadata.tree.Length} 分区 | {r.Metadata.files.Length} 文件 | {r.Metadata.levels} LOD | SH{r.Metadata.shDegree}");
            if(!string.IsNullOrEmpty(r.LastError))EditorGUILayout.HelpBox(r.LastError,MessageType.Error);
            if(r.dataLocation==GaussianDataLocation.ExternalDirectory) {
                EditorGUILayout.HelpBox("外部路径适合 Editor 调试。Windows 便携发布请复制到 StreamingAssets；JSON 与 PLY 保持原始目录结构。",MessageType.Info);
                if(GUILayout.Button("Copy Dataset Into StreamingAssets"))try{string p=GaussianLODEditorUtility.ImportDataset(r.DataDirectory);GaussianLODEditorUtility.AssignJson(r,p);error=null;}catch(Exception e){error=e.Message;}
            }
            if(error!=null)EditorGUILayout.HelpBox(error,MessageType.Error);
            serializedObject.Update();
            EditorGUILayout.Space();EditorGUILayout.LabelField("Rendering Settings",EditorStyles.boldLabel);
            Fields("enableRendering","enableDynamicLOD","coherentLOD","enableFrustumCulling","maxVisibleSplats","renderScale","opacityMultiplier","coordinateScale","positionOffset","rotationOffset");
            EditorGUILayout.Space();EditorGUILayout.LabelField("Streaming Settings (MiB)",EditorStyles.boldLabel);
            Fields("maxGPUMemory","maxCPUCache","preloadDistance","loadChunksPerFrame","enableAsyncLoading","preferRuntimeCache","maxConcurrentLoads","uploadSplatsPerFrame","lodHysteresis","lodTransitionSeconds");
            serializedObject.ApplyModifiedProperties();
            if(GUILayout.Button("Prepare Fast Loading Cache…"))try{GaussianRuntimeCacheWindow.Open(r.ResolveJsonPath());}catch(Exception e){error=e.Message;}
            EditorGUILayout.BeginHorizontal();
            if(GUILayout.Button("PICO 4 Ultra 起始预算")){Undo.RecordObject(r,"PICO loading preset");r.ApplyMobilePreset(true);EditorUtility.SetDirty(r);}
            if(GUILayout.Button("iPhone 14 起始预算")){Undo.RecordObject(r,"iPhone loading preset");r.ApplyMobilePreset(false);EditorUtility.SetDirty(r);}
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("移动预算用于真机调优起点；尚未验证 Android / Metal / XR。PICO APK 内的数据需先部署到 PersistentDataDirectory。缓存不改变 GPU 记录大小。",MessageType.Info);
            serializedObject.Update();
            EditorGUILayout.Space();EditorGUILayout.LabelField("Debug Settings",EditorStyles.boldLabel);Fields("showChunkBounds","showLODLevel","showStatistics","showLoadingStatus");serializedObject.ApplyModifiedProperties();
            if(r.Metadata!=null)EditorGUILayout.LabelField($"完整粗级覆盖 {r.CoveredNodes}/{r.Metadata.tree.Length} | 过渡 {r.TransitioningNodes} 分区 | 有效点数上限 {r.EffectivePointLimit:N0}");
            if(r.showStatistics)EditorGUILayout.HelpBox($"Camera callbacks {r.FPS:F1} FPS | {r.VisibleSplats:N0} splats | {r.VisibleChunks} chunks\nGPU allocated {r.GPUBytes/1048576f:F1} MiB | CPU decoded/reserved {r.CPUBytes/1048576f:F1} MiB\nPending {r.PendingLoads} | Resident {r.ResidentRanges} | Cache hit {r.CacheHitRate:P1} | Disk reads {r.DiskReads}\nFast cache reads {r.RuntimeCacheReads} | PLY decodes {r.PLYReads}\nStreaming tick {r.LastUploadMilliseconds:F2} ms"+(r.BudgetLimited?"\n预算限制了覆盖或细化，当前维持可用 LOD。":""),MessageType.Info);
        }
    }
}
