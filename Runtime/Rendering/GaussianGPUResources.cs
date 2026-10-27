using System;
using UnityEngine;
using UnityEngine.Rendering;
namespace GaussianSplatLOD
{
    internal sealed class GaussianGPUResources : IDisposable
    {
        public readonly ComputeBuffer Splats, Visible, PairsA, PairsB, Hist, Offsets, Totals, Fades;
        public readonly Material Material;
        public readonly int Capacity, VisibleCapacity;
        public readonly long Bytes;
        public readonly GaussianSlotAllocator Slots;
        readonly ComputeShader sort;
        readonly int init,histogram,prefix,scatter;
        public static long SortBytes(int count) => count*24L+((count+255)/256)*128L+64;
        internal static int PlanCapacity(int budgetMiB,int visibleLimit,long permanentPoints,long deviceBufferLimit,int nodeCount=1)
        {
            // Unity 2022.3 ComputeBuffer also has a signed byte-size path. A large total VRAM
            // budget never authorizes a single >2 GiB allocation. Round down by whole records.
            long perBuffer=Math.Min(int.MaxValue,deviceBufferLimit>0?deviceBufferLimit:int.MaxValue);
            long budget=Math.Max(16,budgetMiB)*1024L*1024;
            return (int)Math.Max(0,Math.Min(perBuffer/GaussianSplat.Stride,
                Math.Min(16000000,Math.Min(Math.Max(visibleLimit*2L+65536,permanentPoints+visibleLimit),
                    (budget-SortBytes(visibleLimit)-Math.Max(1,nodeCount)*8L)/GaussianSplat.Stride))));
        }
        public GaussianGPUResources(int budgetMiB,int visibleLimit,Shader drawShader,ComputeShader sorting,long permanentPoints=0,int nodeCount=1)
        {
            try {
                if(!SystemInfo.supportsComputeShaders || SystemInfo.graphicsShaderLevel<50)throw new NotSupportedException("需要 SM5 Compute Shader / DX11 或 DX12。");
                VisibleCapacity=Mathf.Clamp(visibleLimit,1,8000000);
                Capacity=PlanCapacity(budgetMiB,VisibleCapacity,permanentPoints,SystemInfo.maxGraphicsBufferSize,nodeCount);
                if(Capacity<1)throw new InvalidOperationException("GPU 预算不足以容纳排序缓冲，请降低 Max Visible Splats 或增加 Max GPU Memory。");
                if(drawShader==null || !drawShader.isSupported || sorting==null)throw new NotSupportedException("Gaussian Shader / ComputeShader 缺失或不支持。");
                Material=new Material(drawShader){hideFlags=HideFlags.HideAndDontSave};
                sort=UnityEngine.Object.Instantiate(sorting);sort.hideFlags=HideFlags.HideAndDontSave;
                init=sort.FindKernel("Initialize");histogram=sort.FindKernel("Histogram");prefix=sort.FindKernel("Prefix");scatter=sort.FindKernel("Scatter");
                Splats=new ComputeBuffer(Capacity,GaussianSplat.Stride);Visible=new ComputeBuffer(VisibleCapacity,8);
                PairsA=new ComputeBuffer(VisibleCapacity,8);PairsB=new ComputeBuffer(VisibleCapacity,8);
                int groups=(VisibleCapacity+255)/256;
                Hist=new ComputeBuffer(groups*16,4);Offsets=new ComputeBuffer(groups*16,4);Totals=new ComputeBuffer(16,4);
                Fades=new ComputeBuffer(Math.Max(1,nodeCount)*2,4);
                Slots=new GaussianSlotAllocator(Capacity);Bytes=Capacity*(long)GaussianSplat.Stride+SortBytes(VisibleCapacity)+Math.Max(1,nodeCount)*8L;
            } catch { Dispose();throw; }
        }
        public void Sort(CommandBuffer cmd,Matrix4x4 viewModel,int count)
        {
            int groups=(count+255)/256;
            cmd.SetComputeIntParam(sort,"_Count",count);cmd.SetComputeIntParam(sort,"_Groups",groups);cmd.SetComputeMatrixParam(sort,"_View",viewModel);
            cmd.SetComputeBufferParam(sort,init,"_Splats",Splats);cmd.SetComputeBufferParam(sort,init,"_Visible",Visible);cmd.SetComputeBufferParam(sort,init,"_Destination",PairsA);
            cmd.DispatchCompute(sort,init,groups,1,1);
            var src=PairsA;var dst=PairsB;
            for(int shift=0;shift<32;shift+=4) {
                cmd.SetComputeIntParam(sort,"_Shift",shift);
                cmd.SetComputeBufferParam(sort,histogram,"_Source",src);cmd.SetComputeBufferParam(sort,histogram,"_Histogram",Hist);cmd.DispatchCompute(sort,histogram,groups,1,1);
                cmd.SetComputeBufferParam(sort,prefix,"_Histogram",Hist);cmd.SetComputeBufferParam(sort,prefix,"_Offsets",Offsets);cmd.SetComputeBufferParam(sort,prefix,"_Totals",Totals);cmd.DispatchCompute(sort,prefix,1,1,1);
                cmd.SetComputeBufferParam(sort,scatter,"_Source",src);cmd.SetComputeBufferParam(sort,scatter,"_Destination",dst);cmd.SetComputeBufferParam(sort,scatter,"_Offsets",Offsets);cmd.SetComputeBufferParam(sort,scatter,"_Totals",Totals);cmd.DispatchCompute(sort,scatter,groups,1,1);
                var tmp=src;src=dst;dst=tmp;
            }
            // Eight radix passes finish in PairsA. Sorting spans all visible ranges of this model.
        }
        public void Dispose()
        {
            Splats?.Dispose();Visible?.Dispose();PairsA?.Dispose();PairsB?.Dispose();Hist?.Dispose();Offsets?.Dispose();Totals?.Dispose();Fades?.Dispose();Destroy(Material);Destroy(sort);
        }
        internal static void Destroy(UnityEngine.Object o) { if(o==null)return;if(Application.isPlaying)UnityEngine.Object.Destroy(o);else UnityEngine.Object.DestroyImmediate(o); }
    }
}
