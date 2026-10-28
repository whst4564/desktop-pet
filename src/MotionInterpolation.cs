using System;
using System.Windows.Media.Imaging;

namespace CodexCompanion {
 // Keep the original cel for its complete authored duration. Blending even
 // motion-warped full sprites creates duplicate outlines at occlusions and
 // whenever a hand, curl, or skirt moves farther than a local flow estimate.
 // Continuous gaze and grab inertia are rendered separately and remain
 // continuous; dense authored clips do not use this compatibility sampler.
 public static class MotionInterpolation {
  public const int Samples=8;
  public static BitmapSource[] Build(BitmapSource[] frames){
   if(frames==null)throw new ArgumentNullException("frames");
   if(frames.Length==0)return new BitmapSource[0];
   if(frames[0]==null)throw new ArgumentException("Animation frame is null.","frames");
   int width=frames[0].PixelWidth,height=frames[0].PixelHeight;
   var result=new BitmapSource[checked(frames.Length*Samples)];
   for(int i=0;i<frames.Length;i++){
    if(frames[i]==null)throw new ArgumentException("Animation frame is null.","frames");
    if(frames[i].PixelWidth!=width||frames[i].PixelHeight!=height)
     throw new InvalidOperationException("Animation dimensions differ.");
    for(int sample=0;sample<Samples;sample++)result[i*Samples+sample]=frames[i];
   }
   return result;
  }
 }
}
