#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Profiling;
using Unity.Profiling.Memory;
using Debug = UnityEngine.Debug;
public sealed class FlameCapture : MonoBehaviour {
 string root, mode; float seconds; StreamWriter csv; Process process;
 [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
 static void Install() {
  if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLAME_OUTPUT"))) return;
  var go=new GameObject("ProfilingCapture"); DontDestroyOnLoad(go); go.AddComponent<FlameCapture>();
 }
 IEnumerator Start() {
  root=Environment.GetEnvironmentVariable("FLAME_OUTPUT"); Directory.CreateDirectory(root);
  mode=Environment.GetEnvironmentVariable("FLAME_MODE")??"cpu";
  seconds=float.TryParse(Environment.GetEnvironmentVariable("FLAME_SECONDS"),out var n)?n:10;
  QualitySettings.vSyncCount=0; Application.targetFrameRate=60;
  process=Process.GetCurrentProcess();
  csv=new StreamWriter(Path.Combine(root,"memory.csv")); csv.WriteLine("seconds,private_bytes,working_set,mono_used,mono_reserved,unity_allocated,unity_reserved,gc0,gc1,gc2");
  yield return new WaitForSecondsRealtime(5);
  if(mode=="snapshots") yield return Snapshot("warm");
  Profiler.maxUsedMemory=32*1024*1024;
  Profiler.logFile=Path.Combine(root,"capture.raw"); Profiler.enableBinaryLog=true;
  Profiler.enableAllocationCallstacks=mode=="alloc"; Profiler.enabled=mode!="snapshots";
  File.WriteAllText(Path.Combine(root,"ready.json"),"{\"pid\":"+process.Id+",\"mode\":\""+mode+"\"}");
  double start=Time.realtimeSinceStartupAsDouble;
  while(Time.realtimeSinceStartupAsDouble-start<seconds) { Row(Time.realtimeSinceStartupAsDouble-start); yield return new WaitForSecondsRealtime(0.5f); }
  Profiler.enabled=false; Profiler.enableBinaryLog=false; csv.Flush(); csv.Dispose(); csv=null;
  File.WriteAllText(Path.Combine(root,"workload-complete.txt"),"complete");
  if(mode=="snapshots") {
   yield return Snapshot("after-workload");
   GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); yield return Resources.UnloadUnusedAssets();
   yield return new WaitForSecondsRealtime(2); yield return Snapshot("after-cleanup");
  }
  File.WriteAllText(Path.Combine(root,"complete.txt"),"complete");
  yield return new WaitForSecondsRealtime(1); Application.Quit(0);
 }
 IEnumerator Snapshot(string name) {
  bool done=false; bool ok=false;
  MemoryProfiler.TakeSnapshot(Path.Combine(root,name+".snap"),(p,s)=>{ok=s;done=true;},CaptureFlags.ManagedObjects|CaptureFlags.NativeObjects);
  double start=Time.realtimeSinceStartupAsDouble;
  while(!done&&Time.realtimeSinceStartupAsDouble-start<90) yield return null;
  File.AppendAllText(Path.Combine(root,"snapshots.log"),name+" completed="+done+" success="+ok+"\n");
 }
 void Row(double t) { process.Refresh(); csv.WriteLine(string.Join(",",t.ToString("F3",CultureInfo.InvariantCulture),process.PrivateMemorySize64,process.WorkingSet64,Profiler.GetMonoUsedSizeLong(),Profiler.GetMonoHeapSizeLong(),Profiler.GetTotalAllocatedMemoryLong(),Profiler.GetTotalReservedMemoryLong(),GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2))); csv.Flush(); }
 void OnDestroy(){csv?.Dispose();process?.Dispose();}
}
#endif
