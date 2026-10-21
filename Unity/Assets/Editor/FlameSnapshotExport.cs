using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public static class FlameSnapshotExport {
 [Serializable] public class TypeRow {public string type;public long bytes;public int count;}
 [Serializable] public class Summary {public string name;public long nativeObjectBytes,managedHeapSectionBytes;public int nativeObjects,gcHandles;public List<TypeRow> nativeTypes=new List<TypeRow>();}
 public static void Run() {
  string root=Environment.GetEnvironmentVariable("FLAME_SNAPSHOTS");
  var api=Type.GetType("UnityEditor.Profiling.Memory.Experimental.PackedMemorySnapshot, UnityEditor.CoreModule",true);
  foreach(string path in Directory.GetFiles(root,"*.snap")) {
   object snapshot=api.GetMethod("Load").Invoke(null,new object[]{path});
   try {
   var row=new Summary{name=Path.GetFileNameWithoutExtension(path)};
   var objects=Property(snapshot,"nativeObjects");var sizes=Entries(Property(objects,"size"));var indices=Entries(Property(objects,"nativeTypeArrayIndex"));var names=(string[])Entries(Property(Property(snapshot,"nativeTypes"),"typeName"));
   var types=new Dictionary<string,TypeRow>();
   for(int i=0;i<sizes.Length;i++) {
    string name=names[Convert.ToInt32(indices.GetValue(i))];if(!types.TryGetValue(name,out var t)){t=new TypeRow{type=name};types[name]=t;}
    long bytes=Convert.ToInt64(sizes.GetValue(i));t.count++;t.bytes+=bytes;row.nativeObjectBytes+=bytes;row.nativeObjects++;
   }
   row.nativeTypes=types.Values.OrderByDescending(t=>t.bytes).ToList();
   object handles=Property(snapshot,"gcHandles");row.gcHandles=Convert.ToInt32(handles.GetType().GetMethod("GetNumEntries").Invoke(handles,null));
   foreach(var section in (byte[][])Entries(Property(Property(snapshot,"managedHeapSections"),"bytes"))) row.managedHeapSectionBytes+=section.LongLength;
   File.WriteAllText(Path.Combine(root,row.name+".summary.json"),JsonUtility.ToJson(row,true));
   Debug.Log("Snapshot exported: "+row.name+" native bytes="+row.nativeObjectBytes+" native objects="+row.nativeObjects);
   } finally {((IDisposable)snapshot).Dispose();}
  }
 }
 static object Property(object o,string name){return o.GetType().GetProperty(name).GetValue(o,null);}
 static Array Entries(object entries) {
  uint count=Convert.ToUInt32(entries.GetType().GetMethod("GetNumEntries").Invoke(entries,null));
  var arrayType=entries.GetType().GetMethod("GetEntries").GetParameters()[2].ParameterType.GetElementType();
  Array array=Array.CreateInstance(arrayType.GetElementType(),(int)count);
  object[] args={0u,count,array};entries.GetType().GetMethod("GetEntries").Invoke(entries,args);return (Array)args[2];
 }
}
