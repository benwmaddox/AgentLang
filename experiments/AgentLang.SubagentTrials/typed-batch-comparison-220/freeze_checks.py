"""Exercise fail-closed setup prerequisites without touching real study inputs."""
import json
from pathlib import Path
import tempfile
import freeze

scratch = freeze.SCRATCH / 'root/freeze-checks'
scratch.mkdir(exist_ok=True)
with tempfile.TemporaryDirectory(dir=scratch) as temporary:
    freeze.SCRATCH = Path(temporary)
    setup = {'status': 'prepared220', 'participantsDispatched': 0,
             'capabilities': [], 'launchOrder': ['retained', 'fsharp']}
    path = freeze.SCRATCH / 'setup.json'
    path.write_text(json.dumps(setup))
    for folder, suffix in [('packets', '.md'), ('launchers', '.ps1')]:
        (freeze.SCRATCH / folder).mkdir()
        for arm in ['retained', 'fsharp']:
            (freeze.SCRATCH / folder / (arm + suffix)).write_text('fixture')
    for arm, names in freeze.RUNTIME_NAMES.items():
        directory = freeze.SCRATCH / 'runtime' / arm
        directory.mkdir(parents=True)
        for name in names:
            (directory / name).write_bytes(b'fixture')
    freeze.setup_check()
    rejected = 0
    for field, wrong in [('participantsDispatched', 1), ('capabilities', ['network.read']),
                         ('status', 'pending'), ('launchOrder', ['retained', 'retained'])]:
        path.write_text(json.dumps({**setup, field: wrong}))
        try:
            freeze.setup_check()
        except ValueError:
            rejected += 1
        else:
            raise AssertionError(f'Invalid {field} accepted')
    path.write_text(json.dumps(setup))
    for name, mutation in [('packets/fsharp.md', 'missing'), ('launchers/retained.ps1', 'blank'),
                           ('runtime/agentlang/AgentLang.Core.dll', 'missing')]:
        target = freeze.SCRATCH / name
        original = target.read_bytes()
        if mutation == 'missing':
            target.unlink()
        else:
            target.write_bytes(b' ')
        try:
            freeze.setup_check()
        except ValueError:
            rejected += 1
        else:
            raise AssertionError(f'Invalid {name} accepted')
        target.write_bytes(original)
    extra = freeze.SCRATCH / 'runtime/agentlang/Unexpected.dll'
    extra.write_bytes(b'extra')
    try:
        freeze.setup_check()
    except ValueError:
        rejected += 1
    else:
        raise AssertionError('Extra runtime DLL accepted')
    assert rejected == 8
    print('220 setup gates: valid fixture accepted; 8 invalid prerequisites rejected.')
