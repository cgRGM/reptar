"""Offline published-agent smoke: native tools, RPC persistence/restart, and HTTP streaming.

Usage: python3 scripts/test-agent-native.py /absolute/path/to/published/app
No provider credentials are used; fixtures and logs live in a disposable temp directory.
"""
import json, os, pathlib, queue, subprocess, sys, tempfile, threading, time, urllib.request, urllib.error, socket
binary=pathlib.Path(sys.argv[1]).resolve()
if not binary.is_file():
    raise SystemExit(f'Published agent executable not found: {binary}')
root=pathlib.Path(tempfile.mkdtemp(prefix='reptar-agent-native-logs-'))
workspace=pathlib.Path(tempfile.mkdtemp(prefix='reptar-agent-verify-'))
(workspace/'README.md').write_text('Disposable offline agent verification workspace.\n')
env={k:v for k,v in os.environ.items() if 'API_KEY' not in k and not k.startswith(('REPTAR_AGENT_', 'ANTHROPIC_', 'OPENAI_', 'GEMINI_', 'OLLAMA_', 'GOOGLE_'))}
env['HOST']='127.0.0.1'
base=[str(binary),'--workspace',str(workspace),'--db',str(workspace/'agent.db'),'--provider','echo']
subprocess.run(base+['--self-test'],check=True,env=env,stdout=(root/'native-selftest.log').open('w'),stderr=subprocess.STDOUT,timeout=40)

def start_rpc(session=None):
    args=base+['--mode','rpc']
    if session: args+=['--session',session]
    p=subprocess.Popen(args,env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=(root/'native-rpc-stderr.log').open('a'),text=True,bufsize=1)
    q=queue.Queue()
    def read():
        for line in p.stdout:
            try: q.put(json.loads(line))
            except Exception as e: q.put(e)
    threading.Thread(target=read,daemon=True).start()
    return p,q

def call(p,q,cmd,**kwargs):
    tag=str(time.time_ns());p.stdin.write(json.dumps({'type':cmd,'id':tag,**kwargs})+'\n');p.stdin.flush()
    deadline=time.monotonic()+30
    while time.monotonic()<deadline:
        obj=q.get(timeout=max(.1,deadline-time.monotonic()))
        if isinstance(obj,Exception): raise obj
        if obj.get('type')=='response' and obj.get('id')==tag:return obj
    raise TimeoutError(cmd)

def stop(p):
    p.stdin.close()
    try: assert p.wait(timeout=30)==0
    except Exception: p.kill();p.wait();raise

p,q=start_rpc()
try:
    prompted=call(p,q,'prompt',message='hello offline'); assert prompted['success'],prompted
    for _ in range(100):
        entries=call(p,q,'get_entries')
        if entries['success'] and len(entries['data']['entries'])>=2:break
        time.sleep(.1)
    assert len(entries['data']['entries'])==2,entries
    state=call(p,q,'get_state'); session=state['data']['sessionId']
    assert session, state
    cursor=entries['data']['entries'][-1]['id'];first=entries['data']['entries'][0]['id']
    assert call(p,q,'get_entries',since=cursor)['data']['entries']==[]
    assert not call(p,q,'get_entries',since='missing-cursor')['success']
    tree=call(p,q,'get_tree');assert tree['success'] and tree['data']['leafId']==cursor
    assert not call(p,q,'switch_leaf',entryId='missing-entry')['success']
finally:stop(p)
p,q=start_rpc(session)
try:
    restored=call(p,q,'get_state'); assert restored['data']['sessionId']==session,restored
    resumed=call(p,q,'get_entries',since=cursor); assert resumed.get('success'),resumed
    assert resumed['data']['entries']==[]
    fork=call(p,q,'fork'); assert fork['success'] and fork['data']['messageCount']==2,fork
    forkSession=fork['data']['sessionId']
    branch=call(p,q,'get_entries')['data'];assert len(branch['entries'])==2
    firstFork=branch['entries'][0]['id'];leafFork=branch['leafId']
    switched=call(p,q,'switch_leaf',entryId=firstFork);assert switched['success'] and switched['data']['messageCount']==1,switched
    allEntries=call(p,q,'get_entries')['data'];assert len(allEntries['entries'])==2 and allEntries['leafId']==firstFork
    assert call(p,q,'switch_leaf',entryId=leafFork)['data']['messageCount']==2
finally:stop(p)
p,q=start_rpc(forkSession)
try:
    prompted=call(p,q,'prompt',message='second turn after restart'); assert prompted['success'],prompted
    for _ in range(100):
        delta=call(p,q,'get_entries',since=leafFork)
        assert delta['success'],delta
        if len(delta['data']['entries'])>=2:break
        time.sleep(.1)
    assert [e['role'] for e in delta['data']['entries']]==['user','assistant'],delta
    assert len(call(p,q,'get_entries')['data']['entries'])==4
finally:stop(p)

with socket.socket() as sock:
    sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
p=subprocess.Popen(base+['--web',str(port)],env=env,stdout=(root/'native-web.log').open('w'),stderr=subprocess.STDOUT)
url=f'http://127.0.0.1:{port}'
def request(path,data=None):
    req=urllib.request.Request(url+path,data=None if data is None else json.dumps(data).encode(),headers={'Content-Type':'application/json'})
    try:
        with urllib.request.urlopen(req,timeout=10) as r:return r.status,r.read().decode()
    except urllib.error.HTTPError as e:return e.code,e.read().decode()
try:
    for _ in range(150):
        if p.poll() is not None:raise RuntimeError('web server exited')
        try:
            if request('/api/health')[0]==200:break
        except OSError:pass
        time.sleep(.1)
    assert request('/api/health')[0]==200
    assert json.loads(request('/api/chat/status/nonexistent')[1])['running'] is False
    assert request('/api/chat/steer',{'sessionId':'nonexistent','message':'hello'})[0]==409
    assert request('/api/chat/abort',{'sessionId':'nonexistent'})[0]==409
    status,stream=request('/api/chat',{'provider':'echo','message':'hello offline web'})
    assert status==200 and '"type":"done"' in stream,(status,stream)
finally:
    p.terminate()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill();p.wait()
print('NATIVE AGENT VERIFIED: self-test; RPC prompt/tree/fork/leaf/invalid ids; cursor across restart; web health/status/steer/abort/stream. Logs:',root,'Workspace:',workspace)
