"""Exercise the replacement pin gate without running participant code."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile

import score

scratch = score.REPO / '.agentlang/typed-batch-comparison-220/root/pin-gate-tests'
scratch.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(dir=scratch) as directory:
    root = Path(directory)
    cli = score.REPO / 'src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll'
    files = []
    for name in sorted(score.required_paths(cli)):
        data = (score.REPO / name).read_bytes()
        files.append(dict(path=name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
    row = files[0]
    manifest = dict(status='frozen220', languageCli=cli.relative_to(score.REPO).as_posix(), files=files)
    path = root / 'pins.json'
    path.write_text(json.dumps(manifest))
    assert score.verify_pins(path, cli)['verifiedInputCount'] == len(files)
    rejected = 0
    variants = [
        dict(status='preparation'),
        dict(files=[]),
        dict(files=files + [row]),
        dict(languageCli='another-cli.dll'),
        dict(files=[{**row, 'sha256': '0' * 64}, *files[1:]]),
        dict(files=[{**row, 'bytes': row['bytes'] + 1}, *files[1:]]),
        dict(files=files[1:]),
    ]
    for variant in variants:
        altered = {**copy.deepcopy(manifest), **variant}
        path.write_text(json.dumps(altered))
        try:
            score.verify_pins(path, cli)
        except ValueError:
            rejected += 1
        else:
            raise AssertionError(f'Invalid pin manifest accepted: {variant}')
    assert rejected == len(variants)
print('220 language pin gate: complete current inputs accepted; 7 invalid manifests rejected.')
