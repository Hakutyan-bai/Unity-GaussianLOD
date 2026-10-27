using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GaussianSplatLOD.Editor
{
    public static class GaussianLODDemo
    {
        public const string ScenePath="Assets/GaussianSplatLOD/Demo.unity";
        static AholoConversionJob job;
        public static void WriteFixture(string path,int count=65536)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var stream=File.Create(path);using var writer=new BinaryWriter(stream);
            var h=new StringBuilder($"ply\nformat binary_little_endian 1.0\nelement vertex {count}\n");
            foreach(string n in new[]{"x","y","z","f_dc_0","f_dc_1","f_dc_2"})h.Append("property float ").Append(n).Append('\n');
            for(int j=0;j<45;j++)h.Append("property float f_rest_").Append(j).Append('\n');
            foreach(string n in new[]{"opacity","scale_0","scale_1","scale_2","rot_0","rot_1","rot_2","rot_3"})h.Append("property float ").Append(n).Append('\n');
            h.Append("end_header\n");writer.Write(Encoding.ASCII.GetBytes(h.ToString()));
            for(int i=0;i<count;i++) {
                float x=(i%256-127.5f)*.065f,z=(i/256-127.5f)*.065f,y=.7f*Mathf.Sin(x*.7f)*Mathf.Cos(z*.6f);
                writer.Write(x);writer.Write(y);writer.Write(z);
                foreach(float c in new[]{.3f+.5f*(x+8.3f)/16.6f,.3f+.4f*(z+8.3f)/16.6f,.5f+.3f*Mathf.Sin(x)})writer.Write((c-.5f)/.2820947918f);
                for(int j=0;j<45;j++)writer.Write(.035f*Mathf.Sin(i*.001f+j));
                writer.Write(2.5f);writer.Write(Mathf.Log(.045f));writer.Write(Mathf.Log(.016f));writer.Write(Mathf.Log(.035f));float angle=.4f*Mathf.Sin(x);
                writer.Write(Mathf.Cos(angle*.5f));writer.Write(0f);writer.Write(Mathf.Sin(angle*.5f));writer.Write(0f);
            }
        }
        [MenuItem("Tools/Gaussian Splat LOD/Create Demo")]
        public static void Create()
        {
            string path=Path.Combine(Application.streamingAssetsPath,"GaussianSplatLOD/Demo/lod-meta.json");
            if(File.Exists(path)){new GaussianLODLoader(path).ValidateAllRanges();CreateScene();return;}
            if(job!=null&&!job.Work.IsCompleted)throw new InvalidOperationException("Demo 正在生成。");
            string source=Path.GetFullPath("Library/GaussianSplatLOD/DemoSource.ply");WriteFixture(source);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetDirectoryName(path)));
            job=new AholoConversionJob(GaussianLODEditorUtility.NativeExecutable,source,Path.GetDirectoryName(path),5,12000,true);
            EditorApplication.update-=Poll;EditorApplication.update+=Poll;
        }
        static void Poll()
        {
            if(!job.Work.IsCompleted)return;EditorApplication.update-=Poll;
            if(job.Work.IsFaulted){Debug.LogException(job.Work.Exception.GetBaseException());return;}
            if(job.Canceled)return;AssetDatabase.Refresh();CreateScene();
        }
        public static void CreateScene()
        {
            Directory.CreateDirectory("Assets/GaussianSplatLOD");
            if(File.Exists(ScenePath)){Debug.Log("Demo 已存在: "+ScenePath);return;}
            Scene previous=SceneManager.GetActiveScene();Scene scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            var camera=new GameObject("Main Camera").AddComponent<Camera>();camera.tag="MainCamera";camera.transform.position=new Vector3(0,14,-18);camera.transform.LookAt(Vector3.zero);camera.nearClipPlane=.1f;camera.farClipPlane=2000;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.035f,.05f);
            var light=new GameObject("Directional Light").AddComponent<Light>();light.type=LightType.Directional;
            var r=new GameObject("Gaussian LOD Model").AddComponent<GaussianLODRenderer>();r.jsonPath="GaussianSplatLOD/Demo/lod-meta.json";r.dataLocation=GaussianDataLocation.StreamingAssets;r.maxGPUMemory=128;r.maxCPUCache=64;r.maxVisibleSplats=100000;r.targetCamera=camera;
            r.jsonConfiguration=AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/StreamingAssets/"+r.jsonPath);r.SetShaderAssets(Resources.Load<Shader>("GaussianLOD"),Resources.Load<ComputeShader>("GaussianLODSort"));
            var fly=camera.gameObject.AddComponent<GaussianLODDemoCamera>();fly.model=r;
            // The synthetic sample is centred at the origin; campus-specific 60..100 height
            // limits would move its camera away as soon as Play starts.
            fly.minHeight=-20;fly.maxHeight=50;fly.minRotationX=-80;fly.maxRotationX=80;
            EditorSceneManager.SaveScene(scene,ScenePath);EditorSceneManager.CloseScene(scene,true);if(previous.IsValid())SceneManager.SetActiveScene(previous);AssetDatabase.Refresh();Debug.Log("Gaussian LOD Demo 已创建: "+ScenePath);
        }
        public static string CreateCampusScene()
        {
            const string path="Assets/GaussianSplatLOD/Campus.unity";
            if(File.Exists(path))return path;
            const string json="GaussianSplatLOD/SUIT-20261027/lod-meta.json";
            var loader=new GaussianLODLoader(Path.Combine(Application.streamingAssetsPath,json));loader.ValidateAllRanges();
            Scene previous=SceneManager.GetActiveScene();var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            var c=new GameObject("Main Camera").AddComponent<Camera>();c.tag="MainCamera";c.clearFlags=CameraClearFlags.SolidColor;c.backgroundColor=new Color(.025f,.035f,.05f);c.nearClipPlane=.3f;c.farClipPlane=100000;
            var b=loader.Metadata.forwardBox.Bounds;float radius=b.extents.magnitude;c.transform.position=b.center+new Vector3(.15f,.65f,-1)*radius*1.8f;c.transform.LookAt(b.center);
            var light=new GameObject("Directional Light").AddComponent<Light>();light.type=LightType.Directional;
            var r=new GameObject("Gaussian LOD Campus").AddComponent<GaussianLODRenderer>();r.Unload();r.jsonPath=json;r.dataLocation=GaussianDataLocation.StreamingAssets;r.maxCPUCache=512;r.maxGPUMemory=512;r.maxVisibleSplats=1000000;r.targetCamera=c;r.SetShaderAssets(Resources.Load<Shader>("GaussianLOD"),Resources.Load<ComputeShader>("GaussianLODSort"));
            var fly=c.gameObject.AddComponent<GaussianLODDemoCamera>();fly.model=r;fly.moveSpeed=100;
            EditorSceneManager.SaveScene(scene,path);EditorSceneManager.CloseScene(scene,true);if(previous.IsValid())SceneManager.SetActiveScene(previous);AssetDatabase.Refresh();return path;
        }
    }
}
