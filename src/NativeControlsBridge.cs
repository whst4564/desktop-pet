using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace CodexCompanion {
 // Move only the existing frameless, nonactivating ChatGPT Mini window. No
 // browser injection, synthetic clicks, reimplemented voice, or file writes.
 // Electron's native move listener updates/persists its own overlay anchor.
 public sealed class NativeControlsBridge {
  [StructLayout(LayoutKind.Sequential)]struct NativeRect {public int Left,Top,Right,Bottom;}
  delegate bool EnumProc(IntPtr window,IntPtr state);
  [DllImport("user32.dll")]static extern bool EnumWindows(EnumProc proc,IntPtr state);
  [DllImport("user32.dll")]static extern bool IsWindow(IntPtr window);
  [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr window);
  [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr window,StringBuilder name,int length);
  [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]static extern IntPtr GetWindowLong64(IntPtr window,int index);
  [DllImport("user32.dll",EntryPoint="GetWindowLongW")]static extern IntPtr GetWindowLong32(IntPtr window,int index);
  [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr window,out NativeRect bounds);
  [DllImport("user32.dll",SetLastError=true)]static extern bool SetWindowPos(IntPtr window,IntPtr order,int x,int y,int width,int height,uint flags);
  [DllImport("user32.dll")]static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
  IntPtr handle;NativeRect start;bool moving;
  public bool IsAttached {get{return handle!=IntPtr.Zero&&IsWindow(handle)&&IsWindowVisible(handle);}}
  public bool Attach(){
   if(IsAttached)return true;
   handle=IntPtr.Zero;var found=new List<IntPtr>();
   EnumWindows(delegate(IntPtr window,IntPtr unused){
    try{
     if(!IsWindowVisible(window))return true;
     long style=Long(window,-16),ex=Long(window,-20);
     if((style&0x00C00000)!=0||(ex&8)==0||((ex&0x80)==0&&(ex&0x08000000)==0))return true;
     var name=new StringBuilder(128);GetClassName(window,name,name.Capacity);
     if(!name.ToString().StartsWith("Chrome_WidgetWin",StringComparison.Ordinal))return true;
     uint pid;GetWindowThreadProcessId(window,out pid);
     using(var process=Process.GetProcessById((int)pid)){
      string path=process.MainModule.FileName;
      bool executable=path.EndsWith("\\Codex.exe",StringComparison.OrdinalIgnoreCase)||path.EndsWith("\\ChatGPT.exe",StringComparison.OrdinalIgnoreCase);
      if(path.IndexOf("\\WindowsApps\\OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)<0||!executable)return true;
     }
     NativeRect bounds;if(!GetWindowRect(window,out bounds)||bounds.Right-bounds.Left<80||bounds.Bottom-bounds.Top<20)return true;
     found.Add(window);
    }catch{}return true;
   },IntPtr.Zero);
   // Ambiguity must never move another floating ChatGPT window.
   if(found.Count==1)handle=found[0];return IsAttached;
  }
  static long Long(IntPtr h,int index){return(IntPtr.Size==8?GetWindowLong64(h,index):GetWindowLong32(h,index)).ToInt64();}
  public bool BeginDrag(){
   moving=false;if(!Attach())return false;
   IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
   try{moving=GetWindowRect(handle,out start);return moving;}finally{if(previous!=IntPtr.Zero)SetThreadDpiAwarenessContext(previous);}
  }
  public void DragBy(Vector physicalDelta){
   if(!moving||!IsAttached)return;
   IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
   try{SetWindowPos(handle,IntPtr.Zero,start.Left+(int)Math.Round(physicalDelta.X),start.Top+(int)Math.Round(physicalDelta.Y),0,0,0x0015);}
   finally{if(previous!=IntPtr.Zero)SetThreadDpiAwarenessContext(previous);}
  }
  public void EndDrag(){moving=false;}
 }
}
