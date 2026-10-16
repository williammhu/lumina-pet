"""Convert approved streamed frames to a fixed-height 1280x1536 master canvas.

Technical resampling of existing artwork; no redesigned poses or invented detail.
Windows XPRESS frames keep full-resolution playback inexpensive.
"""
import argparse,ctypes,hashlib,json,struct,sys,time,zlib
from concurrent.futures import ThreadPoolExecutor,as_completed
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'work/lumina-build-deps'))
import cv2
cv2.setNumThreads(2)
RELEASE=ROOT if (ROOT/'assets/animations.json').exists() else ROOT/'outputs/Lumina-Desktop'
ASSETS=RELEASE/'assets';QA=RELEASE/'qa-v4'
W,H=1280,1536
cab=ctypes.WinDLL('cabinet',use_last_error=True)
HANDLE=ctypes.c_void_p;SIZE=ctypes.c_size_t
for name in ('CreateCompressor','CreateDecompressor'):
    fn=getattr(cab,name);fn.argtypes=[ctypes.c_uint,HANDLE,ctypes.POINTER(HANDLE)];fn.restype=ctypes.c_int
for name in ('Compress','Decompress'):
    fn=getattr(cab,name);fn.argtypes=[HANDLE,HANDLE,SIZE,HANDLE,SIZE,ctypes.POINTER(SIZE)];fn.restype=ctypes.c_int
for name in ('CloseCompressor','CloseDecompressor'):
    fn=getattr(cab,name);fn.argtypes=[HANDLE];fn.restype=ctypes.c_int

class Codec:
    def __init__(self,algorithm=3):
        self.encoder=HANDLE();self.decoder=HANDLE();self.capacity=W*H*4+65536
        assert cab.CreateCompressor(algorithm,None,ctypes.byref(self.encoder)),ctypes.get_last_error()
        assert cab.CreateDecompressor(algorithm,None,ctypes.byref(self.decoder)),ctypes.get_last_error()
        self.compressed=ctypes.create_string_buffer(self.capacity);self.decoded=ctypes.create_string_buffer(W*H*4)
    def compress(self,raw):
        written=SIZE();source=ctypes.c_char_p(raw)
        if not cab.Compress(self.encoder,source,len(raw),self.compressed,self.capacity,ctypes.byref(written)):
            raise ctypes.WinError(ctypes.get_last_error())
        return self.compressed.raw[:written.value]
    def decompress(self,data):
        written=SIZE()
        if not cab.Decompress(self.decoder,ctypes.c_char_p(data),len(data),self.decoded,W*H*4,ctypes.byref(written)):
            raise ctypes.WinError(ctypes.get_last_error())
        assert written.value==W*H*4
        return self.decoded.raw
    def close(self):
        cab.CloseCompressor(self.encoder);cab.CloseDecompressor(self.decoder)

def source_frames(path):
    with path.open('rb') as f:
        assert f.read(8)==b'LUMAFR2\0'
        w,h,fps,count=struct.unpack('<4I',f.read(16))
        records=[struct.unpack('<QII',f.read(16)) for _ in range(count)]
        for offset,size,crc in records:
            f.seek(offset);raw=zlib.decompress(f.read(size),-15)
            assert len(raw)==w*h*4 and zlib.crc32(raw)==crc
            yield np.frombuffer(raw,np.uint8).reshape(h,w,4),fps,count

def converted(frame,normalization,lock_height=False):
    oh,ow=frame.shape[:2];scale=H/oh*normalization
    # Stable feet and body scale. Wide poses use transparent canvas room, never width-fit.
    old_feet=1025/1056*oh;feet=round(1025/1056*H)
    if lock_height:
        x,y,bw,bh=rect(frame);scale=1344/bh
        matrix=np.float32([[scale,0,W/2-(x+bw/2)*scale],[0,scale,feet-(y+bh)*scale]])
    else:matrix=np.float32([[scale,0,W/2-ow/2*scale],[0,scale,feet-old_feet*scale]])
    out=cv2.warpAffine(frame,matrix,(W,H),flags=cv2.INTER_LANCZOS4,borderMode=cv2.BORDER_CONSTANT)
    out[:,:,:3]=np.minimum(out[:,:,:3],out[:,:,3:4])
    return out

def rect(frame):
    points=cv2.findNonZero((frame[:,:,3]>32).astype(np.uint8));return list(cv2.boundingRect(points))

def image(frame):
    rgba=frame[:,:,[2,1,0,3]].copy();a=rgba[:,:,3:4].astype(np.float32)/255
    rgba[:,:,:3]=np.rint(np.divide(rgba[:,:,:3],a,out=np.zeros_like(rgba[:,:,:3],np.float32),where=a>.0001)).clip(0,255).astype(np.uint8)
    return Image.fromarray(rgba)

def author(state,metadata,extraction):
    normalization=1.
    old_height=924.
    if state in extraction:
        rows=extraction[state];old_height=max(r['source_bbox'][3] for r in rows)*rows[0]['scale']
        normalization=924/old_height
    output=ASSETS/'motion-1k'/(state+'.luma');codec=Codec()
    records=[];hashes=[];key_bounds=[];max_compressed=0
    try:
        with output.open('wb') as f:
            count=metadata['frames'];fps=metadata['fps']
            f.write(b'LUMAFR3\0'+struct.pack('<4I',W,H,fps,count));f.write(bytes(count*16))
            for i,(source,_,_) in enumerate(source_frames(ASSETS/metadata['path'])):
                frame=converted(source,normalization,state in ('magic','wings'))
                raw=frame.tobytes();data=codec.compress(raw)
                assert codec.decompress(data)==raw
                if state in ('magic','wings'):
                    assert not np.any(frame[:6,:,3]>32) and not np.any(frame[-6:,:,3]>32),(state,i,'clipped vertical edge')
                    assert not np.any(frame[:,:6,3]>32) and not np.any(frame[:,-6:,3]>32),(state,i,'clipped horizontal edge')
                records.append((f.tell(),len(data),zlib.crc32(raw)));f.write(data)
                hashes.append(hashlib.sha256(raw).hexdigest());max_compressed=max(max_compressed,len(data))
                if i==0 or state in ('magic','wings') and i in (18,36,54):
                    key_bounds.append({'frame':i,'bbox':rect(frame)})
                    image(frame).save(QA/(state+'-%03d.png'%i))
            assert len(records)==count and len(set(hashes))==metadata['unique_frames']
            f.seek(24)
            for record in records:f.write(struct.pack('<QII',*record))
    finally:codec.close()
    return state,{**metadata,'path':'motion-1k/'+output.name,'width':W,'height':H,'bytes':output.stat().st_size,
        'sha256':hashlib.sha256(output.read_bytes()).hexdigest(),'codec':'windows-xpress',
        'max_compressed_bytes':max_compressed,'source_resolution':[720,1056],'technical_resampling':True,'height_locked':state in ('magic','wings')},\
        {'old_max_character_height':old_height,'normalization':normalization,'new_reference_character_height':1344,
         'feet_y':round(1025/1056*H),'per_frame_height_lock':state in ('magic','wings'),'keyframe_bounds':key_bounds}

def recover(state,metadata,extraction):
    """Recover only a fully committed indexed pack from this immutable v3 conversion."""
    output=ASSETS/'motion-1k'/(state+'.luma')
    with output.open('rb') as f:
        if f.read(8)!=b'LUMAFR3\0':raise ValueError('incomplete header')
        if struct.unpack('<4I',f.read(16))!=(W,H,metadata['fps'],metadata['frames']):raise ValueError('wrong dimensions')
        records=[struct.unpack('<QII',f.read(16)) for _ in range(metadata['frames'])]
        end=24+16*len(records)
        for offset,size,crc in records:
            if offset!=end or size<1:raise ValueError('uncommitted index')
            end+=size
        if end!=output.stat().st_size:raise ValueError('incomplete data')
        codec=Codec();key_bounds=[]
        try:
            for i in ([0,18,36,54] if state in ('magic','wings') else [0]):
                offset,size,crc=records[i];f.seek(offset);raw=codec.decompress(f.read(size))
                if zlib.crc32(raw)!=crc:raise ValueError('CRC')
                frame=np.frombuffer(raw,np.uint8).reshape(H,W,4)
                key_bounds.append({'frame':i,'bbox':rect(frame)})
        finally:codec.close()
    old_height=max(r['source_bbox'][3] for r in extraction[state])*extraction[state][0]['scale'] if state in extraction else 924.
    return state,{**metadata,'path':'motion-1k/'+output.name,'width':W,'height':H,'bytes':output.stat().st_size,
        'sha256':hashlib.sha256(output.read_bytes()).hexdigest(),'codec':'windows-xpress',
        'max_compressed_bytes':max(r[1] for r in records),'source_resolution':[720,1056],'technical_resampling':True},\
        {'old_max_character_height':old_height,'normalization':924/old_height,'new_reference_character_height':1344,
         'feet_y':round(1025/1056*H),'keyframe_bounds':key_bounds}

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--probe',action='store_true');parser.add_argument('--states',default='all');parser.add_argument('--resume',action='store_true');args=parser.parse_args()
    manifest=json.loads((ROOT/'work/lumina-v4/animations-v3.json').read_text(encoding='utf8')) if (ROOT/'work/lumina-v4/animations-v3.json').exists() else json.loads((ASSETS/'animations-v3.json').read_text(encoding='utf8'))
    extraction=json.loads((RELEASE/'qa-v3/extraction.json').read_text(encoding='utf8'))
    QA.mkdir(exist_ok=True);(ASSETS/'motion-1k').mkdir(exist_ok=True)
    if args.probe:
        frame,_,_=next(source_frames(ASSETS/manifest['states']['idle']['path']));raw=converted(frame,1).tobytes()
        for algorithm in (3,4):
            codec=Codec(algorithm);start=time.perf_counter();data=codec.compress(raw);encode_ms=(time.perf_counter()-start)*1000
            start=time.perf_counter()
            for i in range(30):decoded=codec.decompress(data)
            ms=(time.perf_counter()-start)*1000/30;assert decoded==raw;codec.close()
            print(json.dumps({'algorithm':algorithm,'raw_bytes':len(raw),'compressed_bytes':len(data),'encode_ms':round(encode_ms,2),'decode_ms':round(ms,2)}),flush=True)
        return
    requested=list(manifest['states']) if args.states=='all' else args.states.split(',')
    target=ASSETS/'animations-1k.json'
    result=json.loads(target.read_text(encoding='utf8')) if target.exists() else {**manifest,'version':4,'canvas_width':W,'canvas_height':H,'states':{}}
    reports=json.loads((QA/'scale-normalization.json').read_text(encoding='utf8')) if (QA/'scale-normalization.json').exists() else {}
    def job(state):
        if args.resume:
            try:return recover(state,manifest['states'][state],extraction)
            except (OSError,ValueError,AssertionError,struct.error):pass
        return author(state,manifest['states'][state],extraction)
    errors=[]
    with ThreadPoolExecutor(max_workers=2) as pool:
        pending=[pool.submit(job,s) for s in requested]
        for future in as_completed(pending):
            try:state,metadata,report=future.result()
            except Exception as error:errors.append(str(error));continue
            result['states'][state]=metadata;reports[state]=report
            target.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf8')
            (QA/'scale-normalization.json').write_text(json.dumps(reports,indent=2),encoding='utf8')
            print(json.dumps({'state':state,'bytes':metadata['bytes'],'height_before':round(report['old_max_character_height']),
                'height_after':1344,'resolution':[W,H]}),flush=True)
    complete=len(result['states'])==23 and not errors
    (QA/'motion-pack-validation.json').write_text(json.dumps({'ok':complete,'fps':30,'action_count':21,'total_frames':sum(s['frames'] for s in result['states'].values()),'states':result['states']},indent=2),encoding='utf8')
    if errors:raise RuntimeError(errors)

if __name__=='__main__':main()
