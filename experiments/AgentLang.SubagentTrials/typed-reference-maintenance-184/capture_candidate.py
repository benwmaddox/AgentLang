"""Capture a terminal participant candidate from the frozen study184 input manifest."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil

REPO=Path(__file__).resolve().parents[3]
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
parser=argparse.ArgumentParser();parser.add_argument('actor');parser.add_argument('--manifest',default=str(REPO / '.agentlang/efficacy-maintenance-184/trials/actors.json'))
args=parser.parse_args()
manifest=Path(args.manifest).resolve()
assert manifest.is_relative_to((REPO/'.agentlang/efficacy-maintenance-184').resolve())
HERE=manifest.parent
config=json.loads(manifest.read_text())
actor=next(a for a in config['actors'] if a['name']==args.actor)
prompt=actor['prompt']
assert sha(REPO/prompt['path'])==prompt['sha256']
assert sha(REPO/actor['cli'])==actor['cliSha256']
trace_path=(REPO/prompt['trace']).resolve()
assert trace_path.is_relative_to(HERE.resolve())
project=(REPO/actor['project']).resolve()
assert project.is_relative_to((HERE/'actors').resolve())
def inventory(root):
 return {p.relative_to(root).as_posix():sha(p) for p in sorted(root.rglob('*')) if p.is_file() and not any(x in {'bin','obj','__pycache__'} for x in p.relative_to(root).parts) and p.name!='WRITE.lock'}
source_before=inventory(project)
trace_before=sha(trace_path)
trace=[json.loads(line) for line in trace_path.read_text(encoding='utf-8-sig').splitlines() if line.strip()]
end=trace[-1]
assert end['event']=='session-end',end
assert end['hostExitCode'] is not None and end['runtimeExitCode'] is not None,end
destination=HERE/'snapshots'/args.actor
assert not destination.exists(),destination
shutil.copytree(project,destination,ignore=shutil.ignore_patterns('bin','obj','WRITE.lock','__pycache__'))
pins=[{'path':p.relative_to(destination).as_posix(),'sha256':sha(p),'bytes':p.stat().st_size} for p in sorted(destination.rglob('*')) if p.is_file()]
assert inventory(destination)==source_before
assert inventory(project)==source_before and sha(trace_path)==trace_before
normal=end['terminationKind']=='host-close' and end['hostExitCode']==0 and end['runtimeExitCode']==0
record={'actor':args.actor,'arm':actor['arm'],'project':str(destination.relative_to(REPO)),'terminal':end,'normalBrokerFinalization':normal,'terminalPolicy':'Failed terminal candidates may be captured for evidence; finalization assessed separately. Live candidates cannot be captured.','traceSha256':sha(trace_path),'promptSha256':prompt['sha256'],'cliSha256':actor['cliSha256'],'candidatePins':pins,'behaviorScored':False,'preservationReviewed':False}
(HERE/f'capture-{args.actor}.json').write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'actor':args.actor,'snapshot':str(destination),'terminal':end}))
