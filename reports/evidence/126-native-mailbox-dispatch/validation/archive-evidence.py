from pathlib import Path
import hashlib,json,shutil,gzip
repo=Path.cwd()
local=repo/'.agentlang/native-dispatch-001'
out=repo/'reports/evidence/126-native-mailbox-dispatch'
load=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
full=load(local/'full-release-validation.json')
assert full['passed'], 'Full local validation must pass before publication'
runs=sorted(local.glob('integration-run-*/integration-evidence.json'))
passed=[p for p in runs if load(p).get('passed')]
assert passed, 'No passing standalone integration run'
assert all(x['passed'] for x in load(local/'runtime-results.json')['runs'])
selected=max(passed,key=lambda p:load(p)['completedUtc'])
entries=[]
def copy(source,relative):
    data=source.read_bytes()
    raw_sha=hashlib.sha256(data).hexdigest()
    record={'source':source.relative_to(repo).as_posix()}
    if len(data)>1000000:
        old=out/relative
        assert old.resolve().is_relative_to(out.resolve())
        if old.exists(): old.unlink()
        record.update({'compression':'gzip','uncompressedBytes':len(data),'uncompressedSha256':raw_sha})
        relative=Path(str(relative)+'.gz')
        data=gzip.compress(data,mtime=0)
    destination=out/relative
    destination.parent.mkdir(parents=True,exist_ok=True)
    destination.write_bytes(data)
    record.update({'path':relative.as_posix(),'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest()})
    entries.append(record)
for p in sorted(local.iterdir()):
    if p.is_file() and not p.name.startswith('next-') and p.suffix in {'.json','.jsonl','.md','.log','.py','.cpp'}:
        copy(p,Path('validation')/p.name)
for report in runs:
    run=report.parent
    copy(report,Path('integration')/run.name/report.name)
    for p in sorted(run.rglob('*')):
        if not p.is_file() or p==report or 'dotnet-artifacts' in p.parts: continue
        if p.suffix in {'.json','.jsonl','.log','.txt','.c','.h','.ll'}:
            copy(p,Path('integration')/run.name/p.relative_to(run))
source_files=[
 'src/AgentLang.Llvm/LlvmAot.fs','src/AgentLang.Llvm/LlvmToolchain.fs',
 'src/AgentLang.Llvm/AgentLang.Llvm.fsproj','tests/AgentLang.Llvm.Tests/Program.fs',
 'tests/AgentLang.Llvm.Tests/AgentLang.Llvm.Tests.fsproj','scripts/Verify-NativeDispatch.ps1']
for pattern in ['src/AgentLang.Llvm/native/*.c','src/AgentLang.Llvm/native/*.h',
                'tests/fixtures/native-conformance/*abi*.json',
                'tests/fixtures/native-conformance/native-dispatch-v1.json',
                'experiments/AgentLang.NativeDispatch/*.fs',
                'experiments/AgentLang.NativeDispatch/*.fsproj',
                'experiments/AgentLang.NativeDispatch/*.flow',
                'experiments/AgentLang.NativeDispatch/*.c']:
    source_files.extend(p.relative_to(repo).as_posix() for p in repo.glob(pattern))
for name in sorted(set(source_files)):
    copy(repo/name,Path('source')/name)
index={'schemaVersion':1,'selectedIntegrationRun':selected.parent.name,
       'note':'Exact text evidence and tested source bytes; executables/object/build binaries excluded and identified by recorded hashes. Compiler validation records observed command summaries, not a full raw transcript.',
       'files':entries}
(out/'index.json').write_text(json.dumps(index,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'files':len(entries),'bytes':sum(x['bytes'] for x in entries),'selectedIntegrationRun':selected.parent.name}))
