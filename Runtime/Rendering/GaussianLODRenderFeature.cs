using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
namespace GaussianSplatLOD
{
    public sealed class GaussianLODRenderFeature : ScriptableRendererFeature
    {
        sealed class Pass : ScriptableRenderPass
        {
            public Pass(){renderPassEvent=RenderPassEvent.BeforeRenderingTransparents;}
            public override void Execute(ScriptableRenderContext context,ref RenderingData data)
            {
                Camera camera=data.cameraData.camera;
                var cmd=CommandBufferPool.Get("Gaussian Splat LOD");
                try {foreach(var model in GaussianLODRenderer.Instances)if(model!=null)model.Record(camera,cmd);context.ExecuteCommandBuffer(cmd);}
                finally {CommandBufferPool.Release(cmd);}
            }
        }
        Pass pass;
        public override void Create(){pass=new Pass();}
        public override void AddRenderPasses(ScriptableRenderer renderer,ref RenderingData renderingData)
        {
            if(renderingData.cameraData.cameraType!=CameraType.Preview)renderer.EnqueuePass(pass);
        }
    }
}
