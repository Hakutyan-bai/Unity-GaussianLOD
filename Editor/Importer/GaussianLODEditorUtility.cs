using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GaussianSplatLOD.Editor
{
    [InitializeOnLoad]
    public static class GaussianLODEditorUtility
    {
        public const string PackageRoot="Packages/com.yourcompany.gaussian-splat-lod";
        public static string NativeExecutable
        {
            get {
                var package=UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(GaussianLODEditorUtility).Assembly);
                string root=package?.resolvedPath;
                if(string.IsNullOrEmpty(root)) {
                    foreach(string guid in AssetDatabase.FindAssets("GaussianLODEditorUtility t:MonoScript")) {
                        string script=AssetDatabase.GUIDToAssetPath(guid);
                        if(script.EndsWith("/Editor/Importer/GaussianLODEditorUtility.cs",StringComparison.Ordinal)) {
                            root=Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(script)));break;
                        }
                    }
                }
                if(string.IsNullOrEmpty(root))root=PackageRoot;
                string native=Path.GetFullPath(Path.Combine(root,"Editor/Converter/Native~/splat-transform.exe"));
                if(File.Exists(native))return native;
                // .unitypackage imports Assets, where ignored Native~ files cannot be exported.
                // Ship an Editor-only .bytes asset and extract the executable on demand.
                string bytes=Path.GetFullPath(Path.Combine(root,"Editor/Converter/Native/splat-transform.exe.bytes"));
                if(!File.Exists(bytes))throw new FileNotFoundException("Aholo 原生转换器缺失，请重新导入完整插件资源包。",bytes);
                native=Path.GetFullPath("Library/GaussianSplatLOD/Native/2.1.0/splat-transform.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(native));
                if(!File.Exists(native)||new FileInfo(native).Length!=new FileInfo(bytes).Length||File.GetLastWriteTimeUtc(native)!=File.GetLastWriteTimeUtc(bytes))File.Copy(bytes,native,true);
                return native;
            }
        }
        static double nextPreview;
        static GaussianLODEditorUtility()
        {
            GaussianLODRenderer.EditorCameraProvider=()=>SceneView.lastActiveSceneView!=null?SceneView.lastActiveSceneView.camera:Camera.main;
            EditorApplication.update+=UpdatePreview;
            AssemblyReloadEvents.beforeAssemblyReload+=Release;
            EditorApplication.quitting+=Release;
            EditorApplication.playModeStateChanged+=s=>{if(s==PlayModeStateChange.ExitingEditMode||s==PlayModeStateChange.ExitingPlayMode)Release();if(s==PlayModeStateChange.EnteredEditMode||s==PlayModeStateChange.EnteredPlayMode)foreach(var r in UnityEngine.Object.FindObjectsOfType<GaussianLODRenderer>())if(r.loadOnEnable)r.Load();};
        }
        static void Release(){foreach(var r in UnityEngine.Object.FindObjectsOfType<GaussianLODRenderer>())r.Unload();}
        static void UpdatePreview()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling||EditorApplication.timeSinceStartup<nextPreview)return;
            nextPreview=EditorApplication.timeSinceStartup+1.0/30;
            bool repaint=false;
            foreach(var r in UnityEngine.Object.FindObjectsOfType<GaussianLODRenderer>())if(r.isActiveAndEnabled){r.Tick(r.targetCamera!=null?r.targetCamera:GaussianLODRenderer.EditorCameraProvider?.Invoke());if(r.Status!="Unloaded")repaint=true;}
            if(repaint){EditorApplication.QueuePlayerLoopUpdate();SceneView.RepaintAll();}
        }
        public static void AssignJson(GaussianLODRenderer r,string fullPath)
        {
            fullPath=Path.GetFullPath(fullPath);string streaming=Path.GetFullPath(Application.streamingAssetsPath).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(fullPath.StartsWith(streaming,StringComparison.OrdinalIgnoreCase)){r.dataLocation=GaussianDataLocation.StreamingAssets;r.jsonPath=fullPath.Substring(streaming.Length).Replace('\\','/');}
            else{r.dataLocation=GaussianDataLocation.ExternalDirectory;r.jsonPath=fullPath;}
            string project=Path.GetFullPath(".").TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(fullPath.StartsWith(project,StringComparison.OrdinalIgnoreCase))r.jsonConfiguration=AssetDatabase.LoadAssetAtPath<TextAsset>(fullPath.Substring(project.Length).Replace('\\','/'));
            else r.jsonConfiguration=null;
            EditorUtility.SetDirty(r);
        }
        [MenuItem("Tools/Gaussian Splat LOD/Create Renderer")]
        static void CreateMenu()=>CreateRenderer(null);
        [MenuItem("Tools/Gaussian Splat LOD/Enable FPS Roaming")]
        public static void EnableFPSRoaming()
        {
            var r=Selection.activeGameObject!=null?Selection.activeGameObject.GetComponent<GaussianLODRenderer>():null;
            if(r==null)r=UnityEngine.Object.FindObjectOfType<GaussianLODRenderer>();
            var camera=r!=null&&r.targetCamera!=null?r.targetCamera:Camera.main;
            if(camera==null){var cameras=UnityEngine.Object.FindObjectsOfType<Camera>();foreach(var candidate in cameras)if(candidate.enabled&&candidate.cameraType==CameraType.Game){if(camera!=null)throw new InvalidOperationException("找到多台相机，请指定 Target Camera。");camera=candidate;}}
            if(camera==null)throw new InvalidOperationException("请指定 Target Camera 或将相机标记为 MainCamera。");
            if(r!=null&&r.targetCamera==null){Undo.RecordObject(r,"Set FPS Camera");r.targetCamera=camera;EditorUtility.SetDirty(r);}
            var controls=camera.GetComponent<GaussianLODDemoCamera>()??Undo.AddComponent<GaussianLODDemoCamera>(camera.gameObject);
            Undo.RecordObject(controls,"Enable FPS Roaming");controls.model=r;controls.lockCursorOnStart=true;controls.enabled=true;EditorUtility.SetDirty(controls);EditorSceneManager.MarkSceneDirty(camera.gameObject.scene);
        }
        public static GaussianLODRenderer CreateRenderer(string json)
        {
            var go=new GameObject("Gaussian LOD Model");Undo.RegisterCreatedObjectUndo(go,"Create Gaussian LOD Renderer");
            var r=Undo.AddComponent<GaussianLODRenderer>(go);
            if(!string.IsNullOrEmpty(json))AssignJson(r,json);
            r.SetShaderAssets(Resources.Load<Shader>("GaussianLOD"),Resources.Load<ComputeShader>("GaussianLODSort"));
            Selection.activeGameObject=go;EditorSceneManager.MarkSceneDirty(go.scene);if(!string.IsNullOrEmpty(json))r.Load();return r;
        }
        public static string ImportDataset(string directory)
        {
            var loader=new GaussianLODLoader(Path.Combine(directory,"lod-meta.json"));loader.ValidateAllRanges();
            string root=Path.Combine(Application.streamingAssetsPath,"GaussianSplatLOD");Directory.CreateDirectory(root);
            string destination=Path.Combine(root,Path.GetFileName(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)));
            if(Directory.Exists(destination))throw new IOException("目标已存在，请使用新的模型名称: "+destination);
            Directory.CreateDirectory(destination);
            try {
                for(int i=0;i<loader.Metadata.files.Length;i++) {
                    if(EditorUtility.DisplayCancelableProgressBar("Import Gaussian LOD",loader.Metadata.files[i],(float)i/loader.Metadata.files.Length))throw new OperationCanceledException("导入取消，未提交 JSON。已复制数据保留于 "+destination);
                    string path=Path.Combine(destination,loader.Metadata.files[i]);Directory.CreateDirectory(Path.GetDirectoryName(path));
                    if(File.Exists(loader.FilePath(i)))File.Copy(loader.FilePath(i),path,false);
                    string cache=loader.FilePath(i)+GaussianRuntimeCache.Extension;
                    if(File.Exists(cache))File.Copy(cache,path+GaussianRuntimeCache.Extension,false);
                }
                File.Copy(Path.Combine(directory,"lod-meta.json"),Path.Combine(destination,"lod-meta.json"),false);
            } finally{EditorUtility.ClearProgressBar();AssetDatabase.Refresh();}
            return Path.Combine(destination,"lod-meta.json");
        }
        [MenuItem("Tools/Gaussian Splat LOD/Install URP Render Feature")]
        public static void InstallURPFeature()
        {
            var asset=GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if(asset==null){Debug.Log("当前使用 Built-in；无需 Renderer Feature。URP 项目请选择 URP Pipeline Asset 后再执行。");return;}
            var serialized=new SerializedObject(asset);var list=serialized.FindProperty("m_RendererDataList");
            if(list==null)throw new InvalidOperationException("URP 14 Renderer Data 字段未找到。");
            for(int i=0;i<list.arraySize;i++)if(list.GetArrayElementAtIndex(i).objectReferenceValue is ScriptableRendererData data)
            {
                bool exists=false;foreach(var f in data.rendererFeatures)if(f is GaussianLODRenderFeature)exists=true;
                if(exists)continue;
                Undo.RecordObject(data,"Install Gaussian LOD Feature");var feature=ScriptableObject.CreateInstance<GaussianLODRenderFeature>();feature.name="Gaussian Splat LOD";AssetDatabase.AddObjectToAsset(feature,data);data.rendererFeatures.Add(feature);data.SetDirty();EditorUtility.SetDirty(data);
            }
            AssetDatabase.SaveAssets();Debug.Log("Gaussian LOD Renderer Feature 已安装至当前 URP Renderer Data。");
        }
    }
}
