using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
namespace GaussianSplatLOD
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct GaussianVisibleSegment
    {
        public uint Slot, Count, PackedLevel, OutputStart;
    }
    internal sealed class GaussianGPUResources : IDisposable
    {
        public readonly ComputeBuffer Splats, Visible, PairsA, PairsB, PairsRight, Hist, Offsets, Totals, Fades;
        readonly ComputeBuffer blockSums, blockOffsets, descriptors;
        public readonly Material Material;
        public readonly int Capacity, VisibleCapacity;
        public readonly long Bytes;
        public readonly GaussianSlotAllocator Slots;
        readonly ComputeShader sort;
        readonly int init,histogram,prefixBlocks,prefixSums,scatter,buildVisible,descriptorCapacity;
        bool disposed, released;
        GaussianFence lastFence;
        static readonly List<GaussianGPUResources> retired = new List<GaussianGPUResources>();
        static GaussianGPUResources()
        {
            RenderPipelineManager.endFrameRendering += (context,cameras) => CollectRetired();
            Camera.onPostRender += camera => CollectRetired();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += CollectRetired;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += DrainRetired;
            UnityEditor.EditorApplication.quitting += DrainRetired;
#endif
            Application.quitting += DrainRetired;
        }
        // Includes both eye order buffers, histogram hierarchy and deterministic sort scratch.
        public static long SortBytes(int count)
        {
            long groups=(count+255L)/256,blocks=(groups+255)/256;
            return count*32L+groups*128+blocks*128+64;
        }
        internal static int PlanCapacity(int budgetMiB,int visibleLimit,long permanentPoints,long deviceBufferLimit,int nodeCount=1,int residentLimit=0)
        {
            long perBuffer=Math.Min(int.MaxValue,deviceBufferLimit>0?deviceBufferLimit:int.MaxValue);
            long budget=Math.Max(16,budgetMiB)*1024L*1024;
            long extra=Math.Max(1,nodeCount)*40L;
            long limit=residentLimit>0?residentLimit:16000000;
            return (int)Math.Max(0,Math.Min(perBuffer/GaussianSplat.Stride,
                Math.Min(limit,(budget-SortBytes(visibleLimit)-extra)/GaussianSplat.Stride)));
        }
        public GaussianGPUResources(int budgetMiB,int visibleLimit,Shader drawShader,ComputeShader sorting,long permanentPoints=0,int nodeCount=1,int residentLimit=0)
        {
            CollectRetired();
            try{
                if(!SystemInfo.supportsComputeShaders||SystemInfo.graphicsShaderLevel<50)throw new NotSupportedException("Gaussian rendering requires SM5 Compute Shader (DX11/DX12/Vulkan/Metal).");
                VisibleCapacity=Mathf.Clamp(visibleLimit,1,8000000);
                Capacity=PlanCapacity(budgetMiB,VisibleCapacity,permanentPoints,SystemInfo.maxGraphicsBufferSize,nodeCount,residentLimit);
                if(Capacity<1)throw new InvalidOperationException("GPU budget cannot accommodate sorting buffers. Reduce visible splats or increase GPU budget.");
                if(drawShader==null||!drawShader.isSupported||sorting==null)throw new NotSupportedException("Gaussian shader or compute shader is missing/unsupported.");
                Material=new Material(drawShader){hideFlags=HideFlags.HideAndDontSave,enableInstancing=true};
                sort=UnityEngine.Object.Instantiate(sorting);sort.hideFlags=HideFlags.HideAndDontSave;
                init=sort.FindKernel("Initialize");histogram=sort.FindKernel("Histogram");prefixBlocks=sort.FindKernel("PrefixBlocks");prefixSums=sort.FindKernel("PrefixBlockSums");scatter=sort.FindKernel("Scatter");buildVisible=sort.FindKernel("BuildVisible");
                Splats=new ComputeBuffer(Capacity,GaussianSplat.Stride);Visible=new ComputeBuffer(VisibleCapacity,8);
                PairsA=new ComputeBuffer(VisibleCapacity,8);PairsB=new ComputeBuffer(VisibleCapacity,8);PairsRight=new ComputeBuffer(VisibleCapacity,8);
                int groups=(VisibleCapacity+255)/256,blocks=(groups+255)/256;
                Hist=new ComputeBuffer(groups*16,4);Offsets=new ComputeBuffer(groups*16,4);Totals=new ComputeBuffer(16,4);
                blockSums=new ComputeBuffer(blocks*16,4);blockOffsets=new ComputeBuffer(blocks*16,4);
                Fades=new ComputeBuffer(Math.Max(1,nodeCount)*2,4);
                descriptorCapacity=Math.Max(1,nodeCount)*2;descriptors=new ComputeBuffer(descriptorCapacity,16);
                Slots=new GaussianSlotAllocator(Capacity);Bytes=Capacity*(long)GaussianSplat.Stride+SortBytes(VisibleCapacity)+Math.Max(1,nodeCount)*40L;
            }catch{ReleaseNow();throw;}
        }
        public ComputeBuffer GetOrder(int eye) => eye==0?PairsA:PairsRight;
        public void BuildVisible(CommandBuffer cmd,GaussianVisibleSegment[] segments,int segmentCount,int count)
        {
            if(count==0)return;
            if(count>VisibleCapacity||segmentCount<1||segmentCount>descriptorCapacity)throw new ArgumentOutOfRangeException(nameof(count),"Visible descriptors exceed allocated capacity.");
            cmd.BeginSample("Gaussian visible index expansion");
            // Queued upload, not immediate SetData: another camera cannot overwrite this draw's descriptors.
            cmd.SetBufferData(descriptors,segments,0,0,segmentCount);
            cmd.SetComputeIntParam(sort,"_Count",count);cmd.SetComputeIntParam(sort,"_SegmentCount",segmentCount);
            cmd.SetComputeBufferParam(sort,buildVisible,"_Segments",descriptors);cmd.SetComputeBufferParam(sort,buildVisible,"_VisibleOutput",Visible);
            cmd.DispatchCompute(sort,buildVisible,(count+255)/256,1,1);cmd.EndSample("Gaussian visible index expansion");
        }
        public void Sort(CommandBuffer cmd,Matrix4x4 viewModel,int count,int eye=0)
        {
            if(count<1)return;
            if(count>VisibleCapacity)throw new ArgumentOutOfRangeException(nameof(count));
            int groups=(count+255)/256,blocks=(groups+255)/256;
            cmd.BeginSample(eye==0?"Gaussian GPU sort":"Gaussian GPU right-eye sort");
            cmd.SetComputeIntParam(sort,"_Count",count);cmd.SetComputeIntParam(sort,"_Groups",groups);cmd.SetComputeIntParam(sort,"_Blocks",blocks);cmd.SetComputeMatrixParam(sort,"_View",viewModel);
            var output=GetOrder(eye);
            cmd.SetComputeBufferParam(sort,init,"_Splats",Splats);cmd.SetComputeBufferParam(sort,init,"_Visible",Visible);cmd.SetComputeBufferParam(sort,init,"_Destination",output);cmd.DispatchCompute(sort,init,groups,1,1);
            var src=output;var dst=PairsB;
            for(int shift=0;shift<32;shift+=4){
                cmd.SetComputeIntParam(sort,"_Shift",shift);
                cmd.SetComputeBufferParam(sort,histogram,"_Source",src);cmd.SetComputeBufferParam(sort,histogram,"_Histogram",Hist);cmd.DispatchCompute(sort,histogram,groups,1,1);
                cmd.SetComputeBufferParam(sort,prefixBlocks,"_Histogram",Hist);cmd.SetComputeBufferParam(sort,prefixBlocks,"_Offsets",Offsets);cmd.SetComputeBufferParam(sort,prefixBlocks,"_BlockSums",blockSums);cmd.SetComputeBufferParam(sort,prefixBlocks,"_Totals",Totals);cmd.DispatchCompute(sort,prefixBlocks,blocks,16,1);
                if(blocks>1){cmd.SetComputeBufferParam(sort,prefixSums,"_BlockSums",blockSums);cmd.SetComputeBufferParam(sort,prefixSums,"_BlockOffsets",blockOffsets);cmd.SetComputeBufferParam(sort,prefixSums,"_Totals",Totals);cmd.DispatchCompute(sort,prefixSums,1,16,1);}
                cmd.SetComputeBufferParam(sort,scatter,"_Source",src);cmd.SetComputeBufferParam(sort,scatter,"_Destination",dst);cmd.SetComputeBufferParam(sort,scatter,"_Offsets",Offsets);cmd.SetComputeBufferParam(sort,scatter,"_BlockOffsets",blockOffsets);cmd.SetComputeBufferParam(sort,scatter,"_Totals",Totals);cmd.DispatchCompute(sort,scatter,groups,1,1);
                var tmp=src;src=dst;dst=tmp;
            }
            cmd.EndSample(eye==0?"Gaussian GPU sort":"Gaussian GPU right-eye sort");
        }
        public struct GaussianFence
        {
            internal GraphicsFence Fence;
            internal bool Submitted,Supported;
        }
        public GaussianFence CaptureFence() => lastFence;
        internal static bool CanUseAsyncFence(bool graphicsFence,bool asyncCompute) => graphicsFence&&asyncCompute;
        public void MarkSubmitted(CommandBuffer cmd)
        {
            // Unity 2022 DX11 can advertise graphics fences while rejecting .passed on
            // AsyncQueueSynchronisation fences because async compute is unavailable.
            lastFence=new GaussianFence{Submitted=true,Supported=CanUseAsyncFence(SystemInfo.supportsGraphicsFence,SystemInfo.supportsAsyncCompute)};
            if(lastFence.Supported)lastFence.Fence=cmd.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation,SynchronisationStageFlags.PixelProcessing);
        }
        public static bool CanRecycle(GaussianFence token)
        {
            // On backends without fences, Unity's normal SetData/Dispose serialization remains
            // authoritative; it can block. Do not report this path as an async fence completion.
            if(!token.Submitted||!token.Supported)return true;
            try{return token.Fence.passed;}catch(InvalidOperationException){return false;}catch(NotSupportedException){return true;}
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            if(CanRecycle(lastFence))ReleaseNow();else retired.Add(this);
        }
        public static void CollectRetired()
        {
            for(int i=retired.Count-1;i>=0;i--)if(CanRecycle(retired[i].lastFence)){retired[i].ReleaseNow();retired.RemoveAt(i);}
        }
        static void DrainRetired(){foreach(var item in retired)item.ReleaseNow();retired.Clear();}
        void ReleaseNow()
        {
            if(released)return;released=true;
            Splats?.Dispose();Visible?.Dispose();PairsA?.Dispose();PairsB?.Dispose();PairsRight?.Dispose();Hist?.Dispose();Offsets?.Dispose();Totals?.Dispose();Fades?.Dispose();blockSums?.Dispose();blockOffsets?.Dispose();descriptors?.Dispose();Destroy(Material);Destroy(sort);
        }
        internal static void Destroy(UnityEngine.Object o){if(o==null)return;if(Application.isPlaying)UnityEngine.Object.Destroy(o);else UnityEngine.Object.DestroyImmediate(o);}
    }
}
