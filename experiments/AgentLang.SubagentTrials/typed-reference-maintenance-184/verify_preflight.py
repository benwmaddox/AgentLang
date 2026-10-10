"""Verify calibration receipts and current source pins before study dispatch."""
import ast,hashlib,json
from pathlib import Path
REPO=Path(__file__).resolve().parents[3]
STUDY=Path(__file__).resolve().parent
BASE=REPO/'.agentlang/efficacy-maintenance-184'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
replay={'same-shipment-replay-same-status','same-shipment-replay-keeps-first-status','replay-within-batch-keeps-first','batch-replay-of-existing-reference-is-identity','empty-reference-is-valid-and-deduplicates-exactly'}
expected={'control-correct-2':set(),'control-always-append':replay,'control-global-dedup':{'same-reference-on-another-shipment'},'control-global-dedup-wrong-selftests':{'same-reference-on-another-shipment'},'fsharp-correct-migrated-184':set()}
receipts=[]
for label,failures in expected.items():
 p=BASE/'oracle-draft'/label/'result.json';r=read(p);b=r['behavior']
 assert b['executedCaseCount']==18 and not b['setupErrorCaseIds'],label
 assert set(b['failingCaseIds'])==failures and b['passedCaseCount']==18-len(failures),label
 assert r['participantSourceUnchanged'],label
 for endpoint in ['staticTypeMetadata','helperSignature','typeNegativeControls']:assert r[endpoint]['status']=='pass',(label,endpoint)
 assert r['scoreScriptSha256']==sha(STUDY/'oracle/score_reference.py'),label
 assert r['caseSourceSha256']==sha(STUDY/'oracle/cases.json'),label
 receipts.append({'path':p.relative_to(REPO).as_posix(),'sha256':sha(p),'passedCases':b['passedCaseCount'],'failedCases':sorted(failures)})
setup=read(BASE/'seed-draft/controls/control-manifest.json')
for control in setup['controls']:
 rows=control['setup']['responses'];assert control['setup']['exitCode']==0 and all(r['ok'] for r in rows)
 assert rows[-1]['kind']=='task.commit'
 assert sha(REPO/control['source'])==control['sourceSha256']
for p in STUDY.rglob('*.py'):ast.parse(p.read_text(encoding='utf-8-sig'))
files=[p for p in STUDY.rglob('*') if p.is_file() and not any(x in {'bin','obj','__pycache__'} for x in p.relative_to(STUDY).parts)]
files += [REPO/'scripts/Start-SubagentTrialHostV2.ps1',REPO/'scripts/Audit-SubagentTrialTerminationV2.ps1']
for build in ['accepted-build','conventional-build']:
 receipt=read(BASE/(build+'-exit.json'));assert receipt['exitCode']==0
 files += [p for p in (BASE/build/'bin').rglob('*') if p.is_file()]
pins=[{'path':p.relative_to(REPO).as_posix(),'sha256':sha(p)} for p in sorted(set(files))]
record={'status':'passed','receipts':receipts,'pins':pins,'modelSha256':sha(STUDY/'oracle/model.py'),'participantsDispatched':0}
(BASE/'preflight-verification.json').write_text(json.dumps(record,indent=2)+'\n')
print(json.dumps({'status':record['status'],'controlReceipts':len(receipts),'inputPins':len(pins)}))
