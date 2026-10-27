using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace GaussianSplatLOD.Editor
{
    /// <summary>Repeatable real-device preview and Windows build entry points. Results go outside Assets.</summary>
    [InitializeOnLoad]
    public static class GaussianLODValidation
    {
        static Scene scene;
        static Camera camera;
        static GaussianLODRenderer model;
        static RenderTexture target;
        static RenderPipelineAsset previousPipeline,previousQuality;
        static bool testingURP;
        static bool savedPipeline;
        static double start,lastFrame;
        static int stage;
        static string reportPath,jsonPath;
        static readonly List<object> samples=new List<object>();
        static double previewBegan,firstCoverage;
        static int minCoverage,transitionFrames,framesAfterCoverage;
        static GaussianLODValidation(){EditorApplication.update+=Tick;AssemblyReloadEvents.beforeAssemblyReload+=Cleanup;}
        public static string BeginPreview(string json=null,bool urp=false,string label=null,bool runtimeCache=true)
        {
            if(model!=null)throw new InvalidOperationException("Preview 正在运行。");
            testingURP=urp;jsonPath=json??Path.Combine(Application.streamingAssetsPath,"GaussianSplatLOD/Demo/lod-meta.json");
            Directory.CreateDirectory("Reports/GaussianSplatLOD");reportPath="Reports/GaussianSplatLOD/preview-"+(urp?"urp":"builtin")+"-"+(label??(json==null?"demo":"campus"))+".json";
            File.WriteAllText(reportPath,"{\"status\":\"running\"}");samples.Clear();
            previousPipeline=GraphicsSettings.defaultRenderPipeline;previousQuality=QualitySettings.renderPipeline;
            savedPipeline=true;
            if(urp){GraphicsSettings.defaultRenderPipeline=EnsureURPAsset();QualitySettings.renderPipeline=null;}
            else{GraphicsSettings.defaultRenderPipeline=null;QualitySettings.renderPipeline=null;}
            scene=EditorSceneManager.NewPreviewScene();
            var cameraGO=new GameObject("LOD Validation Camera");SceneManager.MoveGameObjectToScene(cameraGO,scene);camera=cameraGO.AddComponent<Camera>();cameraGO.layer=30;camera.cullingMask=1<<30;camera.enabled=false;camera.nearClipPlane=.1f;camera.farClipPlane=100000;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.035f,.05f,1);
            var go=new GameObject("LOD Validation Model");SceneManager.MoveGameObjectToScene(go,scene);go.layer=30;model=go.AddComponent<GaussianLODRenderer>();model.Unload();model.loadOnEnable=false;model.dataLocation=GaussianDataLocation.ExternalDirectory;model.jsonPath=jsonPath;model.maxGPUMemory=json==null?128:512;model.maxCPUCache=json==null?64:512;model.maxVisibleSplats=json==null?100000:1000000;model.targetCamera=camera;model.preloadDistance=10;model.preferRuntimeCache=runtimeCache;model.Load();
            target=new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32){hideFlags=HideFlags.HideAndDontSave};target.Create();camera.targetTexture=target;stage=0;start=lastFrame=previewBegan=EditorApplication.timeSinceStartup;firstCoverage=-1;minCoverage=int.MaxValue;transitionFrames=framesAfterCoverage=0;return reportPath;
        }
        static void Tick()
        {
            if(model==null||EditorApplication.isCompiling||EditorApplication.isUpdating)return;
            try
            {
                var meta=model.Metadata;
                if(meta==null){model.Tick(camera);if(EditorApplication.timeSinceStartup-start>30)throw new Exception("Metadata timeout: "+model.LastError);return;}
                Bounds bound=meta.forwardBox.Bounds;float radius=Mathf.Max(.1f,bound.extents.magnitude);
                float distance=stage==1?radius*40:radius*1.8f;
                if(stage==2){model.transform.SetPositionAndRotation(new Vector3(7,3,-5),Quaternion.Euler(17,33,7));model.transform.localScale=new Vector3(-1.2f,.7f,1.5f);}
                Bounds world=GaussianStreamingSessionBounds(bound,model.ModelMatrix);
                camera.transform.position=world.center+new Vector3(.15f,.65f,-1)*distance;camera.transform.LookAt(world.center);
                double now=EditorApplication.timeSinceStartup;if(now-lastFrame<1.0/60)return;lastFrame=now;
                model.Tick(camera);if(testingURP){RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest{destination=target});}else camera.Render();
                if(model.CoverageReady){if(firstCoverage<0)firstCoverage=now-previewBegan;framesAfterCoverage++;minCoverage=Math.Min(minCoverage,model.CoveredNodes);if(model.TransitioningNodes>0)transitionFrames++;}
                if(now-start<8)return;
                if(model.VisibleSplats<=0)throw new Exception("没有可见高斯: "+model.LastError+" / "+model.Status);
                string png=Path.ChangeExtension(reportPath,null)+"-"+stage+".png";
                int pixels=Capture(png);if(pixels<10)throw new Exception("GPU framebuffer 为空，像素数 "+pixels);
                samples.Add(new {stage,visible=model.VisibleSplats,chunks=model.VisibleChunks,gpuBytes=model.GPUBytes,cpuBytes=model.CPUBytes,reads=model.DiskReads,runtimeCacheReads=model.RuntimeCacheReads,plyReads=model.PLYReads,residentRanges=model.ResidentRanges,currentLevels=(int[])model.GetCurrentLODLevels().Clone(),coloredPixels=pixels,image=png,error=model.LastError});
                if(++stage<3){start=now;return;}
                if(minCoverage!=meta.tree.Length)throw new Exception("完整粗级发布后出现分区覆盖缺失。");
                File.WriteAllText(reportPath,Newtonsoft.Json.JsonConvert.SerializeObject(new{status="passed",device=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString(),pipeline=testingURP?"URP14":"BuiltIn",unity=Application.unityVersion,json=jsonPath,firstCoverageSeconds=firstCoverage,framesAfterCoverage,minCoveredNodes=minCoverage,transitionFrames,samples},Newtonsoft.Json.Formatting.Indented));Cleanup();
            }
            catch(Exception e){File.WriteAllText(reportPath,Newtonsoft.Json.JsonConvert.SerializeObject(new{status="failed",error=e.ToString()},Newtonsoft.Json.Formatting.Indented));Debug.LogException(e);Cleanup();}
        }
        static Bounds GaussianStreamingSessionBounds(Bounds b,Matrix4x4 m)
        {
            var result=new Bounds(m.MultiplyPoint3x4(b.min),Vector3.zero);for(int i=0;i<8;i++)result.Encapsulate(m.MultiplyPoint3x4(b.center+Vector3.Scale(b.extents,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1))));return result;
        }
        static int Capture(string path)
        {
            var previous=RenderTexture.active;RenderTexture.active=target;var image=new Texture2D(target.width,target.height,TextureFormat.RGBA32,false);image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();RenderTexture.active=previous;
            var data=image.GetPixels32();int changed=0;var background=data[0];foreach(var p in data)if(Math.Abs(p.r-background.r)+Math.Abs(p.g-background.g)+Math.Abs(p.b-background.b)>15)changed++;
            File.WriteAllBytes(path,image.EncodeToPNG());UnityEngine.Object.DestroyImmediate(image);return changed;
        }
        static void Cleanup()
        {
            if(model!=null)model.Unload();model=null;camera=null;
            if(scene.IsValid())EditorSceneManager.ClosePreviewScene(scene);
            if(target!=null){target.Release();UnityEngine.Object.DestroyImmediate(target);target=null;}
            if(savedPipeline){GraphicsSettings.defaultRenderPipeline=previousPipeline;QualitySettings.renderPipeline=previousQuality;savedPipeline=false;}
            testingURP=false;
        }
        public static UniversalRenderPipelineAsset EnsureURPAsset()
        {
            const string path="Assets/GaussianSplatLOD/URP14.asset";var asset=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);if(asset!=null)return asset;
            Directory.CreateDirectory("Assets/GaussianSplatLOD");var data=ScriptableObject.CreateInstance<UniversalRendererData>();data.name="Gaussian LOD URP14 Renderer";
            var feature=ScriptableObject.CreateInstance<GaussianLODRenderFeature>();feature.name="Gaussian LOD";data.rendererFeatures.Add(feature);
            AssetDatabase.CreateAsset(data,"Assets/GaussianSplatLOD/URP14Renderer.asset");AssetDatabase.AddObjectToAsset(feature,data);
            asset=UniversalRenderPipelineAsset.Create(data);asset.msaaSampleCount=1;asset.supportsHDR=false;AssetDatabase.CreateAsset(asset,path);AssetDatabase.SaveAssets();return asset;
        }
        public static string BuildWindows(bool urp=false,bool campus=false)
        {
            GaussianLODDemo.CreateScene();var old=GraphicsSettings.defaultRenderPipeline;var quality=QualitySettings.renderPipeline;
            var oldAPIs=PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);bool oldDefault=PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
            try{
                GraphicsSettings.defaultRenderPipeline=urp?EnsureURPAsset():null;QualitySettings.renderPipeline=null;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D12,GraphicsDeviceType.Direct3D11});
                string path="Builds/GaussianSplatLOD-"+(urp?"URP":"BuiltIn")+"/GaussianSplatLOD.exe";
                string scenePath=campus?GaussianLODDemo.CreateCampusScene():GaussianLODDemo.ScenePath;
                AssetDatabase.SaveAssets();
                var result=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{scenePath},locationPathName=path,target=BuildTarget.StandaloneWindows64,options=BuildOptions.Development});
                string json=Newtonsoft.Json.JsonConvert.SerializeObject(new{result=result.summary.result.ToString(),bytes=result.summary.totalSize,seconds=result.summary.totalTime.TotalSeconds,errors=result.summary.totalErrors,warnings=result.summary.totalWarnings,path},Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText("Reports/GaussianSplatLOD/build-"+(urp?"urp":"builtin")+".json",json);if(result.summary.result!=BuildResult.Succeeded)throw new Exception(json);return json;
            }finally{GraphicsSettings.defaultRenderPipeline=old;QualitySettings.renderPipeline=quality;PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,oldAPIs);PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,oldDefault);}
        }
    }
}
