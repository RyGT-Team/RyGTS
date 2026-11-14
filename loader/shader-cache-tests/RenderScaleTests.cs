using System; using System.IO; using System.Drawing; using System.Diagnostics;
namespace RyGT { class RenderScaleTests {
static void Check(bool value,string message){if(!value)throw new Exception(message);}
static void SizeIs(int w,int h,string p,int sw,int sh){Check(RenderResolution.Calculate(w,h,p)==new Size(sw,sh),p+" size");}
static void Main(){
SizeIs(1920,1080,"performance",960,540);SizeIs(1920,1080,"quality",1280,720);SizeIs(1920,1080,"ultra_performance",640,360);SizeIs(1920,1080,"balanced",1136,639);SizeIs(3840,2160,"quality",2560,1440);SizeIs(3840,2160,"dlaa",3840,2160);SizeIs(1280,720,"performance",640,360);
string dir=Path.Combine(Path.GetTempPath(),"RyGT-render-scale-"+Guid.NewGuid());Directory.CreateDirectory(dir);
try { byte[] pe=new byte[512];Action<int,int> put=(at,n)=>Array.Copy(BitConverter.GetBytes(n),0,pe,at,4);put(0x3c,0x80);pe[0x86]=3;int table=0x98;int[] rvas={0x1988ad3,0x1988ab0,0x1988ab9};string[] signatures={"48b98007000038040000","8b0d72ea2301","8b0d6dea2301"};for(int i=0;i<3;i++){int at=table+i*40,raw=400+i*32;put(at+12,rvas[i]);put(at+16,signatures[i].Length/2);put(at+20,raw);for(int j=0;j<signatures[i].Length/2;j++)pe[raw+j]=Convert.ToByte(signatures[i].Substring(j*2,2),16);}File.WriteAllBytes(Path.Combine(dir,"GTSport.exe"),pe);
var start=new ProcessStartInfo();start.EnvironmentVariables.Remove("SHADGT_PATCH");RenderResolution.ConfigureStart(start,dir,new Size(960,540));Check(start.EnvironmentVariables["SHADGT_PATCH"].Contains("48b9c00300001c020000"),"allocation patch");Check(start.EnvironmentVariables["SHADGT_SCENE_WIDTH"]=="960","scene env");Check(start.EnvironmentVariables["SHADGT_EXPERIMENTAL_SCENE_VIEWPORT"]=="1","viewport hook");
var off=new ProcessStartInfo();off.EnvironmentVariables["SHADGT_SCENE_WIDTH"]="960";RenderResolution.ConfigureStart(off,dir,null);Check(off.EnvironmentVariables["SHADGT_SCENE_WIDTH"]==null,"disabled clears inherited scene size");
var conflict=new ProcessStartInfo();conflict.EnvironmentVariables["SHADGT_PATCH"]="custom";bool rejected=false;try{RenderResolution.ConfigureStart(conflict,dir,new Size(960,540));}catch(InvalidOperationException){rejected=true;}Check(rejected,"patch conflict");
pe[400]^=1;File.WriteAllBytes(Path.Combine(dir,"GTSport.exe"),pe);var invalid=new ProcessStartInfo();invalid.EnvironmentVariables.Remove("SHADGT_PATCH");rejected=false;try{RenderResolution.ConfigureStart(invalid,dir,new Size(960,540));}catch(InvalidOperationException){rejected=true;}Check(rejected&&invalid.EnvironmentVariables["SHADGT_PATCH"]==null,"bad signature fail closed");
Console.WriteLine("PASS: presets at 720p/1080p/4K, startup patch bytes, dimensions, disabled cleanup, conflicting patch and signature rejection");
}finally{Directory.Delete(dir,true);}
} } }

