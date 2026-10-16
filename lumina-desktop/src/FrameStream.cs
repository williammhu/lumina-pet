using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Runtime.InteropServices;

namespace LuminaDesktop
{
    // One reusable premultiplied pixel buffer, independent of animation length.
    public sealed class FrameStream : IDisposable
    {
        FileStream file;
        IntPtr decompressor;byte[] compressed;bool nativeCodec;
        [DllImport("cabinet.dll",SetLastError=true)]static extern bool CreateDecompressor(uint algorithm,IntPtr allocator,out IntPtr handle);
        [DllImport("cabinet.dll",SetLastError=true)]static extern bool Decompress(IntPtr handle,byte[] input,UIntPtr inputSize,byte[] output,UIntPtr outputSize,out UIntPtr written);
        [DllImport("cabinet.dll")]static extern bool CloseDecompressor(IntPtr handle);
        long[] offsets; int[] lengths;
        public int Width,Height,Fps,Count;
        public byte[] Pixels;
        public string State="";
        public int LastFrame=-1;
        public long DecodedFrames;
        public int CompressedBufferBytes{get{return compressed==null?0:compressed.Length;}}
        public string Codec{get{return nativeCodec?"windows-xpress":"deflate";}}
        public void ReserveCompressed(int size){if(size<1||size>1280*1536*4+65536)throw new InvalidDataException("Compressed frame bound");if(compressed==null||compressed.Length<size)compressed=new byte[size];}
        public void Open(string state,string path)
        {
            if(file!=null)file.Dispose();
            file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.RandomAccess);
            using(var reader=new BinaryReader(file,Encoding.UTF8,true)) {
                string magic=Encoding.ASCII.GetString(reader.ReadBytes(8));nativeCodec=magic=="LUMAFR3\0";
                if(!nativeCodec&&magic!="LUMAFR2\0")throw new InvalidDataException("Invalid animation pack");
                Width=reader.ReadInt32();Height=reader.ReadInt32();Fps=reader.ReadInt32();Count=reader.ReadInt32();
                if(Width<1||Height<1||Width>1440||Height>2160||Fps<1||Fps>60||Count<1||Count>3600)throw new InvalidDataException("Animation bounds");
                offsets=new long[Count];lengths=new int[Count];
                for(int i=0;i<Count;i++){offsets[i]=reader.ReadInt64();lengths[i]=reader.ReadInt32();reader.ReadUInt32();if(offsets[i]<24+Count*16||lengths[i]<1||offsets[i]+lengths[i]>file.Length)throw new InvalidDataException("Animation index");}
                if(nativeCodec){int largest=0;foreach(int n in lengths)largest=Math.Max(largest,n);ReserveCompressed(largest);}
            }
            int size=checked(Width*Height*4);if(Pixels==null||Pixels.Length!=size)Pixels=new byte[size];
            if(nativeCodec&&decompressor==IntPtr.Zero&&!CreateDecompressor(3,IntPtr.Zero,out decompressor))throw new System.ComponentModel.Win32Exception();
            State=state;LastFrame=-1;
        }
        public void Read(int index)
        {
            if(index==LastFrame)return;
            if(index<0||index>=Count)throw new ArgumentOutOfRangeException("index");
            file.Position=offsets[index];
            if(nativeCodec){
                int position=0;while(position<lengths[index]){int got=file.Read(compressed,position,lengths[index]-position);if(got==0)throw new EndOfStreamException();position+=got;}
                UIntPtr written;if(!Decompress(decompressor,compressed,(UIntPtr)lengths[index],Pixels,(UIntPtr)Pixels.Length,out written))throw new System.ComponentModel.Win32Exception();
                if(written.ToUInt64()!=(ulong)Pixels.Length)throw new InvalidDataException("Decoded frame size");
                LastFrame=index;DecodedFrames++;return;
            }
            using(var bounded=new BoundedStream(file,lengths[index]))
            using(var decoder=new DeflateStream(bounded,CompressionMode.Decompress,false)) {
                int position=0;
                while(position<Pixels.Length){int got=decoder.Read(Pixels,position,Pixels.Length-position);if(got==0)throw new EndOfStreamException();position+=got;}
                if(decoder.ReadByte()!=-1)throw new InvalidDataException("Oversized animation frame");
            }
            LastFrame=index;DecodedFrames++;
        }
        public void Dispose(){if(file!=null){file.Dispose();file=null;}if(decompressor!=IntPtr.Zero){CloseDecompressor(decompressor);decompressor=IntPtr.Zero;}Pixels=null;compressed=null;}
    }
    sealed class BoundedStream : Stream
    {
        readonly Stream source;long remaining;
        public BoundedStream(Stream s,long count){source=s;remaining=count;}
        public override int Read(byte[] b,int offset,int count){int got=source.Read(b,offset,(int)Math.Min(count,remaining));remaining-=got;return got;}
        public override bool CanRead{get{return true;}}public override bool CanWrite{get{return false;}}public override bool CanSeek{get{return false;}}
        public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
        public override void Flush(){}public override long Seek(long o,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long l){throw new NotSupportedException();}public override void Write(byte[] b,int o,int c){throw new NotSupportedException();}
    }
}
