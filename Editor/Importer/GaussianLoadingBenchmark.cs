using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace GaussianSplatLOD.Editor
{
    /// <summary>CPU/IO benchmark only. Does not report device FPS or GPU upload time.</summary>
    public static class GaussianLoadingBenchmark
    {
        static Task running;
        public static string Begin(string json)
        {
            if(running!=null&&!running.IsCompleted)throw new InvalidOperationException("Loading benchmark already running.");
            string report=Path.GetFullPath("Reports/GaussianSplatLOD/loading-cache-benchmark.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            string unity=Application.unityVersion,device=SystemInfo.processorType;
            File.WriteAllText(report,"{\"status\":\"building caches\"}");
            running=Task.Run(()=>{
                try {
                    var loader=new GaussianLODLoader(json,false);var build=Stopwatch.StartNew();
                    GaussianRuntimeCacheWindow.BuildDataset(json,CancellationToken.None);
                    double buildSeconds=build.Elapsed.TotalSeconds;
                    var samples=new List<object>();
                    // The two permanent files determine campus startup; also include one detail file.
                    var files=new HashSet<int>(loader.Metadata.permanentFiles);if(loader.Metadata.files.Length>2)files.Add(2);
                    foreach(int file in files)
                    {
                        string path=loader.FilePath(file);var h=loader.Header(file);
                        int n=(int)Math.Min(100000,h.VertexCount);var cached=GaussianRuntimeCache.Validate(path+GaussianRuntimeCache.Extension,path,h.SHDegree);
                        // Warm both paths before alternating measured runs; OS page cache is not flushed.
                        GaussianPLYReader.ReadRange(path,h,0,n,h.SHDegree,CancellationToken.None);
                        GaussianRuntimeCache.ReadRange(path+GaussianRuntimeCache.Extension,cached,0,n,CancellationToken.None);
                        var plyTimes=new double[5];var cacheTimes=new double[5];
                        for(int trial=0;trial<5;trial++)
                        {
                            bool cachedFirst=trial%2!=0;
                            for(int pass=0;pass<2;pass++)
                            {
                                bool readCache=(pass==0)==cachedFirst;var watch=Stopwatch.StartNew();
                                var data=readCache?GaussianRuntimeCache.ReadRange(path+GaussianRuntimeCache.Extension,cached,0,n,CancellationToken.None):GaussianPLYReader.ReadRange(path,h,0,n,h.SHDegree,CancellationToken.None);
                                double ms=watch.Elapsed.TotalMilliseconds;GC.KeepAlive(data);
                                if(readCache)cacheTimes[trial]=ms;else plyTimes[trial]=ms;
                            }
                        }
                        Array.Sort(plyTimes);Array.Sort(cacheTimes);
                        samples.Add(new{file=loader.Metadata.files[file],count=n,plyMedianMilliseconds=plyTimes[2],cacheMedianMilliseconds=cacheTimes[2],ratio=plyTimes[2]/Math.Max(.001,cacheTimes[2])});
                    }
                    File.WriteAllText(report,JsonConvert.SerializeObject(new{status="passed",unity,processor=device,json,buildSeconds,files=loader.Metadata.files.Length,shDegree=loader.Metadata.shDegree,notes="Windows Editor CPU decoding + warm filesystem reads only. Includes output allocation. Excludes metadata scheduling, GPU upload, rendering and cold device storage. No PICO/iPhone measurements.",samples},Formatting.Indented));
                }catch(Exception e){File.WriteAllText(report,JsonConvert.SerializeObject(new{status="failed",error=e.ToString()},Formatting.Indented));}
            });
            return report;
        }
    }
}
