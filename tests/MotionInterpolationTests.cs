using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexCompanion;

public static class MotionInterpolationTests {
 static int checks;
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
 static BitmapSource Frame(int width,int height,int left,int top,int length,byte alpha){
  byte[] pixels=new byte[width*height*4];
  for(int y=top;y<top+length;y++)for(int x=left;x<left+length;x++){
   int n=(y*width+x)*4;pixels[n]=20;pixels[n+1]=80;pixels[n+2]=170;pixels[n+3]=alpha;
  }
  // Hidden colour in a transparent pixel must not become a visible halo.
  pixels[0]=pixels[1]=pixels[2]=255;
  var bitmap=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);bitmap.Freeze();return bitmap;
 }
 static byte[] Pixels(BitmapSource frame){byte[] data=new byte[frame.PixelWidth*frame.PixelHeight*4];frame.CopyPixels(data,frame.PixelWidth*4,0);return data;}
 static bool Same(byte[] a,byte[] b){if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
 [STAThread] public static void Main(){
  var a=Frame(64,48,4,10,10,255);var b=Frame(64,48,41,23,10,255);
  var prepared=MotionInterpolation.Build(new[]{a,b});
  Check(prepared.Length==16,"compatibility sample count preserves clip timing");
  bool originals=true,pixels=true,noHalfOpacity=true;
  for(int i=0;i<prepared.Length;i++){
   BitmapSource expected=i<8?a:b;
   originals&=Object.ReferenceEquals(prepared[i],expected);
   byte[] data=Pixels(prepared[i]);pixels&=Same(data,Pixels(expected));
   for(int n=3;n<data.Length;n+=4)noHalfOpacity&=data[n]==0||data[n]==255;
  }
  Check(originals,"disoccluded moving features retain exactly one original cel per sample");
  Check(pixels,"large diagonal feature motion introduces no new pixels or resampling");
  Check(noHalfOpacity,"opaque motion does not create two half-alpha silhouettes");
  byte[] early=Pixels(prepared[4]),late=Pixels(prepared[12]);
  Check(early[(25*64+43)*4+3]==0 && late[(12*64+6)*4+3]==0,"old and new feature positions never coexist as a ghost pair");
  Check(early[3]==0&&late[3]==0,"transparent hidden colour remains transparent without a halo");
  var soft=Frame(64,48,20,15,10,93);var softPrepared=MotionInterpolation.Build(new[]{soft,a});
  bool alphaPreserved=true;for(int i=0;i<8;i++)alphaPreserved&=Same(Pixels(softPrepared[i]),Pixels(soft));
  Check(alphaPreserved,"authored translucent edge coverage remains pixel-identical");
  Check(MotionInterpolation.Build(new BitmapSource[0]).Length==0,"empty clips remain empty");
  bool nullRejected=false;try{MotionInterpolation.Build(null);}catch(ArgumentNullException){nullRejected=true;}Check(nullRejected,"null clip is rejected explicitly");
  bool missingRejected=false;try{MotionInterpolation.Build(new BitmapSource[]{a,null});}catch(ArgumentException){missingRejected=true;}Check(missingRejected,"missing cels are rejected before drawing");
  bool dimensionRejected=false;try{MotionInterpolation.Build(new[]{a,Frame(32,48,4,4,6,255)});}catch(InvalidOperationException){dimensionRejected=true;}Check(dimensionRejected,"inconsistent cel dimensions remain an explicit error");
  Check(a.IsFrozen&&b.IsFrozen&&soft.IsFrozen,"input sprite ownership and frozen state remain unchanged");
  Console.WriteLine("ALL "+checks+" GHOSTING CHECKS PASSED");
 }
}
