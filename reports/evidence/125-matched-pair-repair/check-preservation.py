import pathlib,json,hashlib
r=pathlib.Path(__file__).resolve().parent
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
def manifest(p):
 s=p/'.agentlang/store'; c=json.loads((s/'CURRENT').read_text(encoding='utf-8')); return json.loads((s/'manifests'/(c['manifestHash']+'.json')).read_text(encoding='utf-8'))
def current_revisions(m):
 return {w['currentName']:next(v for v in m['revisions'] if v['wordId']==w['wordId'] and v['revision']==w['currentRevision']) for w in m['words']}
rows=[]
for trace in sorted((r/'logs').glob('*.trace.jsonl')):
 frames=[json.loads(l) for l in trace.read_text(encoding='utf-8').splitlines() if l.strip()]
 if not any(x.get('event')=='session-end' for x in frames):continue
 name=trace.name.removesuffix('.trace.jsonl'); arm=name.rsplit('-',1)[0]; p=r/'actors'/name;s=r/'starts'/arm
 if arm=='conventional':
  before=(s/'Operations.fs').read_text(encoding='utf-8'); after=(p/'Operations.fs').read_text(encoding='utf-8'); target='    /// Processes both invoices'
  t0=(s/'SelfTests.fs').read_text(encoding='utf-8');t1=(p/'SelfTests.fs').read_text(encoding='utf-8'); test_boundary='    let private makePair '
  rows.append({'actor':name,'domainUnchanged':sha(s/'Domain.fs')==sha(p/'Domain.fs'),'projectUnchanged':sha(s/'StatefulPilot.fsproj')==sha(p/'StatefulPilot.fsproj'),'inheritedHelperUnchanged':before.split(target)[0]==after.split(target)[0],'inheritedTestsUnchanged':t0.split(test_boundary)[0]==t1.split(test_boundary)[0],'operationsSource':after,'targetSource':after.split(target)[1] if target in after else after})
 else:
  a=manifest(s);b=manifest(p);ar=current_revisions(a);br=current_revisions(b);target='invoice.queue-reminders-for-pair'; tr=br[target]
  body=(p/'.agentlang/store/objects'/(tr['definition']['hash']+'.agent')).read_text(encoding='utf-8')
  desc=[]
  for f in frames:
   if f.get('event')!='exchange' or f.get('operation')!='describe':continue
   q=json.loads(f['request']['canonical']);resp=json.loads(f['response']['canonical'])
   if q.get('word')==target and resp.get('ok'):desc.append(resp['data'])
  bindings=[x for x in tr['callBindings'] if x.get('bodyRole')=='definition']
  rows.append({'actor':name,'typesUnchanged':a['types']==b['types'],'inheritedWordsUnchanged':{k:ar[k]==br.get(k) for k in ar if k!=target},'newWords':sorted(set(br)-set(ar)),'targetRevision':tr['revision'],'maturity':tr['maturity'],'targetTests':len(tr['tests']),'targetSource':body,'targetCallBindings':bindings,'finalActorDescription':desc[-1] if desc else None})
(r/'results/preservation.json').write_text(json.dumps(rows,indent=2)+'\n',encoding='utf-8')
print(json.dumps([{k:v for k,v in x.items() if k not in ['operationsSource','targetSource','targetCallBindings','finalActorDescription']} for x in rows],indent=2))
