using System;
using System.Runtime.InteropServices;
using Forms=System.Windows.Forms;

namespace LuminaDesktop
{
    // Two reusable native DIBs: one source, one screen-sized destination. No image cache.
    public sealed class NativeSpriteLayer:Forms.NativeWindow,IDisposable
    {
        readonly PetWindow owner;IntPtr dc,dib,oldBitmap,bits,sourceDc,sourceDib,oldSource,sourceBits;
        int sourceW,sourceH,width,height;bool visible;public long SuccessfulPresents;
        [StructLayout(LayoutKind.Sequential)]struct Point{public int X,Y;public Point(int x,int y){X=x;Y=y;}}
        [StructLayout(LayoutKind.Sequential)]struct Size{public int Width,Height;public Size(int w,int h){Width=w;Height=h;}}
        [StructLayout(LayoutKind.Sequential,Pack=1)]struct Blend{public byte Operation,Flags,Alpha,Format;}
        [StructLayout(LayoutKind.Sequential)]struct Info{public uint Size;public int Width,Height;public ushort Planes,BitCount;public uint Compression,SizeImage;public int X,Y;public uint Colors,Important;}
        [DllImport("gdi32.dll")]static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")]static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")]static extern IntPtr CreateDIBSection(IntPtr dc,ref Info info,uint usage,out IntPtr data,IntPtr section,uint offset);
        [DllImport("gdi32.dll")]static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
        [DllImport("gdi32.dll")]static extern bool DeleteObject(IntPtr obj);
        [DllImport("msimg32.dll",SetLastError=true)]static extern bool AlphaBlend(IntPtr dst,int x,int y,int w,int h,IntPtr src,int sx,int sy,int sw,int sh,Blend blend);
        [DllImport("msvcrt.dll",CallingConvention=CallingConvention.Cdecl)]static extern IntPtr memset(IntPtr dst,int value,UIntPtr count);
        [DllImport("user32.dll",SetLastError=true)]static extern bool UpdateLayeredWindow(IntPtr window,IntPtr screen,ref Point target,ref Size size,IntPtr memory,ref Point source,int key,ref Blend blend,uint flags);
        [DllImport("user32.dll")]static extern bool ShowWindow(IntPtr h,int command);
        [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]static extern IntPtr SetCapture(IntPtr h);
        [DllImport("user32.dll")]static extern bool ReleaseCapture();
        public bool Ready{get{return SuccessfulPresents>0&&IsWindowVisible(Handle);}}
        public NativeSpriteLayer(IntPtr parent,PetWindow pet){owner=pet;CreateHandle(new Forms.CreateParams{Caption="Lumina sprite surface",Parent=parent,Style=unchecked((int)0x80000000),ExStyle=0x80000|0x80|0x08000000,ClassStyle=8});dc=CreateCompatibleDC(IntPtr.Zero);sourceDc=CreateCompatibleDC(IntPtr.Zero);}
        IntPtr Allocate(IntPtr device,int w,int h,out IntPtr pixels,out IntPtr old)
        {
            var info=new Info{Size=40,Width=w,Height=-h,Planes=1,BitCount=32,SizeImage=(uint)(w*h*4)};
            IntPtr bitmap=CreateDIBSection(device,ref info,0,out pixels,IntPtr.Zero,0);if(bitmap==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();old=SelectObject(device,bitmap);return bitmap;
        }
        public void Present(byte[] raw,int w,int h,int x,int y,int destW,int destH)
        {
            if(destW<1||destH<1)return;
            if(sourceDib==IntPtr.Zero||sourceW!=w||sourceH!=h){if(sourceDib!=IntPtr.Zero){SelectObject(sourceDc,oldSource);DeleteObject(sourceDib);}sourceDib=Allocate(sourceDc,w,h,out sourceBits,out oldSource);sourceW=w;sourceH=h;}
            if(dib==IntPtr.Zero||width!=destW||height!=destH){if(dib!=IntPtr.Zero){SelectObject(dc,oldBitmap);DeleteObject(dib);}dib=Allocate(dc,destW,destH,out bits,out oldBitmap);width=destW;height=destH;}
            int bytes=checked(w*h*4);if(raw.Length<bytes)throw new ArgumentException("Short pixel buffer");
            Marshal.Copy(raw,0,sourceBits,bytes);memset(bits,0,(UIntPtr)(width*height*4));
            double scale=Math.Min(width/(double)w,height/(double)h);int rw=(int)Math.Round(w*scale),rh=(int)Math.Round(h*scale);var blend=new Blend{Alpha=255,Format=1};
            if(!AlphaBlend(dc,(width-rw)/2,(height-rh)/2,rw,rh,sourceDc,0,0,w,h,blend))throw new System.ComponentModel.Win32Exception();
            var target=new Point(x,y);var size=new Size(width,height);var origin=new Point(0,0);
            if(!UpdateLayeredWindow(Handle,IntPtr.Zero,ref target,ref size,dc,ref origin,0,ref blend,2))throw new System.ComponentModel.Win32Exception();SuccessfulPresents++;
            if(!visible||!IsWindowVisible(Handle)){ShowWindow(Handle,4);if(!IsWindowVisible(Handle))ShowWindow(Handle,4);visible=true;}
        }
        public void SetVisible(bool value){visible=value;ShowWindow(Handle,value?4:0);}
        public void SetThrough(bool value){int style=Native.GetWindowLong(Handle,-20);Native.SetWindowLong(Handle,-20,value?style|0x20:style&~0x20);}
        public System.Windows.Media.Imaging.BitmapSource Snapshot(double dpiX,double dpiY){if(bits==IntPtr.Zero)return null;var bytes=new byte[width*height*4];Marshal.Copy(bits,bytes,0,bytes.Length);var bitmap=System.Windows.Media.Imaging.BitmapSource.Create(width,height,96*dpiX,96*dpiY,System.Windows.Media.PixelFormats.Pbgra32,null,bytes,width*4);bitmap.Freeze();return bitmap;}
        protected override void WndProc(ref Forms.Message m)
        {
            switch(m.Msg){case 0x201:SetCapture(Handle);owner.NativeDown(false);m.Result=IntPtr.Zero;return;case 0x203:owner.NativeDown(true);m.Result=IntPtr.Zero;return;case 0x200:owner.NativeMove();break;case 0x202:ReleaseCapture();owner.NativeUp();m.Result=IntPtr.Zero;return;case 0x205:owner.NativeMenu();m.Result=IntPtr.Zero;return;case 0x20A:int delta=(short)((m.WParam.ToInt64()>>16)&0xffff);owner.SetHeight(owner.Config.Height+(delta>0?40:-40));m.Result=IntPtr.Zero;return;}base.WndProc(ref m);
        }
        public void Dispose(){if(dib!=IntPtr.Zero){SelectObject(dc,oldBitmap);DeleteObject(dib);dib=IntPtr.Zero;}if(sourceDib!=IntPtr.Zero){SelectObject(sourceDc,oldSource);DeleteObject(sourceDib);sourceDib=IntPtr.Zero;}if(dc!=IntPtr.Zero){DeleteDC(dc);dc=IntPtr.Zero;}if(sourceDc!=IntPtr.Zero){DeleteDC(sourceDc);sourceDc=IntPtr.Zero;}if(Handle!=IntPtr.Zero)DestroyHandle();}
    }
}
