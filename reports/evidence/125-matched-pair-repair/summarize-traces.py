import pathlib,json,datetime,hashlib
r=pathlib.Path(__file__).resolve().parent
rows=[]
for p in sorted((r/'logs').glob('*.trace.jsonl')):
    frames=[json.loads(x) for x in p.read_text(encoding='utf-8-sig').splitlines() if x.strip()]
    end=[x for x in frames if x.get('event')=='session-end']
    if len(end)!=1: continue
    start=next(x for x in frames if x.get('event')=='session-start')
    exchanges=[]
    for x in frames:
        if x.get('event')!='exchange': continue
        q=json.loads(x['request']['canonical']); a=json.loads(x['response']['canonical'])
        exchanges.append({'index':x['index'],'operation':x['operation'],'requestBytes':x['request']['payloadUtf8Bytes'],'responseBytes':x['response']['payloadUtf8Bytes'],'ok':a.get('ok'),'kind':a.get('kind'),'error':a.get('error'),'word':q.get('word'),'requestedSyntaxVersion':q.get('syntaxVersion'),'returnedSyntaxVersion':(a['data'].get('syntaxVersion') if isinstance(a.get('data'),dict) else None)})
    parse=lambda x:datetime.datetime.fromisoformat(x.replace('Z','+00:00'))
    rows.append({'actor':p.name.removesuffix('.trace.jsonl'),'traceSha256':hashlib.sha256(p.read_bytes()).hexdigest(),'exchanges':len(exchanges),'requestBytes':sum(x['requestBytes'] for x in exchanges),'responseBytes':sum(x['responseBytes'] for x in exchanges),'structuredErrors':[x for x in exchanges if x['ok'] is False],'seconds':(parse(end[0]['finishedUtc'])-parse(start['startedUtc'])).total_seconds(),'sessionStart':start,'sessionEnd':end[0],'details':exchanges})
(r/'results').mkdir(exist_ok=True)
(r/'results/trace-summary.json').write_text(json.dumps({'source':'Exact broker payloads; bytes are not model tokens; full session duration includes idle and close.','providerUsage':None,'actors':rows},indent=2)+'\n',encoding='utf-8')
print(json.dumps([{k:x[k] for k in ['actor','exchanges','requestBytes','responseBytes','seconds']} | {'structuredErrors':len(x['structuredErrors'])} for x in rows],indent=2))
