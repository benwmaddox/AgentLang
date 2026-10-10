"""Materialize six frozen isolated trials only after accepted calibration."""
import hashlib,json,shutil
from pathlib import Path
REPO=Path(__file__).resolve().parents[3]
STUDY=Path(__file__).resolve().parent
BASE=REPO/'.agentlang/efficacy-maintenance-184'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
gate=json.loads((BASE/'dispatch-gate.json').read_text())
assert gate['status']=='passed'
for pin in gate['pins']:
 assert sha(REPO/pin['path'])==pin['sha256'],pin['path']
HERE=BASE/'trials';assert not HERE.exists();HERE.mkdir()
common='The human authorizes edits only to your assigned isolated trial project and local test execution through this broker. You are an external coding agent. Read only this prompt, then launch the exact broker command once, without changing its allowlist or limits. Use only the broker for discovery, edits and tests. Do not read repository files, other trials, oracles or reports; do not use web tools or other agents. Disclose any non-broker tool use. One session, at most 100 exchanges. Poll the same live handle after timeouts; do not restart for silence or repeat uncertain mutations. Finish with {"op":"host.close"} and observe terminal status.\n'
primer='Flow/2 uses fn, named typed inputs, immutable let, record properties value.field, dotted calls, newline separators and exhaustive matches. Include frontend:"flow", syntaxVersion:2 on define/eval. Use describe/source/tests/examples/help to discover syntax. Exact-root calls begin with a dot. Omitted effects means pure. Begin task.begin, define, test, commit with library:true for library functions, then task.commit. Library dependency closure and own coverage gates remain enforced. Do not edit storage manifests.\n'
flow_ops=json.loads((STUDY/'prompts/proposed-allowlist.json').read_text())['allowedOperations']
actors=[]
for name,arm in [('retained-1','retained'),('reset-1','reset-rich'),('fsharp-1','fsharp'),('fsharp-2','fsharp'),('reset-2','reset-rich'),('retained-2','retained')]:
 source=REPO/gate['seeds'][arm];project=HERE/'actors'/name
 shutil.copytree(source,project,ignore=shutil.ignore_patterns('bin','obj','WRITE.lock','__pycache__'))
 cli=REPO/gate['clis']['fsharp' if arm=='fsharp' else 'flow'];temp=HERE/'temp'/name;temp.mkdir(parents=True)
 trace=HERE/('trace-'+name+'.jsonl');ops=['inspect','read','search','patch','replace','validate'] if arm=='fsharp' else flow_ops
 launch=f"$env:TEMP = '{temp}'\n$env:TMP = $env:TEMP\n$env:DOTNET_CLI_HOME = '{temp}'\n"
 launch+=f"& '{REPO/'scripts/Start-SubagentTrialHostV2.ps1'}' -CliDll '{cli}' -ProjectPath '{project}' -TracePath '{trace}' -AllowedOperations @('"+','.join(ops)+"') -Profile '"+('conventional' if arm=='fsharp' else 'agentlang')+"' "
 if arm=='fsharp':launch+="-AdditionalCliArguments @('--validation-project','tests/AgentLang.TypedReference.Tests.fsproj') "
 launch+="-ClockValue '2000-01-01T00:00:00Z' -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100"
 task=(STUDY/'prompts'/('fsharp.md' if arm=='fsharp' else 'agentlang-'+arm+'.md')).read_text()
 specific='Edit only business/Business.fs and tests/Program.fs; project files are frozen. Validate uses the fixed local test project.\n' if arm=='fsharp' else primer
 prompt=HERE/('prompt-'+name+'.md');prompt.write_text(common+'\n'+task+'\n'+specific+'\nLaunch with exec_command, tty:true, yield_time_ms:10000.\n\n```powershell\n'+launch+'\n```\n')
 rel=lambda p:p.relative_to(REPO).as_posix()
 actors.append({'name':name,'arm':arm,'project':rel(project),'cli':rel(cli),'cliSha256':sha(cli),'operations':ops,'prompt':{'path':rel(prompt),'sha256':sha(prompt),'trace':rel(trace)},'initialPins':[{'path':rel(p),'sha256':sha(p)} for p in sorted(project.rglob('*')) if p.is_file()]})
(HERE/'actors.json').write_text(json.dumps({'gateSha256':sha(BASE/'dispatch-gate.json'),'actors':actors},indent=2)+'\n')
print('Prepared six isolated trials; no participants dispatched')
