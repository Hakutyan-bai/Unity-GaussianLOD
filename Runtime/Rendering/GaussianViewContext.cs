using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GaussianSplatLOD
{
    /// <summary>Actual render views. Reused per camera; no per-frame matrix/plane arrays.</summary>
    public sealed class GaussianViewContext
    {
        static readonly Dictionary<Camera,GaussianViewContext> cameras = new Dictionary<Camera,GaussianViewContext>();
        static readonly Dictionary<Camera,GaussianViewContext> renderCameras = new Dictionary<Camera,GaussianViewContext>();
        static readonly List<Camera> dead = new List<Camera>();
        public readonly Matrix4x4[] View = new Matrix4x4[2];
        public readonly Matrix4x4[] Projection = new Matrix4x4[2];
        public readonly Matrix4x4[] GPUProjection = new Matrix4x4[2];
        public readonly Vector4[] Viewport = new Vector4[2];
        public readonly Vector3[] EyePosition = new Vector3[2];
        readonly Vector4[] localCamera = new Vector4[2];
        readonly Plane[][] planes = { new Plane[6], new Plane[6] };
        public int ViewCount { get; private set; } = 1;
        public int DrawEye { get; private set; }
        public bool SinglePass { get; private set; }
        public bool ParallelEyes { get; private set; } = true;
        public bool RequiresPerEyeSort => ViewCount > 1 && !ParallelEyes;
        bool explicitProjection;
        public GaussianViewContext() { }
        internal static GaussianViewContext Get(Camera camera,bool renderPass=false)
        {
            var cache=renderPass?renderCameras:cameras;
            if (!cache.TryGetValue(camera,out var value))
            {
                dead.Clear(); foreach (var item in cache) if (item.Key == null) dead.Add(item.Key);
                foreach (var item in dead) cache.Remove(item);
                value = new GaussianViewContext(); cache.Add(camera,value);
            }
            return value;
        }
        public static GaussianViewContext FromCamera(Camera camera)
        {
            var value = Get(camera);
            value.ViewCount = camera.stereoEnabled ? 2 : 1;
            value.SinglePass = value.ViewCount > 1 && UnityEngine.XR.XRSettings.stereoRenderingMode != UnityEngine.XR.XRSettings.StereoRenderingMode.MultiPass;
            value.DrawEye = camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? 1 : 0;
            value.explicitProjection = false;
            for (int eye = 0; eye < value.ViewCount; eye++)
            {
                var stereo = eye == 0 ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right;
                value.View[eye] = value.ViewCount > 1 ? camera.GetStereoViewMatrix(stereo) : camera.worldToCameraMatrix;
                value.Projection[eye] = value.ViewCount > 1 ? camera.GetStereoProjectionMatrix(stereo) : camera.projectionMatrix;
                value.GPUProjection[eye] = GL.GetGPUProjectionMatrix(value.Projection[eye],camera.targetTexture != null || value.ViewCount > 1);
                float width=camera.pixelWidth,height=camera.pixelHeight;
                if(value.ViewCount>1 && UnityEngine.XR.XRSettings.eyeTextureWidth>0){width=UnityEngine.XR.XRSettings.eyeTextureWidth*camera.rect.width;height=UnityEngine.XR.XRSettings.eyeTextureHeight*camera.rect.height;}
                value.Viewport[eye] = new Vector4(width,height,0,0);
            }
            value.Finish(); return value;
        }
        public static GaussianViewContext FromURP(ref CameraData data)
        {
            var value = Get(data.camera,true);
            value.DrawEye=0;
            bool xr = data.xr != null && data.xr.enabled;
            value.ViewCount = xr ? Mathf.Min(2,data.xr.viewCount) : 1;
            value.SinglePass = xr && data.xr.singlePassEnabled;
            value.explicitProjection = true;
            for(int eye = 0; eye < value.ViewCount; eye++)
            {
                value.View[eye] = data.GetViewMatrix(eye);
                value.Projection[eye] = data.GetProjectionMatrix(eye);
                value.GPUProjection[eye] = data.GetGPUProjectionMatrix(eye);
                Rect viewport = xr ? data.xr.GetViewport(eye) : new Rect(0,0,data.scaledWidth,data.scaledHeight);
                value.Viewport[eye] = new Vector4(viewport.width,viewport.height,viewport.x,viewport.y);
            }
            value.Finish(); return value;
        }
        // Synthetic eyes are also useful for tests without requiring an attached headset.
        public void SetViews(Matrix4x4 leftView, Matrix4x4 leftProjection, Matrix4x4 rightView,
            Matrix4x4 rightProjection, Vector2 viewport)
        {
            ViewCount = 2; DrawEye=0; SinglePass = true; explicitProjection = true;
            View[0]=leftView; View[1]=rightView; Projection[0]=leftProjection; Projection[1]=rightProjection;
            for(int eye=0;eye<2;eye++){GPUProjection[eye]=GL.GetGPUProjectionMatrix(Projection[eye],true);Viewport[eye]=new Vector4(viewport.x,viewport.y,0,0);}
            Finish();
        }
        void Finish()
        {
            for(int eye=0;eye<ViewCount;eye++)
            {
                GeometryUtility.CalculateFrustumPlanes(Projection[eye]*View[eye],planes[eye]);
                Vector3 position=View[eye].inverse.MultiplyPoint3x4(Vector3.zero);
                EyePosition[eye]=position;
            }
            ParallelEyes=ViewCount<2 || Vector3.Angle(View[0].inverse.MultiplyVector(Vector3.forward),View[1].inverse.MultiplyVector(Vector3.forward))<.1f;
        }
        public bool TestBounds(Bounds bounds)
        {
            for(int eye=0;eye<ViewCount;eye++)if(GeometryUtility.TestPlanesAABB(planes[eye],bounds))return true;
            return false;
        }
        public float ProjectedArea(Bounds bounds)
        {
            float area=0;
            for(int eye=0;eye<ViewCount;eye++)
            {
                float focal=Mathf.Abs(Projection[eye].m11)*Viewport[eye].y*.5f;
                Vector3 position=new Vector3(EyePosition[eye].x,EyePosition[eye].y,EyePosition[eye].z);
                bool orthographic=Mathf.Abs(Projection[eye].m33-1)<.001f;
                float diameter=bounds.size.magnitude*focal/(orthographic?1:Mathf.Max(.01f,Mathf.Sqrt(bounds.SqrDistance(position))));
                area=Mathf.Max(area,Mathf.Min(Viewport[eye].x*Viewport[eye].y,diameter*diameter));
            }
            return area;
        }
        public void ApplyTo(MaterialPropertyBlock properties,Matrix4x4 model)
        {
            for(int eye=0;eye<ViewCount;eye++)
            {
                Vector3 world=new Vector3(EyePosition[eye].x,EyePosition[eye].y,EyePosition[eye].z);
                Vector3 local=model.inverse.MultiplyPoint3x4(world);localCamera[eye]=new Vector4(local.x,local.y,local.z,1);
            }
            int drawEye=SinglePass?0:Mathf.Min(DrawEye,ViewCount-1);
            properties.SetMatrix("_View",View[drawEye]);properties.SetMatrix("_Projection",GPUProjection[drawEye]);
            properties.SetVector("_Viewport",Viewport[drawEye]);properties.SetVector("_LocalCamera",localCamera[drawEye]);
            properties.SetInt("_UseCustomProjection",explicitProjection?1:0);
            properties.SetInt("_ViewCount",SinglePass?ViewCount:1);
            properties.SetInt("_UseStereoMatrices",explicitProjection?1:0);
            properties.SetInt("_StereoOrderSeparate",RequiresPerEyeSort?1:0);
            properties.SetMatrixArray("_EyeViews",View);properties.SetMatrixArray("_EyeProjections",GPUProjection);
            properties.SetVectorArray("_EyeViewports",Viewport);properties.SetVectorArray("_EyeCameras",localCamera);
        }
    }
}
