"""Restore archived experiment sources into an isolated workspace .work directory."""
from pathlib import Path
import argparse
import hashlib
import json
import shutil

archive = Path(__file__).resolve().parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--workspace', type=Path, default=archive.parents[2])
parser.add_argument('--destination', type=Path, default=Path('.work/naga-csharp'))
parser.add_argument('--verify-only', action='store_true')
args = parser.parse_args()
def linked(path):
    return path.is_symlink() or getattr(path, 'is_junction', lambda: False)()

workspace = args.workspace.resolve(strict=True)
work = workspace / '.work'
if linked(work):
    raise SystemExit('Refusing a linked .work directory.')
destination = workspace / args.destination
if not destination.resolve().is_relative_to(work.resolve()) or destination.resolve() == work.resolve():
    raise SystemExit('Destination must be a child directory of workspace/.work.')
for parent in [destination, *destination.parents]:
    if parent == workspace:
        break
    if linked(parent):
        raise SystemExit('Refusing a linked destination path.')
inventory = json.loads((archive / 'inventory.json').read_text(encoding='utf-8'))
pending = []
for entry in inventory:
    relative = Path(entry['path'])
    source = archive / 'snapshot' / relative
    target = destination / relative
    if relative.is_absolute() or '..' in relative.parts or source.is_symlink():
        raise SystemExit(f'Invalid archive path: {relative}')
    if not target.resolve().is_relative_to(destination.resolve()):
        raise SystemExit(f'Destination escapes through a linked path: {relative}')
    for parent in [target, *target.parents]:
        if parent == destination:
            break
        if linked(parent):
            raise SystemExit(f'Refusing a linked destination path: {relative}')
    data = source.read_bytes()
    if len(data) != entry['bytes'] or hashlib.sha256(data).hexdigest() != entry['sha256']:
        raise SystemExit(f'Archive integrity failure: {relative}')
    if not args.verify_only:
        if target.exists():
            if linked(target) or not target.is_file() or target.read_bytes() != data:
                raise SystemExit(f'Refusing to overwrite different existing data: {relative}')
        else:
            pending.append((source, target))
if args.verify_only:
    print(f'Verified {len(inventory)} archived sources and synthetic inputs.')
else:
    # Check every conflict before creating any output; existing experiments stay intact.
    for source, target in pending:
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
    print(f'Restored {len(pending)} files to {destination}; existing identical files preserved.')
