"""Author 30 FPS transparent motion packs. OpenCV is used only during this build."""
import argparse, hashlib, json, struct, sys, zlib
from pathlib import Path
import numpy as np
from PIL import Image

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'work/lumina-build-deps'))
import cv2
cv2.setNumThreads(4)
W,H,FPS=720,1056,30
ASSETS=ROOT/'outputs/Lumina-Desktop/assets'
OUT=ASSETS/'motion'
QA=ROOT/'outputs/Lumina-Desktop/qa-v2'
OUT.mkdir(exist_ok=True);QA.mkdir(exist_ok=True)
DELAYS={'idle':.36,'running-right':.27,'running-left':.27,'waving':.23,'jumping':.19,'failed':.27,'waiting':.27,'running':.27,'review':.27}
manifest=json.loads((ASSETS/'manifest.json').read_text(encoding='utf8'))
grid=np.dstack(np.meshgrid(np.arange(W,dtype=np.float32),np.arange(H,dtype=np.float32)))

def keyframe(path):
    rgba=np.asarray(Image.open(ASSETS/path).convert('RGBA'),dtype=np.float32)/255
    rgba[:,:,:3]*=rgba[:,:,3:4]
    return cv2.resize(rgba,(W,H),interpolation=cv2.INTER_LANCZOS4).clip(0,1)

def flow(a,b):
    # Estimate motion on a half-size luma/alpha composite; warp all four channels together.
    def gray(im):
        rgb=im[:,:,:3]+(1-im[:,:,3:4])*.15
        return cv2.resize(cv2.cvtColor((rgb*255).clip(0,255).astype(np.uint8),cv2.COLOR_RGB2GRAY),(W//2,H//2))
    estimator=cv2.DISOpticalFlow_create(cv2.DISOPTICAL_FLOW_PRESET_MEDIUM)
    estimator.setFinestScale(0)
    f=estimator.calc(gray(a),gray(b),None)
    return cv2.resize(f,(W,H),interpolation=cv2.INTER_LINEAR)*2

def warp(im,f,t):
    inverse=grid-f*t
    corrected=cv2.remap(f,inverse[:,:,0],inverse[:,:,1],cv2.INTER_LINEAR,borderMode=cv2.BORDER_REPLICATE)
    inverse=grid-corrected*t
    return cv2.remap(im,inverse[:,:,0],inverse[:,:,1],cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT)

def raw_pixels(frame):
    # Premultiplied BGRA can be copied directly into the reusable WPF surface.
    rgba=np.rint(frame.clip(0,1)*255).astype(np.uint8)
    rgba[:,:,:3]=np.minimum(rgba[:,:,:3],rgba[:,:,3:4])
    return np.ascontiguousarray(rgba[:,:,[2,1,0,3]]).tobytes()

def write_pack(state,keys):
    animated=state in DELAYS
    steps=max(2,round(DELAYS.get(state,1/FPS)*FPS)) if animated else 1
    count=len(keys)*steps
    output=OUT/(state+'.luma')
    records=[];hashes=[];sample=[]
    with output.open('wb') as f:
        f.write(b'LUMAFR2\0'+struct.pack('<4I',W,H,FPS,count))
        f.write(bytes(count*16))
        for i,a in enumerate(keys):
            b=keys[(i+1)%len(keys)]
            if animated:
                forward=flow(a,b);backward=flow(b,a)
            for step in range(steps):
                t=step/steps
                if step==0: frame=a
                else:
                    t=t*t*(3-2*t)
                    frame=warp(a,forward,t)*(1-t)+warp(b,backward,1-t)*t
                raw=raw_pixels(frame);digest=hashlib.sha256(raw).hexdigest();hashes.append(digest)
                encoder=zlib.compressobj(5,zlib.DEFLATED,-15)
                data=encoder.compress(raw)+encoder.flush()
                records.append((f.tell(),len(data),zlib.crc32(raw)));f.write(data)
                if i==0 and step in (0,steps//2,steps-1):
                    alpha=frame[:,:,3:4];rgb=np.divide(frame[:,:,:3],alpha,out=np.zeros_like(frame[:,:,:3]),where=alpha>.001)
                    image=Image.fromarray(np.rint(np.concatenate((rgb,alpha),axis=2).clip(0,1)*255).astype(np.uint8))
                    image.save(QA/(state+'-%02d.png'%step))
                    sample.append(image.resize((360,528)))
        f.seek(24)
        for record in records:f.write(struct.pack('<QII',*record))
    # Structural decode and CRC verification against the exact encoded pack.
    with output.open('rb') as f:
        for start,size,crc in records:
            f.seek(start);raw=zlib.decompress(f.read(size),-15)
            assert len(raw)==W*H*4 and zlib.crc32(raw)==crc
    if sample and state in ('idle','waving','jumping','running'):
        # This preview samples the first transition; the pack holds the full 30 FPS loop.
        preview=Image.new('RGBA',(360*len(sample),528),(36,32,45,255))
        for i,image in enumerate(sample):preview.alpha_composite(image,(i*360,0))
        preview.save(QA/(state+'-transition.png'))
    return {'path':'motion/'+output.name,'width':W,'height':H,'fps':FPS,'frames':count,'unique_frames':len(set(hashes)),'keyframes':len(keys),'duration_seconds':count/FPS,'bytes':output.stat().st_size,'sha256':hashlib.sha256(output.read_bytes()).hexdigest()}

parser=argparse.ArgumentParser();parser.add_argument('--states',default='all');args=parser.parse_args()
requested=list(manifest['states']) if args.states=='all' else args.states.split(',')
target=ASSETS/'animations.json'
result=json.loads(target.read_text(encoding='utf8')) if target.exists() else {'version':2,'fps':FPS,'canvas_width':W,'canvas_height':H,'states':{},'actions':manifest['actions']}
for state in requested:
    keys=[keyframe(p) for p in manifest['states'][state]]
    result['states'][state]=write_pack(state,keys)
    target.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf8')
    print(json.dumps({'state':state,**result['states'][state]}),flush=True)
(QA/'motion-pack-validation.json').write_text(json.dumps({'ok':True,'fps':FPS,'method':'bidirectional DIS optical-flow interpolation, premultiplied alpha','opencv_build_only':cv2.__version__,'states':result['states']},indent=2),encoding='utf8')
