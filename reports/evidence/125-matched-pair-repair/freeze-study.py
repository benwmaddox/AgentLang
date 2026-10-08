import json, hashlib, pathlib, datetime, subprocess
root=pathlib.Path('.agentlang/pair-repair-001')
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def inventory(p): return [{'path':f.relative_to(p).as_posix(),'bytes':f.stat().st_size,'sha256':sha(f)} for f in sorted(p.rglob('*')) if f.is_file()]
starts=json.loads((root/'actor-starts.json').read_text(encoding='utf-8'))
for a in starts['copies']:
    actual=inventory(pathlib.Path(a['project']))
    assert sorted(actual,key=lambda f:f['path'])==sorted(a['files'],key=lambda f:f['path']),a['actor']
controls={}
for name,expected in [('flow-reset-reviewed-oracle',10),('flow-retained-reviewed-oracle',10),('fsharp-seed-reviewed-oracle',10),('flow-corrected-reviewed-oracle',12),('fsharp-corrected-reviewed-oracle',12),('flow-mutant-wrong-second-path-committed-reviewed-oracle',6),('fsharp-mutant-wrong-second-path-reviewed-oracle',6),('flow-mutant-redundant-second-write-reviewed-oracle',9),('fsharp-mutant-redundant-second-write-reviewed-oracle',9)]:
    p=root/'scoring'/('score-'+name+'.json'); v=json.loads(p.read_text(encoding='utf-8'))
    assert v['oracleSha256'].upper()==sha(root/'oracle.json'),name
    assert v['behavior']['passedCases']==expected,(name,v['behavior'])
    assert v['actorUnchanged'] and not v['failure'],name
    controls[p.relative_to(root).as_posix()]={'sha256':sha(p),'behavior':v['behavior'],'passed':v['passed']}
paths=['plan.md','public-contract.md','oracle.json','actor-starts.json','runtime/runtime-pin.json','conventional-broker-pin.json','scoring/score-pair.ps1','review.md']
paths += [p.relative_to(root).as_posix() for p in sorted((root/'prompts').glob('*.md'))]
data={'schemaVersion':1,'frozenUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'repositoryHead':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),'actorsNotDispatched':True,'files':[{'path':p,'sha256':sha(root/p)} for p in paths],'actors':starts['copies'],'runtime':inventory(root/'runtime/debug-artifacts'),'conventionalBroker':inventory(root/'conventional-broker'),'host':{'path':'scripts/Start-SubagentTrialHostV2.ps1','sha256':sha(pathlib.Path('scripts/Start-SubagentTrialHostV2.ps1'))},'controls':controls}
assert len(data['runtime'])==22 and len(data['conventionalBroker'])==10
pin=json.loads((root/'runtime/runtime-pin.json').read_text(encoding='utf-8'))
assert data['host']['sha256']==pin['broker']['sha256']
with (root/'freeze.json').open('x',encoding='utf-8',newline='\n') as f: json.dump(data,f,indent=2); f.write('\n')
print('Frozen',len(starts['copies']),'actors; controls',len(controls),'sha256',sha(root/'freeze.json'))
