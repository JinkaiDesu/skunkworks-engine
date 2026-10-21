using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

public static class FlameExport {
 static string Safe(string s) { return (s ?? "[unresolved]").Replace(';', ':').Replace('\n', ' ').Replace('\r', ' '); }
 public static void Run() {
  string input=Environment.GetEnvironmentVariable("FLAME_INPUT"), output=Environment.GetEnvironmentVariable("FLAME_EXPORT");
  Directory.CreateDirectory(output);
  UnityEngine.Profiling.Profiler.maxUsedMemory=1024*1024*1024;
  var history=typeof(ProfilerDriver).GetMethod("SetMaxFrameHistoryLength", BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
  if(history!=null) history.Invoke(null,new object[]{10000});
  typeof(ProfilerDriver).GetMethod("SetAutomaticMemoryManagement", BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)?.Invoke(null,new object[]{false});
  if(!ProfilerDriver.LoadProfile(input,false)) throw new Exception("Could not load "+input);
  var cpu=new Dictionary<string,double>(); var alloc=new Dictionary<string,double>();
  var addresses=new List<ulong>(); var resolved=new Dictionary<ulong,string>();
  long samples=0, allocations=0, bytes=0, resolvedAddresses=0, unknownAddresses=0; int frames=0;
  int first=ProfilerDriver.firstFrameIndex, last=ProfilerDriver.lastFrameIndex;
  for(int frame=first;frame<=last;frame++) {
   bool any=false;
   for(int thread=0;thread<512;thread++) using(var data=ProfilerDriver.GetRawFrameDataView(frame,thread)) {
    if(!data.valid) break;
    any=true;
    var path=new List<string>{Safe(data.threadName)}; var ends=new List<int>{int.MaxValue};
    for(int i=0;i<data.sampleCount;i++) {
     while(ends.Count>1 && i>ends[ends.Count-1]) { ends.RemoveAt(ends.Count-1); path.RemoveAt(path.Count-1); }
     string name=Safe(data.GetSampleName(i)); path.Add(name);
     int descendants=data.GetSampleChildrenCountRecursive(i), end=i+descendants;
     double exclusive=data.GetSampleTimeMs(i);
     for(int child=i+1;child<=end;) { exclusive-=data.GetSampleTimeMs(child); child+=1+data.GetSampleChildrenCountRecursive(child); }
     if(exclusive>0) Add(cpu,string.Join(";",path),exclusive);
     if(name=="GC.Alloc" && data.GetSampleMetadataCount(i)>0) {
      long size=data.GetSampleMetadataAsLong(i,0);
      if(size>0) {
       addresses.Clear(); data.GetSampleCallstack(i,addresses);
       var stack=new List<string>{Safe(data.threadName)};
       foreach(ulong address in Enumerable.Reverse(addresses)) {
        if(!resolved.TryGetValue(address,out string method)) {
         method=data.ResolveMethodInfo(address).methodName;
         if(string.IsNullOrEmpty(method)) {method="[unresolved 0x"+address.ToString("x")+"]";unknownAddresses++;} else resolvedAddresses++;
         resolved[address]=Safe(method);
        }
        stack.Add(resolved[address]);
       }
       if(addresses.Count==0) {stack.Add("[marker ancestry: no allocation callstack]");stack.AddRange(path.Skip(1));}
       Add(alloc,string.Join(";",stack),size); bytes+=size; allocations++;
      }
     }
     samples++; ends.Add(end);
    }
   }
   if(any)frames++;
  }
  Write(Path.Combine(output,"cpu-scopes.folded"),cpu); Write(Path.Combine(output,"managed-allocations.folded"),alloc);
  File.WriteAllText(Path.Combine(output,"export.json"),"{\"firstFrame\":"+first+",\"lastFrame\":"+last+",\"frames\":"+frames+",\"samples\":"+samples+",\"allocationEvents\":"+allocations+",\"allocationBytes\":"+bytes+",\"resolvedAddresses\":"+resolvedAddresses+",\"unknownAddresses\":"+unknownAddresses+"}");
  if(samples==0) throw new Exception("No profiler samples exported");
  Debug.Log("FLAME_EXPORT complete: "+frames+" frames, "+allocations+" allocations, "+bytes+" bytes");
 }
 static void Add(Dictionary<string,double> d,string key,double n) {if(d.ContainsKey(key))d[key]+=n;else d[key]=n;}
 static void Write(string path,Dictionary<string,double> d) {using(var w=new StreamWriter(path)) foreach(var item in d.OrderBy(x=>x.Key))w.WriteLine(item.Key+" "+item.Value.ToString("R",CultureInfo.InvariantCulture));}
}
