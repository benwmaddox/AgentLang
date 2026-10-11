"""Pin complete study inputs before dispatch; verify them without re-freezing."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import xml.etree.ElementTree as ET

REPO = Path(__file__).resolve().parents[3]
STUDY = Path(__file__).resolve().parent
SCRATCH = REPO / '.agentlang/typed-batch-comparison-220'
MANIFEST = SCRATCH / 'frozen-inputs.json'
RUNTIME_NAMES = {
    'agentlang': {'AgentLang.Cli.dll', 'AgentLang.Cli.deps.json', 'AgentLang.Cli.runtimeconfig.json',
                 'AgentLang.Core.dll', 'FSharp.Core.dll'},
    'conventional': {'AgentLang.Conventional.Cli.dll', 'AgentLang.Conventional.Cli.deps.json',
                     'AgentLang.Conventional.Cli.runtimeconfig.json', 'AgentLang.Conventional.dll', 'FSharp.Core.dll'},
}


def digest(path):
    data = path.read_bytes()
    return {'path': path.resolve().relative_to(REPO.resolve()).as_posix(),
            'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}


def project_sources(project, seen=None):
    seen = set() if seen is None else seen
    project = project.resolve()
    if project in seen:
        return seen
    project.relative_to(REPO.resolve())
    seen.add(project)
    for item in ET.parse(project).getroot():
        for entry in item:
            if entry.tag == 'Compile':
                seen.add((project.parent / entry.attrib['Include']).resolve())
            elif entry.tag == 'ProjectReference':
                project_sources(project.parent / entry.attrib['Include'], seen)
    return seen


def text_files(root):
    return {path for path in root.rglob('*') if path.is_file()
            and not any(part in {'bin', 'obj', '__pycache__'} for part in path.relative_to(root).parts)}


def expected_inputs():
    spec = importlib.util.spec_from_file_location('language220_pins', STUDY / 'oracle/language/score.py')
    adapter = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(adapter)
    cli = SCRATCH / 'runtime/agentlang/AgentLang.Cli.dll'
    files = {REPO / name for name in adapter.required_paths(cli)}
    files |= text_files(STUDY)
    for folder in ['packets', 'launchers', 'baselines/retained', 'baselines/fsharp']:
        files |= text_files(SCRATCH / folder)
    files |= set((SCRATCH / 'runtime/conventional').iterdir())
    files |= project_sources(REPO / 'experiments/AgentLang.Conventional.Cli/AgentLang.Conventional.Cli.fsproj')
    files |= project_sources(REPO / 'experiments/AgentLang.Business/AgentLang.Business.fsproj')
    files |= {SCRATCH / 'setup.json', SCRATCH / 'root/readiness.json',
              SCRATCH / 'root/preflight.json',
              SCRATCH / 'root/preflight.py',
              SCRATCH / 'root/shell-fix.json', SCRATCH / 'root/broker-smoke.json',
              SCRATCH / 'root/broker_smoke.py',
              REPO / 'reports/evidence/220-typed-batch-comparison-readiness/index.json',
              REPO / 'reports/evidence/220-typed-batch-comparison-readiness/readiness.zip',
              REPO / 'reports/evidence/220-typed-batch-comparison-readiness/review.md'}
    files |= {REPO / 'reports/evidence/221-typed-batch-fresh-comparison/attempt1-inputs.zip',
              REPO / 'reports/evidence/221-typed-batch-fresh-comparison/attempt1-index.json'}
    for arm in ['retained', 'fsharp']:
        files |= {SCRATCH / f'root/preflight-{arm}.stdout.jsonl',
                  SCRATCH / f'root/preflight-{arm}.stderr.txt'}
        files |= {SCRATCH / f'root/broker-smoke/{arm}/{name}' for name in
                  ['launch.ps1', 'trace.jsonl', 'stdout.jsonl', 'stderr.txt',
                   'audit.stdout.json', 'audit.stderr.txt']}
    return {path.resolve() for path in files}


def setup_check():
    setup = json.loads((SCRATCH / 'setup.json').read_text(encoding='utf-8'))
    if setup.get('status') != 'prepared220' or setup.get('participantsDispatched') != 0 or setup.get('capabilities') != []:
        raise ValueError('Incomplete or already dispatched setup')
    if sorted(setup['launchOrder']) != ['fsharp', 'retained']:
        raise ValueError('Setup must record exactly two launch arms')
    for folder, suffix in [('packets', '.md'), ('launchers', '.ps1')]:
        expected = {f'{arm}{suffix}' for arm in ['retained', 'fsharp']}
        files = text_files(SCRATCH / folder)
        if {path.relative_to(SCRATCH / folder).as_posix() for path in files} != expected:
            raise ValueError(f'Incomplete {folder}')
        if any(not path.read_bytes().strip() for path in files):
            raise ValueError(f'Empty {folder}')
    for arm, names in RUNTIME_NAMES.items():
        if {path.name for path in (SCRATCH / 'runtime' / arm).iterdir()} != names:
            raise ValueError(f'Unexpected or missing private {arm} runtime files')
    return setup


def initial_actor_check():
    for arm in ['retained', 'fsharp']:
        baseline = SCRATCH / 'baselines' / arm
        actor = SCRATCH / 'actors' / arm
        before = {path.relative_to(baseline).as_posix(): path.read_bytes() for path in text_files(baseline)}
        after = {path.relative_to(actor).as_posix(): path.read_bytes() for path in text_files(actor)}
        if not before or before != after:
            raise ValueError(f'Initial {arm} actor differs from baseline')
        if arm == 'retained':
            checksum = hashlib.sha256()
            for name, content in sorted(before.items()):
                encoded = name.encode()
                checksum.update(len(encoded).to_bytes(8, 'big'))
                checksum.update(encoded)
                checksum.update(len(content).to_bytes(8, 'big'))
                checksum.update(content)
            if checksum.hexdigest() != 'b99ad6a855a96aa2ed577230b0c03c285d38e65f40f850ea7420d0f29d5f1611':
                raise ValueError('Retained baseline differs from accepted unmigrated seed')


def verify(initial=False):
    setup_check()
    data = json.loads(MANIFEST.read_text(encoding='utf-8'))
    if data.get('status') != 'frozen220' or not data.get('dispatchAllowed'):
        raise ValueError('Not a complete dispatch freeze')
    rows = data['files']
    expected = {path.relative_to(REPO).as_posix() for path in expected_inputs()}
    if len(rows) != len({row['path'] for row in rows}) or expected != {row['path'] for row in rows}:
        raise ValueError('Frozen input closure differs from required study inputs')
    for row in rows:
        if digest(REPO / row['path']) != row:
            raise ValueError(f"Frozen input changed: {row['path']}")
    if initial:
        initial_actor_check()
    return {'status': 'verified', 'initialActorsChecked': initial, 'inputCount': len(rows),
            'manifestSha256': hashlib.sha256(MANIFEST.read_bytes()).hexdigest()}


def freeze():
    if MANIFEST.exists():
        raise ValueError('Freeze already exists; verify rather than overwrite')
    initial_actor_check()
    setup = setup_check()
    broker = json.loads((SCRATCH / 'root/broker-smoke.json').read_text(encoding='utf-8'))
    if broker.get('status') != 'passed' or not broker.get('preparationOnly') or set(broker['arms']) != {'retained', 'fsharp'}:
        raise ValueError('Both corrected broker launch paths must pass')
    if any(row.get('exitCode') != 0 or row.get('auditExitCode') != 0 for row in broker['arms'].values()):
        raise ValueError('Broker startup/normal closure smoke failed')
    preflight = json.loads((SCRATCH / 'root/preflight.json').read_text(encoding='utf-8'))
    if preflight.get('status') != 'passed' or preflight.get('participantsDispatched') != 0 or not preflight.get('preparationOnly'):
        raise ValueError('Both fixed actor validation projects must pass before freeze')
    if set(preflight['arms']) != {'retained', 'fsharp'} or any(
            row.get('exitCode') != 0 or row.get('responseCount') != 2 or row.get('allOk') is not True
            for row in preflight['arms'].values()):
        raise ValueError('Incomplete or failed fixed actor preflight')
    index_path = REPO / 'reports/evidence/220-typed-batch-comparison-readiness/index.json'
    index = json.loads(index_path.read_text(encoding='utf-8'))
    if index.get('status') != 'reviewed preparation controls; no participant outcome or dispatch freeze' or index.get('participantsDispatched') != 0:
        raise ValueError('Reviewed calibration readiness evidence missing')
    archive = index_path.parent / index['archive']
    if hashlib.sha256(archive.read_bytes()).hexdigest() != index['archiveSha256']:
        raise ValueError('Reviewed readiness archive changed')
    readiness = json.loads((SCRATCH / 'root/readiness.json').read_text(encoding='utf-8'))
    if readiness.get('participantsDispatched') != 0 or readiness.get('capabilities') != [] or readiness.get('filesystem') != 'virtual':
        raise ValueError('Readiness boundary changed')
    expected_receipts = {f'{folder}/{name}' for folder, arm in [
        ('src/AgentLang.Cli/bin/Release/net9.0', 'agentlang'),
        ('experiments/AgentLang.Conventional.Cli/bin/Release/net9.0', 'conventional')]
        for name in RUNTIME_NAMES[arm]}
    rows = readiness['runtimeFiles']
    if len(rows) != 10 or {row['path'] for row in rows} != expected_receipts:
        raise ValueError('Fresh build receipt must include exactly both complete runtimes')
    for row in rows:
        source = REPO / row['path']
        arm = 'conventional' if 'Conventional' in row['path'] else 'agentlang'
        private = SCRATCH / 'runtime' / arm / source.name
        actual = digest(private)
        if any(actual[key] != row[key] for key in ['bytes', 'sha256']):
            raise ValueError(f'Private runtime differs from fresh build receipt: {private}')
    data = {'status': 'frozen220', 'scope': 'complete fresh pair dispatch inputs',
            'dispatchAllowed': True, 'participantsDispatchedAtFreeze': 0,
            'languageCli': (SCRATCH / 'runtime/agentlang/AgentLang.Cli.dll').relative_to(REPO).as_posix(),
            'launchOrder': setup['launchOrder'],
            'files': [digest(path) for path in sorted(expected_inputs())]}
    original = SCRATCH / 'frozen-inputs.json'
    if original != MANIFEST and original.exists():
        data['supersededSetupAttemptManifestSha256'] = hashlib.sha256(original.read_bytes()).hexdigest()
    MANIFEST.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
    return verify(initial=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--freeze', action='store_true')
    parser.add_argument('--initial', action='store_true')
    parser.add_argument('--manifest', type=Path, default=MANIFEST,
                        help='Use a separate immutable manifest for a corrected protocol attempt')
    args = parser.parse_args()
    MANIFEST = args.manifest.resolve()
    MANIFEST.relative_to(SCRATCH.resolve())
    print(json.dumps(freeze() if args.freeze else verify(args.initial)))
