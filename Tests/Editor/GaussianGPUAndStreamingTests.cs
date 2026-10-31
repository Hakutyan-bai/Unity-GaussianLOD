using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
namespace GaussianSplatLOD.Tests
{
    public sealed class GaussianGPUAndStreamingTests
    {
        [TestCase(2147483648L)] [TestCase(1073741824L)]
        public void LargeVRAMBudgetRespectsSingleBufferDeviceLimit(long deviceLimit)
        {
            // Reproduces the user's 4096 MiB / 5M configuration without allocating gigabytes.
            int capacity=GaussianGPUResources.PlanCapacity(4096,5000000,436000,deviceLimit);
            Assert.Greater(capacity,0);Assert.LessOrEqual(capacity*(long)GaussianSplat.Stride,deviceLimit);
            Assert.LessOrEqual(capacity*(long)GaussianSplat.Stride,int.MaxValue);
            Assert.LessOrEqual(capacity*(long)GaussianSplat.Stride+GaussianGPUResources.SortBytes(5000000)+8,4096L*1048576);
        }
        [TestCase(1)] [TestCase(257)] [TestCase(4097)] [TestCase(8193)]
        public void GPURadixSortsAcrossBlocksWithStableTies(int count)
        {
            using var gpu=new GaussianGPUResources(32,count,Resources.Load<Shader>("GaussianLOD"),Resources.Load<ComputeShader>("GaussianLODSort"));
            var splats=new GaussianSplat[count];var refs=new Vector2Int[count];
            var expected=new List<int>();var random=new System.Random(71);
            for(int i=0;i<count;i++){splats[i].positionOpacity=new Vector4(0,0,(random.Next(100)-50)*.1f,1);refs[i]=new Vector2Int(i,0);expected.Add(i);}
            gpu.Splats.SetData(splats);gpu.Visible.SetData(refs);expected.Sort((a,b)=>{int c=splats[a].positionOpacity.z.CompareTo(splats[b].positionOpacity.z);return c!=0?c:a.CompareTo(b);});
            using var cmd=new CommandBuffer();gpu.Sort(cmd,Matrix4x4.identity,count);Graphics.ExecuteCommandBuffer(cmd);
            var sorted=new Vector2Int[count];gpu.PairsA.GetData(sorted);
            for(int i=0;i<count;i++)Assert.AreEqual(expected[i],sorted[i].y,"rank "+i);
        }
        [Test] public void ShaderCompilesAndIsSupported()
        {
            var shader=Resources.Load<Shader>("GaussianLOD");Assert.NotNull(shader);Assert.IsTrue(shader.isSupported);
            foreach(var message in UnityEditor.ShaderUtil.GetShaderMessages(shader))Assert.AreNotEqual(UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error,message.severity,message.message);
        }
        [TestCase(0f)] [TestCase(25f)]
        public void GaussianProjectionMatchesUnityGeometryWhenCameraPitches(float pitch)
        {
            if(GraphicsSettings.currentRenderPipeline!=null)Assert.Ignore("Built-in camera matrix regression; run with Built-in pipeline.");
            using var gpu=new GaussianGPUResources(16,1,Resources.Load<Shader>("GaussianLOD"),Resources.Load<ComputeShader>("GaussianLODSort"));
            var cameraGO=new GameObject("Projection regression camera");var camera=cameraGO.AddComponent<Camera>();camera.enabled=false;camera.cullingMask=1<<29;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;camera.transform.rotation=Quaternion.Euler(pitch,0,0);
            var rt=new RenderTexture(128,128,24,RenderTextureFormat.ARGB32);rt.Create();camera.targetTexture=rt;
            var cube=GameObject.CreatePrimitive(PrimitiveType.Cube);cube.layer=29;cube.transform.position=new Vector3(.6f,-.5f,5);cube.transform.localScale=Vector3.one*.08f;cube.SetActive(false);
            var material=new Material(Shader.Find("Unlit/Color"));material.color=Color.white;cube.GetComponent<Renderer>().sharedMaterial=material;
            var image=new Texture2D(128,128,TextureFormat.RGBA32,false);
            using var cmd=new CommandBuffer();
            Vector2 Centroid()
            {
                camera.Render();var old=RenderTexture.active;RenderTexture.active=rt;image.ReadPixels(new Rect(0,0,128,128),0,0);image.Apply();RenderTexture.active=old;
                double x=0,y=0,total=0;var pixels=image.GetPixels32();
                for(int i=0;i<pixels.Length;i++){double w=pixels[i].r/255.0;if(w<.01)continue;x+=(i%128)*w;y+=(i/128)*w;total+=w;}
                Assert.Greater(total,1,"Rendered marker must be visible.");return new Vector2((float)(x/total),(float)(y/total));
            }
            try
            {
                gpu.Splats.SetData(new[]{GaussianSplat.Create(cube.transform.position,Vector3.one*.04f,Quaternion.identity,.9f,Vector3.one)});gpu.Visible.SetData(new[]{new Vector2Int(0,0)});gpu.Fades.SetData(new[]{1f,0f});
                gpu.Sort(cmd,camera.worldToCameraMatrix,1);
                var props=new MaterialPropertyBlock();props.SetBuffer("_Splats",gpu.Splats);props.SetBuffer("_Visible",gpu.Visible);props.SetBuffer("_Order",gpu.PairsA);props.SetBuffer("_Fades",gpu.Fades);
                props.SetMatrix("_Model",Matrix4x4.identity);props.SetMatrix("_View",camera.worldToCameraMatrix);props.SetInt("_UseCustomProjection",0);props.SetFloat("_RenderScale",1);props.SetFloat("_OpacityMultiplier",1);props.SetVector("_Viewport",new Vector4(128,128,0,0));
                cmd.DrawProcedural(Matrix4x4.identity,gpu.Material,0,MeshTopology.Triangles,6,1,props);camera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha,cmd);
                Vector2 gaussian=Centroid();camera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha,cmd);cube.SetActive(true);Vector2 mesh=Centroid();
                Assert.Less(Vector2.Distance(gaussian,mesh),1.5f,"Gaussian and Unity geometry must share a screen position, including projection Y flip.");
            }
            finally{camera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha,cmd);camera.targetTexture=null;rt.Release();UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(cube);UnityEngine.Object.DestroyImmediate(material);UnityEngine.Object.DestroyImmediate(cameraGO);}
        }
        [Test] public void OpticalDepthFadePreservesOpacityForIdenticalGaussianLayers()
        {
            using var gpu=new GaussianGPUResources(16,2,Resources.Load<Shader>("GaussianLOD"),Resources.Load<ComputeShader>("GaussianLODSort"));
            gpu.Splats.SetData(new[]{GaussianSplat.Create(new Vector3(0,0,-3),Vector3.one*.2f,Quaternion.identity,.8f,new Vector3(.8f,.6f,.4f))});
            var rt=new RenderTexture(64,64,0,RenderTextureFormat.ARGB32){hideFlags=HideFlags.HideAndDontSave};rt.Create();
            var texture=new Texture2D(64,64,TextureFormat.RGBA32,false);
            Color Render(int count)
            {
                gpu.Visible.SetData(new[]{new Vector2Int(0,0),new Vector2Int(0,256)});gpu.Fades.SetData(count==1?new[]{1f,0f}:new[]{.25f,.75f});
                using var cmd=new CommandBuffer();cmd.SetRenderTarget(rt);cmd.ClearRenderTarget(false,true,Color.clear);gpu.Sort(cmd,Matrix4x4.identity,count);
                var p=new MaterialPropertyBlock();p.SetBuffer("_Splats",gpu.Splats);p.SetBuffer("_Visible",gpu.Visible);p.SetBuffer("_Order",gpu.PairsA);p.SetBuffer("_Fades",gpu.Fades);
                p.SetMatrix("_Model",Matrix4x4.identity);p.SetMatrix("_View",Matrix4x4.identity);p.SetInt("_UseCustomProjection",1);p.SetMatrix("_Projection",GL.GetGPUProjectionMatrix(Matrix4x4.Perspective(60,1,.1f,100),true));
                p.SetVector("_Viewport",new Vector4(64,64,0,0));p.SetFloat("_RenderScale",1);p.SetFloat("_OpacityMultiplier",1);p.SetVector("_LocalCamera",Vector4.zero);
                cmd.DrawProcedural(Matrix4x4.identity,gpu.Material,0,MeshTopology.Triangles,6,count,p);Graphics.ExecuteCommandBuffer(cmd);
                var previous=RenderTexture.active;RenderTexture.active=rt;texture.ReadPixels(new Rect(0,0,64,64),0,0);texture.Apply();RenderTexture.active=previous;return texture.GetPixel(32,32);
            }
            try{var full=Render(1);var fade=Render(2);Assert.Greater(full.a,.5f);Assert.That(fade.a,Is.EqualTo(full.a).Within(.015f));Assert.That(fade.r,Is.EqualTo(full.r).Within(.015f));}
            finally{rt.Release();UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(texture);}
        }
        [UnityTest] public IEnumerator AsyncSharedCacheChangesLODAndReleasesGPU()
        {
            string path=Path.Combine(Application.streamingAssetsPath,"GaussianSplatLOD/Demo/lod-meta.json");if(!File.Exists(path))Assert.Ignore("先执行 Create Demo 生成原生测试数据。");
            var go=new GameObject("Gaussian test model");var cam=new GameObject("Gaussian test camera").AddComponent<Camera>();cam.enabled=false;
            var r=go.AddComponent<GaussianLODRenderer>();r.Unload();r.maxGPUMemory=64;r.maxCPUCache=32;r.maxVisibleSplats=100000;r.enableAsyncLoading=true;r.targetCamera=cam;r.loadOnEnable=false;r.Load();
            cam.transform.position=new Vector3(0,12,-16);cam.transform.LookAt(Vector3.zero);
            try{
                double start=Time.realtimeSinceStartupAsDouble;
                while(Time.realtimeSinceStartupAsDouble-start<8){r.Tick(cam);yield return null;}
                Assert.NotNull(r.Metadata,r.LastError);Assert.IsNull(r.LastError);var levels=r.GetCurrentLODLevels();Assert.That(Array.Exists(levels,l=>l>=0));
                long reads=r.DiskReads;start=Time.realtimeSinceStartupAsDouble;
                while(Time.realtimeSinceStartupAsDouble-start<.5){r.Tick(cam);yield return null;}
                Assert.AreEqual(reads,r.DiskReads,"Stable camera must not reread resident intervals.");
                Assert.LessOrEqual(r.CPUBytes,32L*1024*1024);Assert.LessOrEqual(r.GPUBytes,64L*1024*1024);
                int near=0;foreach(int l in levels)near+=Math.Max(0,l);
                cam.transform.position=new Vector3(0,500,-700);cam.transform.LookAt(Vector3.zero);start=Time.realtimeSinceStartupAsDouble;
                while(Time.realtimeSinceStartupAsDouble-start<1){r.Tick(cam);yield return null;}
                int far=0;foreach(int l in r.GetCurrentLODLevels())far+=Math.Max(0,l);Assert.GreaterOrEqual(far,near,"Far camera should select coarser LOD.");
                r.Unload();Assert.AreEqual(0,r.GPUBytes);Assert.AreEqual(0,r.CPUBytes);Assert.AreEqual("Unloaded",r.Status);
            }finally{UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(cam.gameObject);}
        }
        [UnityTest] public IEnumerator InitialCoverageIsAtomicAndTransitionsNeverDropWholeRegions()
        {
            string path=Path.Combine(Application.streamingAssetsPath,"GaussianSplatLOD/Demo/lod-meta.json");if(!File.Exists(path))Assert.Ignore("Create Demo first.");
            var go=new GameObject("LOD continuity regression");go.SetActive(false);
            var cam=new GameObject("LOD continuity camera").AddComponent<Camera>();cam.enabled=false;
            var r=go.AddComponent<GaussianLODRenderer>();r.maxGPUMemory=64;r.maxCPUCache=32;r.maxVisibleSplats=90000;r.enableAsyncLoading=false;r.uploadSplatsPerFrame=128;r.enableDynamicLOD=false;r.enableFrustumCulling=false;
            // This regression checks the common distant frontier. Near-quality protection
            // intentionally permits a nearby region to refine ahead of that frontier.
            r.nearQualityDistance=0;
            cam.transform.position=new Vector3(0,12,-16);cam.transform.LookAt(Vector3.zero);
            using var session=new GaussianStreamingSession(r,new GaussianLODLoader(path));session.InitializeCandidates();
            try
            {
                int initialDraws=0;
                for(int i=0;i<200&&!session.CoverageReady;i++)
                {
                    session.Tick(cam,Time.realtimeSinceStartupAsDouble);
                    if(session.CoverageReady)Assert.AreEqual(session.Loader.Metadata.tree.Length,session.CoveredNodes);
                    else{Assert.AreEqual(0,session.VisibleSegments(cam).Count);initialDraws++;}
                }
                Assert.Greater(initialDraws,1,"Test must span multiple incremental uploads.");Assert.IsTrue(session.CoverageReady);
                int coarse=session.Loader.Metadata.levels-1;
                foreach(int l in session.CurrentLevels)Assert.AreEqual(coarse,l,"First appearance must be complete coarse coverage.");
                Assert.LessOrEqual(session.DiskReads,2,"Merged coarse file must not create one request per node.");
                r.uploadSplatsPerFrame=16384;
                bool sawFade=false;double start=Time.realtimeSinceStartupAsDouble;
                while(Time.realtimeSinceStartupAsDouble-start<4)
                {
                    session.Tick(cam,Time.realtimeSinceStartupAsDouble);
                    long points=0;foreach(var s in session.VisibleSegments(cam))points+=s.Count;
                    Assert.LessOrEqual(points,session.GPU.VisibleCapacity,"Crossfade must reserve old+new whole regions.");
                    Assert.AreEqual(session.Loader.Metadata.tree.Length,session.CoveredNodes,"A ready coarse model must never lose a region.");
                    int finest=int.MaxValue,coarsest=-1;
                    foreach(int level in session.CurrentLevels){finest=Math.Min(finest,level);coarsest=Math.Max(coarsest,level);}
                    Assert.LessOrEqual(coarsest-finest,1,"Coherent progression must not complete several detail levels on one tile before its peers.");
                    foreach(float fade in session.Fades)if(fade>.01f&&fade<.99f)sawFade=true;
                    yield return null;
                }
                Assert.IsTrue(sawFade,"LOD switches must crossfade rather than snap.");
                cam.transform.rotation=Quaternion.Euler(0,180,0);session.Tick(cam,Time.realtimeSinceStartupAsDouble+.2);
                Assert.AreEqual(session.Loader.Metadata.tree.Length,session.CoveredNodes);
            }
            finally{UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(cam.gameObject);}
        }
    }
}
