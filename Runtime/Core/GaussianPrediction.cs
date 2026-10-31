using System;
using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatLOD
{
    internal static class GaussianPrediction
    {
        public static float DistanceToSegment(Vector3 point,Vector3 from,Vector3 to)
        {
            Vector3 delta=to-from;float denominator=delta.sqrMagnitude;
            float t=denominator<1e-8f?0:Mathf.Clamp01(Vector3.Dot(point-from,delta)/denominator);
            return Vector3.Distance(point,from+t*delta);
        }
    }
    /// <summary>Static median-split index rebuilt only when the model transform changes.</summary>
    internal sealed class GaussianChunkBVH
    {
        struct Node {public Bounds Bounds;public int Left,Right,Item;}
        readonly Bounds[] bounds;readonly int[] ids;readonly Node[] nodes;int used;
        public GaussianChunkBVH(Bounds[] source)
        {
            bounds=source;ids=new int[source.Length];nodes=new Node[Math.Max(1,source.Length*2)];
            for(int i=0;i<ids.Length;i++)ids[i]=i;
            if(ids.Length>0)Build(0,ids.Length);
        }
        int Build(int start,int count)
        {
            int id=used++;var b=bounds[ids[start]];for(int i=1;i<count;i++)b.Encapsulate(bounds[ids[start+i]]);
            if(count==1){nodes[id]=new Node{Bounds=b,Item=ids[start],Left=-1,Right=-1};return id;}
            Vector3 size=b.size;int axis=size.x>=size.y&&size.x>=size.z?0:size.y>=size.z?1:2;
            Array.Sort(ids,start,count,Comparer<int>.Create((a,c)=>bounds[a].center[axis].CompareTo(bounds[c].center[axis])));
            int left=Build(start,count/2),right=Build(start+count/2,count-count/2);
            nodes[id]=new Node{Bounds=b,Item=-1,Left=left,Right=right};return id;
        }
        public void Query(GaussianViewContext views,List<int> output){output.Clear();if(used>0)Visit(0,views,output);}
        void Visit(int id,GaussianViewContext views,List<int> output)
        {
            var node=nodes[id];if(!views.TestBounds(node.Bounds))return;
            if(node.Item>=0)output.Add(node.Item);else{Visit(node.Left,views,output);Visit(node.Right,views,output);}
        }
    }
}
