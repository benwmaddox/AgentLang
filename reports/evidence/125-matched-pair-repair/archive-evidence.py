import pathlib,shutil,json,hashlib
src=pathlib.Path('.agentlang/pair-repair-001'); dst=pathlib.Path('reports/evidence/125-matched-pair-repair')
assert not dst.exists(), 'Archive must be created once after all work finishes.'
for p in (src/'logs').glob('*.trace.jsonl'):
 frames=[json.loads(x) for x in p.read_text(encoding='utf-8').splitlines() if x.strip()]
 assert len([x for x in frames if x.get('event')=='session-end'])==1,p
assert len(list((src/'logs').glob('*.trace.jsonl')))==6
for p in sorted(src.rglob('*')):
 if not p.is_file():continue
 rel=p.relative_to(src)
 if any(x in ('bin','obj','debug-artifacts','conventional-broker','__pycache__') or x.startswith('.scratch-') for x in rel.parts):continue
 if p.suffix.lower() in ('.dll','.exe','.pdb'):continue
 target=dst/rel;target.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(p,target)
files=[]
for p in sorted(dst.rglob('*')):
 if not p.is_file():continue
 rel=p.relative_to(dst);origin=src/rel;assert p.read_bytes()==origin.read_bytes(),rel
 files.append({'path':rel.as_posix(),'bytes':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()})
(dst/'index.json').write_text(json.dumps({'schemaVersion':1,'source':src.as_posix(),'exclusions':['bin','obj','debug-artifacts','conventional-broker binaries','scratch scoring copies'],'files':files},indent=2)+'\n',encoding='utf-8')
print('Archived',len(files),'files,',sum(x['bytes'] for x in files),'bytes; byte-identical to reviewed local evidence.')
