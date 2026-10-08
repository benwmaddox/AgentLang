import pathlib, json, hashlib, shutil, subprocess
r=pathlib.Path('.agentlang/pair-repair-001').resolve(); actor=r/'actors/reset-1'; owned=r/'post-mutations/reset-1'; owned.mkdir(parents=True,exist_ok=True)
def inv(p): return {f.relative_to(p).as_posix():hashlib.sha256(f.read_bytes()).hexdigest() for f in p.rglob('*') if f.is_file()}
before=inv(actor)
text=(actor/'dictionary.agent').read_text(encoding='utf-8')
source=next(x.strip() for x in text.split('// frontend: flow/2') if x.strip().startswith('fn invoice.queue-reminders-for-pair('))
source=source.split('// frontend: flow/1')[0].strip()
assert source.endswith('}')
mutants={'wrong-second-path':source.replace('invoice::reminder-path(pair.second.id)','invoice::reminder-path(pair.first.id)'), 'redundant-second-write':source.replace('file::read(second-path)','let existing-marker = file::read(second-path);\n        file::write(second-path, existing-marker);\n        existing-marker')}
assert source.count('invoice::reminder-path(pair.second.id)')==1 and source.count('file::read(second-path)')==1
(owned/'plan.md').write_text('Test only two predeclared target-body mutations on copies of the closed reset-1 actor. Preserve all actor tests and metadata. Run define/test/test-all/replace-word. Gate rejection is detection; do not score unchanged committed source as a mutant. Score with the frozen oracle only if replacement succeeds. Verify original actor inventory unchanged.\n',encoding='utf-8')
summary=[]
for name,mutant in mutants.items():
    dest=owned/name; shutil.copytree(actor,dest)
    (owned/(name+'.flow')).write_text(mutant,encoding='utf-8')
    requests=[{'op':'define','syntaxVersion':2,'frontend':'flow','replace':True,'expectedRevision':2,'source':mutant},{'op':'test','word':'invoice.queue-reminders-for-pair'},{'op':'test-all'},{'op':'replace-word','word':'invoice.queue-reminders-for-pair'}]
    proc=subprocess.run(['dotnet',str(r/'runtime/debug-artifacts/AgentLang.Cli.dll'),'--project',str(dest),'--clock','2000-01-01T00:00:00Z','--allow','fs.read,fs.write','--jsonl'],input='\n'.join(json.dumps(q) for q in requests)+'\n',capture_output=True,text=True,encoding='utf-8',timeout=120,creationflags=subprocess.CREATE_NO_WINDOW)
    responses=[json.loads(x) for x in proc.stdout.splitlines() if x.strip()]
    assert proc.returncode==0 and len(responses)==4 and responses[0]['ok'],(name,proc.stderr,proc.stdout)
    evidence={'actor':'reset-1','mutation':name,'requests':requests,'responses':responses,'stderr':proc.stderr,'exitCode':proc.returncode,'originalUnchanged':before==inv(actor)}
    (owned/(name+'-gate.json')).write_text(json.dumps(evidence,indent=2)+'\n',encoding='utf-8')
    accepted=responses[-1]['ok']
    if accepted:
        scored=subprocess.run(['pwsh','-NoProfile','-File',str(r/'scoring/score-pair.ps1'),'-Language','flow','-ActorProject',str(dest),'-OutputPath',str(owned/(name+'-score.json'))],capture_output=True,text=True,encoding='utf-8',timeout=240,creationflags=subprocess.CREATE_NO_WINDOW)
        (owned/(name+'-scorer-output.txt')).write_text(scored.stdout+scored.stderr,encoding='utf-8')
        assert scored.returncode==0
    summary.append({'mutation':name,'replacementAccepted':accepted,'gateError':responses[-1].get('error'),'ownTestResponse':responses[1],'independentScored':accepted})
assert before==inv(actor)
(owned/'summary.json').write_text(json.dumps({'actor':'reset-1','actorUnchanged':True,'mutations':summary},indent=2)+'\n',encoding='utf-8')
print(json.dumps([{'mutation':x['mutation'],'replacementAccepted':x['replacementAccepted'],'gateError':x['gateError']} for x in summary],indent=2))
