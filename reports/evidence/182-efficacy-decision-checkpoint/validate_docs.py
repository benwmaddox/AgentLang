import hashlib
import json
import re
import subprocess
from pathlib import Path

repo = Path.cwd().resolve()
outputs = ['README.md', 'docs/PRD.md', 'docs/ROADMAP.md',
           'docs/NATIVE-NOMINALS-IMPLEMENTATION.md',
           'reports/182-efficacy-decision-checkpoint.md']
links = []
for relative in outputs:
    source = repo / relative
    for target in re.findall(r'\]\(([^)]+)\)', source.read_text(encoding='utf-8-sig')):
        target = target.strip('<>').split('#', 1)[0]
        if not target or '://' in target or target.startswith('mailto:'):
            continue
        path = (source.parent / target).resolve()
        assert path.is_relative_to(repo), (relative, target)
        assert path.exists(), (relative, target)
        links.append({'source': relative, 'target': path.relative_to(repo).as_posix()})
inputs = ['reports/177-reliability-and-vocabulary-checkpoint.md',
          'reports/178-preview-defect-control-calibration.md',
          'reports/179-preview-repair-and-record-migration.md',
          'reports/180-native-refined-mailbox.md',
          'reports/181-validated-snapshot-reuse.md',
          'reports/evidence/179-preview-repair-and-record-migration/archive.json',
          'reports/evidence/179-preview-repair-and-record-migration/evidence.zip',
          'reports/evidence/181-validated-snapshot-reuse/archive.json',
          'src/AgentLang.Llvm/OwningStackAot.fs',
          'tests/AgentLang.Llvm.Tests/Program.fs',
          'src/AgentLang.Core/Runtime.fs',
          'tests/AgentLang.Flow.Runtime.Tests/Program.fs']
pins = [{'path': relative, 'sha256': hashlib.sha256((repo / relative).read_bytes()).hexdigest()}
        for relative in inputs]
for pin in pins:
    original = subprocess.check_output(['git', 'show', 'HEAD:' + pin['path']])
    pin['committedSha256'] = hashlib.sha256(original).hexdigest()
    pin['gitBlob'] = subprocess.check_output(['git', 'rev-parse', 'HEAD:' + pin['path']]).decode().strip()
    filtered = subprocess.check_output(['git', 'hash-object', '--path=' + pin['path'], pin['path']]).decode().strip()
    assert filtered == pin['gitBlob'], pin['path']
audit = repo / 'reports/evidence/182-efficacy-decision-checkpoint/audit.md'
raw_audit = repo / '.agentlang/efficacy-decision-182/audit.md'
if raw_audit.exists():
    assert audit.read_bytes() == raw_audit.read_bytes()
output_pins = [{'path': relative, 'sha256': hashlib.sha256((repo / relative).read_bytes()).hexdigest()}
               for relative in outputs + ['reports/evidence/182-efficacy-decision-checkpoint/audit.md',
                                          'reports/evidence/182-efficacy-decision-checkpoint/native-plan.md']]
result = {'status': 'passed', 'baseRevision': subprocess.check_output(['git', 'rev-parse', 'HEAD']).decode().strip(),
          'inputPins': pins, 'outputPins': output_pins, 'localLinksChecked': links,
          'auditCopyCheckedAgainstRaw': raw_audit.exists(),
          'identityMethod': 'Working-file SHA-256 and committed SHA-256 recorded separately; Git-filtered object ID must match HEAD. An initial raw-byte check stopped on archive.json line-ending normalization.',
          'scope': 'Documentation links and unchanged committed evidence/source inputs; no runtime gate rerun or new participant trial.'}
receipt = repo / '.agentlang/efficacy-decision-182/validation.json'
receipt.parent.mkdir(parents=True, exist_ok=True)
receipt.write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps({'status': result['status'], 'inputPins': len(pins), 'localLinks': len(links)}))
