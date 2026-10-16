"""Extract generated 2x2 keyframe sheets, align feet, and author new streamed 30-FPS motions.

Only build-time Pillow/NumPy/OpenCV dependencies are required. No runtime image cache.
The generated alpha is preserved when cropping and resizing the authored artwork.
"""
import argparse, hashlib, json, struct, sys, zlib
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'work/lumina-build-deps'))
import cv2
cv2.setNumThreads(4)
W,H,FPS=720,1056,30
RELEASE=ROOT if (ROOT/'assets/animations.json').exists() else ROOT/'outputs/Lumina-Desktop'
SOURCE=ROOT/'source' if RELEASE==ROOT else ROOT/'lumina-desktop'
ASSETS=RELEASE/'assets'
QA=RELEASE/'qa-v3'
ART=SOURCE/'art/actions'
GRID=np.dstack(np.meshgrid(np.arange(W,dtype=np.float32),np.arange(H,dtype=np.float32)))
NEW=[
 ('bowing','鞠躬','日常',3,True),('tea','品茶','日常',4,False),
 ('reading','读书','日常',4,False),('writing','羽笔写字','日常',3,False),
 ('stretching','伸懒腰','日常',3,False),('sleeping','小憩','日常',2,False),
 ('laughing','掩嘴轻笑','表情',4,True),('shy','害羞','表情',2,True),
 ('surprised','惊讶','表情',1,True),('heart','比心','表情',3,True),
 ('magic','星光施法','银龙',2,False),('wings','舒展龙翼','银龙',3,False),
]
BASE=[('idle','待机'),('waving','招手'),('jumping','轻跃'),('running','专注思考'),
      ('waiting','等待'),('review','查看成果'),('failed','失落'),
      ('running-left','向左轻跑'),('running-right','向右轻跑')]

def bounds(rgba):
    mask=(rgba[:,:,3]>32).astype(np.uint8)
    n,labels,stats,centers=cv2.connectedComponentsWithStats(mask,8)
    i=1+int(np.argmax(stats[1:,cv2.CC_STAT_AREA]))
    x,y,w,h,area=stats[i]
    assert area>15000, 'Missing character or empty cell'
    return int(x),int(y),int(w),int(h)

def extract(state):
    jobs=json.loads((SOURCE/'art/prompts.json').read_text(encoding='utf8'))['jobs']
    filename=next(j.get('selected_file',state+'.png') for j in jobs if j['state']==state)
    image=Image.open(ART/filename).convert('RGBA');sw,sh=image.size
    cells=[np.array(image.crop((c*sw//2,r*sh//2,(c+1)*sw//2,(r+1)*sh//2))) for r in range(2) for c in range(2)]
    # Select the character component when extracting each sprite sheet cell.
    # Keep soft alpha at its contour; detached matte debris is not a sprite.
    cleaned=[]
    for cell in cells:
        mask=(cell[:,:,3]>32).astype(np.uint8)
        n,labels,stats,centers=cv2.connectedComponentsWithStats(mask,8)
        main=1+int(np.argmax(stats[1:,cv2.CC_STAT_AREA]))
        keep=(labels==main).astype(np.uint8)
        support=cv2.dilate(keep,np.ones((3,3),np.uint8))
        cell=cell.copy();cell[:,:,3]*=support;cleaned.append(cell)
    cells=cleaned;boxes=[bounds(cell) for cell in cells]
    # One scale for the full action; bowed/stretching poses keep their natural height changes.
    scale=min(924/max(b[3] for b in boxes),630/max(b[2] for b in boxes))
    output=ASSETS/'keyframes'/state;output.mkdir(parents=True,exist_ok=True)
    frames=[];report=[]
    for i,(cell,box) in enumerate(zip(cells,boxes)):
        rgba=cell.astype(np.float32)/255;rgba[:,:,:3]*=rgba[:,:,3:4]
        x,y,w,h=box
        matrix=np.float32([[scale,0,360-(x+w/2)*scale],[0,scale,1025-(y+h)*scale]])
        frame=cv2.warpAffine(rgba,matrix,(W,H),flags=cv2.INTER_LANCZOS4,borderMode=cv2.BORDER_CONSTANT).clip(0,1)
        frame[:,:,:3]=np.minimum(frame[:,:,:3],frame[:,:,3:4]);frames.append(frame)
        to_image(frame).save(output/('%02d.png'%i))
        report.append({'source_cell':i,'source_bbox':list(box),'scale':scale,'feet_y':1025})
    return frames,report

def to_image(frame):
    a=frame[:,:,3:4]
    rgb=np.divide(frame[:,:,:3],a,out=np.zeros_like(frame[:,:,:3]),where=a>.0001)
    return Image.fromarray(np.rint(np.concatenate((rgb,a),axis=2).clip(0,1)*255).astype(np.uint8))

def flow(a,b):
    def gray(im):
        rgb=im[:,:,:3]+(1-im[:,:,3:4])*.15
        return cv2.resize(cv2.cvtColor((rgb*255).clip(0,255).astype(np.uint8),cv2.COLOR_RGB2GRAY),(W//2,H//2))
    estimator=cv2.DISOpticalFlow_create(cv2.DISOPTICAL_FLOW_PRESET_MEDIUM);estimator.setFinestScale(0)
    return cv2.resize(estimator.calc(gray(a),gray(b),None),(W,H))*2

def warp(im,f,t):
    inverse=GRID-f*t
    corrected=cv2.remap(f,inverse[:,:,0],inverse[:,:,1],cv2.INTER_LINEAR,borderMode=cv2.BORDER_REPLICATE)
    inverse=GRID-corrected*t
    return cv2.remap(im,inverse[:,:,0],inverse[:,:,1],cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT)

def interpolate(a,b,forward,backward,t):
    wa=warp(a,forward,t);wb=warp(b,backward,1-t)
    # Keep an occluded hand/head silhouette on one temporal anchor instead of
    # cross-dissolving two disagreeing silhouettes into a transparent double image.
    color_error=np.max(np.abs(wa[:,:,:3]-wb[:,:,:3]),axis=2)
    alpha_error=np.abs(wa[:,:,3]-wb[:,:,3])
    confidence=np.exp(-(color_error/.10)**2-(alpha_error/.15)**2)
    confidence=cv2.GaussianBlur(confidence,(5,5),.8)
    weight=(1-t)*confidence+(1-confidence)*(1 if t<.5 else 0)
    return wa*weight[:,:,None]+wb*(1-weight[:,:,None])

def encode(state,keys):
    steps=18;count=len(keys)*steps;path=ASSETS/'motion'/(state+'.luma')
    records=[];hashes=[];preview=[]
    with path.open('wb') as f:
        f.write(b'LUMAFR2\0'+struct.pack('<4I',W,H,FPS,count));f.write(bytes(count*16))
        for i,a in enumerate(keys):
            b=keys[(i+1)%len(keys)];forward=flow(a,b);backward=flow(b,a)
            for step in range(steps):
                t=step/steps;t=t*t*(3-2*t)
                frame=a if step==0 else interpolate(a,b,forward,backward,t)
                rgba=np.rint(frame.clip(0,1)*255).astype(np.uint8)
                rgba[:,:,:3]=np.minimum(rgba[:,:,:3],rgba[:,:,3:4])
                raw=np.ascontiguousarray(rgba[:,:,[2,1,0,3]]).tobytes();hashes.append(hashlib.sha256(raw).hexdigest())
                encoder=zlib.compressobj(5,zlib.DEFLATED,-15);data=encoder.compress(raw)+encoder.flush()
                records.append((f.tell(),len(data),zlib.crc32(raw)));f.write(data)
                preview.append(to_image(frame).resize((270,396),Image.Resampling.LANCZOS))
        f.seek(24)
        for record in records:f.write(struct.pack('<QII',*record))
    with path.open('rb') as f:
        for start,size,crc in records:
            f.seek(start);raw=zlib.decompress(f.read(size),-15)
            assert len(raw)==W*H*4 and zlib.crc32(raw)==crc
    assert len(set(hashes))==count, 'Repeated frame unexpectedly used as interpolation'
    # Opaque previews are QA derivatives; shipped animation retains original transparent alpha.
    display=[]
    for im in preview:
        bg=Image.new('RGBA',im.size,(37,33,46,255));bg.alpha_composite(im);display.append(bg.convert('RGB'))
    display[0].save(QA/(state+'.gif'),save_all=True,append_images=display[1:],duration=1000/FPS,loop=0,optimize=False)
    to_image(keys[2]).save(QA/(state+'-pose.png'))
    return {'path':'motion/'+path.name,'width':W,'height':H,'fps':FPS,'frames':count,'unique_frames':len(set(hashes)),
            'keyframes':len(keys),'duration_seconds':count/FPS,'bytes':path.stat().st_size,'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),
            'authored_action':True,'method':'generated distinct key poses + occlusion-aware bidirectional DIS optical flow'}

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--states',default='all');args=parser.parse_args()
    requested={s[0] for s in NEW} if args.states=='all' else set(args.states.split(','))
    QA.mkdir(exist_ok=True)
    target=ASSETS/'animations.json';result=json.loads(target.read_text(encoding='utf8'))
    extraction=json.loads((QA/'extraction.json').read_text(encoding='utf8')) if (QA/'extraction.json').exists() else {}
    def author(state):
        keys,report=extract(state);return state,encode(state,keys),report
    # Build two different files concurrently; only the main thread updates the manifest.
    with ThreadPoolExecutor(max_workers=2) as pool:
        pending=[pool.submit(author,state) for state,label,category,weight,click in NEW if state in requested]
        for future in as_completed(pending):
            state,metadata,report=future.result();extraction[state]=report;result['states'][state]=metadata
            target.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf8')
            print(json.dumps({'state':state,**metadata}),flush=True)
    all_ready=all(s[0] in result['states'] for s in NEW)
    if all_ready:
        result['actions']=[{'state':s,'label':l,'category':'基础','ambient_weight':1 if s in ('waving','jumping') else 0,'click':s in ('waving','jumping')} for s,l in BASE]
        result['actions'] += [{'state':s,'label':l,'category':c,'ambient_weight':w,'click':click} for s,l,c,w,click in NEW]
        result['version']=3;target.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf8')
    (QA/'motion-pack-validation.json').write_text(json.dumps({'ok':all_ready,'fps':FPS,'action_count':len(result['actions']),
        'total_frames':sum(s['frames'] for s in result['states'].values()),'states':result['states']},indent=2),encoding='utf8')
    (QA/'extraction.json').write_text(json.dumps(extraction,indent=2),encoding='utf8')

if __name__=='__main__':main()
