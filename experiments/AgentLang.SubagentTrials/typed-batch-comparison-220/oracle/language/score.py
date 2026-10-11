"""Reuse 216 semantics with explicit new runtime/source pins for study 220.

Never modifies historical pin files or treats this runtime as accepted by 216.
All language-specific behavior, preservation and library gates remain in the
original scorer. Independent source review and lifecycle assessment stay separate.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

REPO = Path(__file__).resolve().parents[5]
SOURCE = REPO / 'experiments/AgentLang.SubagentTrials/typed-batch-failure-216/oracle'

def required_paths(cli):
    paths = set()
    for project in ['src/AgentLang.Core/AgentLang.Core.fsproj', 'src/AgentLang.Cli/AgentLang.Cli.fsproj']:
        source = REPO / project
        paths.add(source)
        for item in ET.parse(source).getroot().iter('Compile'):
            paths.add(source.parent / item.attrib['Include'])
    for folder in [SOURCE, SOURCE.parent.parent / 'organic-reuse-210/oracle',
                   SOURCE.parent.parent / 'subscription-handoff-161/oracle', Path(__file__).parent]:
        for path in folder.iterdir():
            if path.is_file() and path.suffix in ['.py', '.json']:
                paths.add(path)
    paths.update(REPO / name for name in ['scripts/Start-SubagentTrialHostV2.ps1', 'scripts/Audit-SubagentTrialTerminationV2.ps1'])
    paths.update(cli.parent / name for name in ['AgentLang.Cli.dll', 'AgentLang.Cli.deps.json',
        'AgentLang.Cli.runtimeconfig.json', 'AgentLang.Core.dll', 'FSharp.Core.dll'])
    return {path.resolve().relative_to(REPO.resolve()).as_posix() for path in paths}

def verify_pins(path, cli):
    manifest = json.loads(path.read_text(encoding='utf-8-sig'))
    if manifest.get('status') != 'frozen220':
        raise ValueError('Study220 inputs must be frozen before scoring')
    files = manifest['files']
    if not files or len({row['path'] for row in files}) != len(files):
        raise ValueError('Pin manifest must contain unique nonempty inputs')
    cli_relative = cli.resolve().relative_to(REPO.resolve()).as_posix()
    if cli_relative != manifest['languageCli']:
        raise ValueError('CLI differs from frozen study220 entry point')
    missing = required_paths(cli) - {row['path'] for row in files}
    if missing:
        raise ValueError(f'Required runtime/source/oracle inputs omitted: {sorted(missing)}')
    for row in files:
        source = (REPO / row['path']).resolve()
        source.relative_to(REPO.resolve())
        data = source.read_bytes()
        if len(data) != row['bytes'] or hashlib.sha256(data).hexdigest() != row['sha256']:
            raise ValueError(f"Frozen220 input changed: {row['path']}")
    if cli_relative not in {row['path'] for row in files}:
        raise ValueError('CLI artifact must be an explicit pin')
    return dict(status='verified frozen220 inputs, not historical213 acceptance',
                verifiedInputCount=len(files), cli=cli_relative)

def main():
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument('--frozen-inputs', type=Path, required=True)
    args, forwarded = parser.parse_known_args()
    sys.path.insert(0, str(SOURCE))
    spec = importlib.util.spec_from_file_location('score216_for220', SOURCE / 'score_failure.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.validate_accepted_213_pins = lambda cli: verify_pins(args.frozen_inputs, cli)
    module.STUDY_ROOT = REPO / '.agentlang/typed-batch-comparison-220'
    sys.argv = [sys.argv[0], *forwarded]
    return module.main()

if __name__ == '__main__':
    raise SystemExit(main())
