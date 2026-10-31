using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatLOD.Tests
{
    public sealed class GaussianXRAndIndexTests
    {
        static Matrix4x4 CameraView(Vector3 position,Quaternion rotation)
            => Matrix4x4.Scale(new Vector3(1,1,-1))*Matrix4x4.TRS(position,rotation,Vector3.one).inverse;
        [Test] public void StereoCullingIncludesRegionsVisibleToOnlyOneEye()
        {
            var projection=Matrix4x4.Perspective(60,1,.1f,100);
            var left=CameraView(new Vector3(-4,0,0),Quaternion.identity);
            var right=CameraView(new Vector3(4,0,0),Quaternion.identity);
            var bounds=new Bounds(new Vector3(4,0,5),Vector3.one*.1f);
            Assert.IsFalse(GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(projection*left),bounds));
            var views=new GaussianViewContext();views.SetViews(left,projection,right,projection,new Vector2(1000,1000));
            Assert.IsTrue(views.TestBounds(bounds));Assert.IsTrue(views.ParallelEyes);Assert.IsFalse(views.RequiresPerEyeSort);
            Assert.Greater(views.ProjectedArea(bounds),0);
            Assert.IsFalse(views.TestBounds(new Bounds(new Vector3(0,0,-5),Vector3.one)));
            views.SetViews(left,projection,CameraView(new Vector3(4,0,0),Quaternion.Euler(0,10,0)),projection,new Vector2(1000,1000));
            Assert.IsTrue(views.RequiresPerEyeSort,"Canted views must use independent depth ordering.");
        }
        [Test] public void CameraViewContextReusesItsMatrixAndPlaneStorage()
        {
            var go=new GameObject("View context reuse");var camera=go.AddComponent<Camera>();
            try{Assert.AreSame(GaussianViewContext.FromCamera(camera),GaussianViewContext.FromCamera(camera));}
            finally{UnityEngine.Object.DestroyImmediate(go);}
        }
        [Test] public void SelectionCaptureCannotOverwriteActualURPRenderView()
        {
            var go=new GameObject("URP context isolation");var camera=go.AddComponent<Camera>();
            try{
                // Use the render-context cache directly: CameraData getters are valid only inside a real URP pass.
                var render=GaussianViewContext.Get(camera,true);
                var projection=Matrix4x4.Perspective(60,1,.1f,100);
                render.SetViews(CameraView(new Vector3(-1,0,0),Quaternion.identity),projection,CameraView(new Vector3(1,0,0),Quaternion.identity),projection,new Vector2(777,555));
                var selection=GaussianViewContext.FromCamera(camera);
                Assert.AreNotSame(selection,render);Assert.AreEqual(2,render.ViewCount);Assert.AreEqual(777,render.Viewport[0].x);
            }finally{UnityEngine.Object.DestroyImmediate(go);}
        }
        [Test] public void ResidentCapacityUsesByteBudgetInsteadOfVisibleCountHeuristic()
        {
            int capacity=GaussianGPUResources.PlanCapacity(384,350000,435022,int.MaxValue,52,2000000);
            Assert.GreaterOrEqual(capacity-435022,383095,"A whole campus detail range must fit beyond pinned data.");
            Assert.LessOrEqual(capacity*256L+GaussianGPUResources.SortBytes(350000)+52*40L,384L*1048576);
            Assert.AreEqual(700000,GaussianGPUResources.PlanCapacity(384,350000,435022,int.MaxValue,52,700000));
        }
        [Test] public void GPUVisibleIndexExpansionPreservesRecordOffsetsAndPackedLOD()
        {
            using var gpu=new GaussianGPUResources(16,519,Resources.Load<Shader>("GaussianLOD"),Resources.Load<ComputeShader>("GaussianLODSort"),0,2,4096);
            var segments=new[]{new GaussianVisibleSegment{Slot=10,Count=257,PackedLevel=0x100,OutputStart=0},new GaussianVisibleSegment{Slot=600,Count=262,PackedLevel=0x203,OutputStart=257}};
            using var cmd=new CommandBuffer();gpu.BuildVisible(cmd,segments,2,519);Graphics.ExecuteCommandBuffer(cmd);
            var indices=new Vector2Int[519];gpu.Visible.GetData(indices);
            for(int i=0;i<indices.Length;i++)Assert.AreEqual(new Vector2Int(i<257?10+i:600+i-257,i<257?0x100:0x203),indices[i]);
        }
        [Test] public void QueuedCameraDescriptorsAreAppliedInCommandOrder()
        {
            using var gpu=new GaussianGPUResources(16,4,Resources.Load<Shader>("GaussianLOD"),Resources.Load<ComputeShader>("GaussianLODSort"),0,1,128);
            var first=new[]{new GaussianVisibleSegment{Slot=10,Count=4,PackedLevel=3,OutputStart=0}};
            var second=new[]{new GaussianVisibleSegment{Slot=20,Count=4,PackedLevel=1,OutputStart=0}};
            using var cmd=new CommandBuffer();gpu.BuildVisible(cmd,first,1,4);gpu.BuildVisible(cmd,second,1,4);Graphics.ExecuteCommandBuffer(cmd);
            var indices=new Vector2Int[4];gpu.Visible.GetData(indices);
            for(int i=0;i<4;i++)Assert.AreEqual(new Vector2Int(20+i,1),indices[i]);
        }
        [TestCase(65537)] [TestCase(131073)]
        public void HierarchicalRadixPrefixCrosses256HistogramGroups(int count)
        {
            using var gpu=new GaussianGPUResources(64,count,Resources.Load<Shader>("GaussianLOD"),Resources.Load<ComputeShader>("GaussianLODSort"),0,1,count);
            var data=new GaussianSplat[count];var indices=new Vector2Int[count];var expected=new List<int>(count);var random=new System.Random(987);
            for(int i=0;i<count;i++){data[i].positionOpacity=new Vector4(0,0,random.Next(-300,300)*.1f,1);indices[i]=new Vector2Int(i,0);expected.Add(i);}
            gpu.Splats.SetData(data);gpu.Visible.SetData(indices);
            expected.Sort((a,b)=>{int order=data[a].positionOpacity.z.CompareTo(data[b].positionOpacity.z);return order!=0?order:a.CompareTo(b);});
            using var cmd=new CommandBuffer();gpu.Sort(cmd,Matrix4x4.identity,count);Graphics.ExecuteCommandBuffer(cmd);gpu.PairsA.GetData(indices);
            for(int i=0;i<count;i++)Assert.AreEqual(expected[i],indices[i].y,"Sorted rank "+i);
        }
        [Test] public void StereoInstancedShaderDrawsBothArrayLayersWithoutSkippingSplatIndices()
        {
            if(!SystemInfo.supports2DArrayTextures || !SystemInfo.supportsRenderTargetArrayIndexFromVertexShader)
                Assert.Ignore("This graphics backend cannot validate vertex-selected stereo array layers.");
            using var gpu=new GaussianGPUResources(16,1,Resources.Load<Shader>("GaussianLOD"),Resources.Load<ComputeShader>("GaussianLODSort"),0,1,16);
            gpu.Splats.SetData(new[]{GaussianSplat.Create(new Vector3(0,0,5),Vector3.one*.2f,Quaternion.identity,.9f,Vector3.one)});
            gpu.Visible.SetData(new[]{new Vector2Int(0,0)});gpu.Fades.SetData(new[]{1f,0f});
            var target=new RenderTexture(128,128,0,RenderTextureFormat.ARGB32){dimension=TextureDimension.Tex2DArray,volumeDepth=2};target.Create();
            var slice=new RenderTexture(128,128,0,RenderTextureFormat.ARGB32);slice.Create();
            var image=new Texture2D(128,128,TextureFormat.RGBA32,false);
            var projection=Matrix4x4.Perspective(60,1,.1f,100);
            var views=new GaussianViewContext();views.SetViews(CameraView(new Vector3(-.1f,0,0),Quaternion.identity),projection,CameraView(new Vector3(.1f,0,0),Quaternion.identity),projection,new Vector2(128,128));
            try{
                var properties=new MaterialPropertyBlock();views.ApplyTo(properties,Matrix4x4.identity);
                properties.SetMatrix("_Model",Matrix4x4.identity);properties.SetBuffer("_Splats",gpu.Splats);properties.SetBuffer("_Visible",gpu.Visible);properties.SetBuffer("_Order",gpu.PairsA);properties.SetBuffer("_OrderRight",gpu.PairsRight);properties.SetBuffer("_Fades",gpu.Fades);
                properties.SetFloat("_RenderScale",1);properties.SetFloat("_OpacityMultiplier",1);
                using var cmd=new CommandBuffer();gpu.Sort(cmd,CameraView(Vector3.zero,Quaternion.identity),1);
                cmd.SetRenderTarget(new RenderTargetIdentifier(target,0,CubemapFace.Unknown,-1));cmd.SetViewport(new Rect(0,0,128,128));cmd.ClearRenderTarget(false,true,Color.clear);
                cmd.EnableShaderKeyword("STEREO_INSTANCING_ON");cmd.SetInstanceMultiplier(2);
                cmd.DrawProcedural(Matrix4x4.identity,gpu.Material,0,MeshTopology.Triangles,6,1,properties);
                cmd.SetInstanceMultiplier(1);cmd.DisableShaderKeyword("STEREO_INSTANCING_ON");Graphics.ExecuteCommandBuffer(cmd);
                var centers=new float[2];
                for(int eye=0;eye<2;eye++){
                    Graphics.CopyTexture(target,eye,0,slice,0,0);var previous=RenderTexture.active;RenderTexture.active=slice;
                    image.ReadPixels(new Rect(0,0,128,128),0,0);image.Apply();RenderTexture.active=previous;
                    var pixels=image.GetPixels32();double weighted=0,total=0;for(int i=0;i<pixels.Length;i++){weighted+=(i%128)*pixels[i].a;total+=pixels[i].a;}
                    Assert.Greater(total,1000,"Eye "+eye+" must render its Gaussian; raw SV_InstanceID would index out of bounds for the second eye.");centers[eye]=(float)(weighted/total);
                }
                Assert.Greater(centers[0]-centers[1],2,"Per-eye view projection must produce stereo parallax.");
            }finally{target.Release();slice.Release();UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(slice);UnityEngine.Object.DestroyImmediate(image);}
        }
        [Test] public void IndependentEyeSortDoesNotOverwriteLeftEyeOrder()
        {
            const int count=513;
            using var gpu=new GaussianGPUResources(16,count,Resources.Load<Shader>("GaussianLOD"),Resources.Load<ComputeShader>("GaussianLODSort"),0,1,count);
            var data=new GaussianSplat[count];var indices=new Vector2Int[count];
            for(int i=0;i<count;i++){data[i].positionOpacity=new Vector4(count-i,0,i-300,1);indices[i]=new Vector2Int(i,0);}
            gpu.Splats.SetData(data);gpu.Visible.SetData(indices);
            using var cmd=new CommandBuffer();gpu.Sort(cmd,Matrix4x4.identity,count);gpu.Sort(cmd,Matrix4x4.Rotate(Quaternion.Euler(0,-90,0)),count,1);Graphics.ExecuteCommandBuffer(cmd);
            var left=new Vector2Int[count];var right=new Vector2Int[count];gpu.GetOrder(0).GetData(left);gpu.GetOrder(1).GetData(right);
            for(int i=0;i<count;i++){Assert.AreEqual(i,left[i].y);Assert.AreEqual(count-1-i,right[i].y);}
        }
    }
}
