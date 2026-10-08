import pathlib,json,hashlib,subprocess,re
r=pathlib.Path('.agentlang/pair-repair-001');sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
load=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
f=load(r/'freeze.json');d=load(r/'dispatch.json');checks=[]
assert sha(r/'freeze.json')==d['freezeSha256']
for x in f['files']:
 expected=d['finalReviewSha256'] if x['path']=='review.md' else x['sha256'].lower()
 assert sha(r/x['path'])==expected,x['path']
checks.append('Frozen inputs unchanged; final predispatch review uses its separately pinned dispatch hash.')
for kind,folder in [('runtime','runtime/debug-artifacts'),('conventionalBroker','conventional-broker')]:
 actual={p.relative_to(r/folder).as_posix():sha(p) for p in (r/folder).rglob('*') if p.is_file()}
 assert actual=={x['path']:x['sha256'].lower() for x in f[kind]},kind
assert sha(pathlib.Path(f['host']['path']))==f['host']['sha256'].lower()
checks.append('Exact frozen runtime and both brokers unchanged.')
for name in ['retained-1','retained-2','reset-1','reset-2','conventional-1','conventional-2']:
 v=load(r/'results'/(name+'-score.json'));assert v['passed'] and v['behavior']['passedCases']==12 and v['actorUnchanged'] and not v['failure'],name
 assert v['oracleSha256']==sha(r/'oracle.json'),name
 actual={p.relative_to(r/'actors'/name).as_posix():sha(p) for p in (r/'actors'/name).rglob('*') if p.is_file()}
 expected={x['path']:x['sha256'] for x in v['actorInventoryAfter']}
 assert actual==expected,name
 if name.startswith('retained'):
  m=load(r/'post-mutations'/name/'summary.json');assert m['originalActorInventoryUnchanged']
  assert len(m['mutations'])==2
  for x in m['mutations']:assert x['candidateMatchesPredeclaredMutation'] and x['testsUnchanged'] and not x['replaceWordAccepted'] and not x['oracleScorerRun'],(name,x['name'])
 elif name.startswith('reset'):
  m=load(r/'post-mutations'/name/'summary.json');assert m['actorUnchanged']
  assert len(m['mutations'])==2
  for x in m['mutations']:assert not x['replacementAccepted'] and not x['independentScored'] and x['gateError']['code']=='COMMIT_TESTS_FAILED'
 else:
  for mut,n in [('wrong-second-id',6),('write-back-existing-second',9)]:
   v=load(r/'post-mutations'/name/(mut+'-score.json'));assert v['behavior']['passedCases']==n and not v['passed'] and v['actorTests']['exitCode']!=0 and v['actorUnchanged'];assert v['oracleSha256']==sha(r/'oracle.json')
checks.append('Six final original inventories unchanged after all scoring/mutation work; all six12/12 and all12mutations detected.')
for x in load(r/'results/preservation.json'):
 if x['actor'].startswith('conventional'):assert all(x[k] for k in ['domainUnchanged','projectUnchanged','inheritedHelperUnchanged','inheritedTestsUnchanged'])
 else:assert x['typesUnchanged'] and all(x['inheritedWordsUnchanged'].values()) and x['maturity']=='library'
assert 'POST_MUTATION_RESULTS_PENDING' not in pathlib.Path('reports/125-matched-pair-repair.md').read_text(encoding='utf-8')
assert not subprocess.run(['git','diff','--check'],capture_output=True).returncode
checks.append('Preservation, library maturity, report completeness and git diff whitespace checks pass.')
(r/'results/local-validation.json').write_text(json.dumps({'passed':True,'checks':checks,'scope':'Evidence/report milestone; no product source changes or full solution rerun.','scorerSha256':sha(r/'scoring/score-pair.ps1'),'oracleSha256':sha(r/'oracle.json'),'limitations':['Final review was completed after initial freeze and pinned before dispatch.','Host execution used after sandbox launcher ACL failure.']},indent=2)+'\n',encoding='utf-8')
print('\n'.join(checks))
