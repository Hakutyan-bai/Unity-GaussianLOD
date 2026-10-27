using System;
using System.Collections.Generic;
namespace GaussianSplatLOD
{
    /// <summary>Coalescing first-fit arena; individual splats never own Unity objects.</summary>
    public sealed class GaussianSlotAllocator
    {
        struct Block { public int start, count; }
        readonly List<Block> free=new List<Block>();
        public int FreeCount { get; private set; }
        public GaussianSlotAllocator(int capacity) { free.Add(new Block{start=0,count=capacity});FreeCount=capacity; }
        public int Allocate(int count)
        {
            if(count<=0)return -1;
            for(int i=0;i<free.Count;i++) { var b=free[i];if(b.count<count)continue;int at=b.start;b.start+=count;b.count-=count;if(b.count==0)free.RemoveAt(i);else free[i]=b;FreeCount-=count;return at; }
            return -1;
        }
        public void Release(int start,int count)
        {
            if(start<0||count<=0)throw new ArgumentOutOfRangeException();
            int i=0;while(i<free.Count&&free[i].start<start)i++;
            if((i>0&&free[i-1].start+free[i-1].count>start)||(i<free.Count&&start+count>free[i].start))throw new InvalidOperationException("GPU arena double release/overlap.");
            free.Insert(i,new Block{start=start,count=count});FreeCount+=count;
            if(i>0&&free[i-1].start+free[i-1].count==start){var a=free[i-1];a.count+=count;free[i-1]=a;free.RemoveAt(i);i--;}
            if(i+1<free.Count&&free[i].start+free[i].count==free[i+1].start){var a=free[i];a.count+=free[i+1].count;free[i]=a;free.RemoveAt(i+1);}
        }
    }
}
