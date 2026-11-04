"""Opt-in real native-host -> WPF -> yt-dlp smoke test; never touches a browser profile.
Run after build-browser.ps1. Windows launches an isolated, minimized Flow instance.
"""
from pathlib import Path
import json, os, queue, signal, struct, subprocess, threading, time, uuid

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / '.review-discovery' / ('live-' + uuid.uuid4().hex[:8])
OUT.mkdir(parents=True)
ENV = dict(os.environ, FLOW_DATA_DIR=str(OUT/'data'), FLOW_LEGACY_DIR=str(OUT/'legacy'), FLOW_DOWNLOAD_DIR=str(OUT/'downloads'))
(OUT/'data').mkdir()
(OUT/'data'/'flow.settings.json').write_text(json.dumps({'Language':'en','Theme':'light','Notify':False,'WindowsTask':False,'CloseToTray':False,'MinimizeToTray':False,'AutoPreview':False}), encoding='utf-8')
HOST = ROOT/'Flow.BrowserHost.exe'
ORIGIN = 'chrome-extension://eidibhbenooohggnhdeblgfdclahhdnd/'
native = None
app_pid = None
checks = []

def note(label):
    checks.append(label)
    print('PASS ' + label, flush=True)

def start_host():
    return subprocess.Popen([str(HOST), ORIGIN], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=ENV, creationflags=subprocess.CREATE_NO_WINDOW)

def read_exact(stream, n):
    data = b''
    while len(data)<n:
        part = stream.read(n-len(data))
        if not part: raise EOFError('Native host closed its output')
        data += part
    return data

def request(action, **fields):
    payload=json.dumps(dict(v=1, action=action, **fields), ensure_ascii=False).encode('utf-8')
    native.stdin.write(struct.pack('<I',len(payload))+payload); native.stdin.flush()
    responses=queue.Queue()
    def read():
        try:
            n=struct.unpack('<I',read_exact(native.stdout,4))[0]
            if n>524288: raise ValueError('oversized native response')
            responses.put(json.loads(read_exact(native.stdout,n)))
        except Exception as error: responses.put(error)
    threading.Thread(target=read,daemon=True).start()
    result=responses.get(timeout=205)
    if isinstance(result,Exception): raise result
    return result

try:
    rejected=subprocess.run([str(HOST),'https://example.com/'],capture_output=True,env=ENV,timeout=10,creationflags=subprocess.CREATE_NO_WINDOW)
    assert rejected.returncode==2 and rejected.stdout==b''
    note('untrusted native-host origin rejected')
    native=start_host()
    assert request('ping')['error']['code']=='APP_UNAVAILABLE'
    note('passive polling does not launch Flow')
    handshake=request('ping',launch=True)
    assert handshake['ok'],handshake
    app_pid=handshake['data']['pid']
    assert handshake['data']['version']=='2.2' and handshake['data']['folder']==str(OUT/'downloads')
    assert 'image-original' in handshake['data']['capabilities']
    note('native host starts the isolated Flow 2.2 desktop instance with image capability')
    assert request('inspect',url='https://127.0.0.1/media')['error']['code']=='UrlNotPublic'
    note('private target refused across the actual process boundary')
    result=request('inspect',url='https://www.youtube.com/watch?v=jNQXAC9IVRw')
    if not result['ok']:
        print('LIVE SITE BLOCKED: '+json.dumps(result,ensure_ascii=False),flush=True)
        raise RuntimeError('Live site inspection did not complete')
    media=result['data']
    assert media['title'] and media['choices']
    choice=next(c for c in media['choices'] if c['format']=='mp4' and 0<c['height']<=360)
    note('real media metadata and bounded compatible MP4 choice returned')
    job_request=dict(token=media['token'],choice=choice['key'],hold=True,requestId=str(uuid.uuid4()))
    added=request('enqueue',**job_request)
    assert added['ok'],added
    job_id=added['data']['jobId']
    note('held download acknowledged only after it is stored on disk')
    native.stdin.close();native.wait(timeout=10)
    native=start_host()
    jobs=request('queue')['data']['items']
    assert next(i for i in jobs if i['id']==job_id)['state']=='paused'
    assert request('enqueue',**job_request)['data']['duplicate']
    note('desktop queue survives native-host exit and retries do not duplicate jobs')
    assert request('resume',jobId=job_id)['ok']
    until=time.monotonic()+210
    while time.monotonic()<until:
        current=next(i for i in request('queue')['data']['items'] if i['id']==job_id)
        if current['state'] in ('completed','failed','needs-action','canceled'):break
        time.sleep(2)
    assert current['state']=='completed',current
    note('real download completes through the existing Flow executor')
    files=list((OUT/'downloads').rglob('*.mp4'))
    assert len(files)==1
    probe=subprocess.run([str(ROOT/'tools/ffprobe.exe'),'-v','error','-show_streams','-show_format','-of','json',str(files[0])],capture_output=True,timeout=15,creationflags=subprocess.CREATE_NO_WINDOW)
    info=json.loads(probe.stdout)
    assert any(s['codec_name']=='h264' for s in info['streams']) and any(s['codec_name']=='aac' for s in info['streams'])
    assert float(info['format']['duration'])>17
    note('ffprobe verifies H.264 + AAC and the complete duration')
    assets=[
        ('image','https://mdn.github.io/shared-assets/images/examples/grapefruit-slice.jpg','image-original'),
        ('video','https://interactive-examples.mdn.mozilla.net/media/cc0-videos/flower.mp4','direct-video'),
    ]
    for kind,url,label in assets:
        result=request('inspect',kind=kind,url=url,title=label,referrer='https://developer.mozilla.org/en-US/docs/Web/HTML?test=private')
        assert result['ok'],result
        media=result['data']
        choice=next(c for c in media['choices'] if c['format']==('original' if kind=='image' else 'mp4'))
        note(label+': real metadata and download choice returned')
        added=request('enqueue',token=media['token'],choice=choice['key'],hold=False,requestId=str(uuid.uuid4()))
        assert added['ok'],added
        job_id=added['data']['jobId'];until=time.monotonic()+120
        while time.monotonic()<until:
            current=next(i for i in request('queue')['data']['items'] if i['id']==job_id)
            if current['state'] in ('completed','failed','needs-action','canceled'):break
            time.sleep(1)
        assert current['state']=='completed',current
        note(label+': download and content verification complete in the shared queue')
    images=list((OUT/'downloads').rglob('*.jpg'))
    assert len(images)==1 and images[0].read_bytes().startswith(b'\xff\xd8\xff')
    note('original image is saved as JPEG without conversion to video')
finally:
    if native and native.poll() is None:
        try:native.stdin.close();native.wait(timeout=5)
        except Exception:native.kill()
    if app_pid:
        # Only the PID returned by the bridge for the fresh isolated data directory.
        try:os.kill(app_pid,signal.SIGTERM)
        except OSError:pass
    (OUT/'report.json').write_text(json.dumps({'checks':checks,'count':len(checks)},ensure_ascii=False,indent=2),encoding='utf-8')
    print('REPORT '+str(OUT/'report.json'),flush=True)
