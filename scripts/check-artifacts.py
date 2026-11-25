"""Verify frozen source hashes, local API surface and links without modifying sources."""
from pathlib import Path
import hashlib
import json
import re

root = Path(__file__).resolve().parents[1]
manifest = json.loads((root / 'source-manifest.json').read_text(encoding='utf-8-sig'))
for entry in manifest['sources']:
    local = root / entry['local'].replace('\\', '/')
    assert hashlib.sha256(local.read_bytes()).hexdigest() == entry['sha256'], local
    original = Path(entry['path'])
    if original.exists():
        assert hashlib.sha256(original.read_bytes()).hexdigest() == entry['sha256'], f'Original changed: {original}'
assert (root / 'source/schema.sql').read_bytes() == (root / 'Vairas.ChatBackend.Persistance/Migrations/Sql/baseline.sql').read_bytes()
api = json.loads((root / 'api/chat.openapi.json').read_text(encoding='utf-8-sig'))
operations = [(p, m) for p, methods in api['paths'].items() for m in methods if m in ('get','post','put','delete','patch')]
assert len(operations) == 14, operations
assert sum(p.startswith('/internal/') for p, _ in operations) == 2
assert '/internal/v1/confirmations/validate' not in api['paths']
for file in [root / 'README.md', root / 'VALIDATION.md', *root.glob('docs/*.md')]:
    if not file.exists():
        raise AssertionError(f'Document missing: {file}')
    for target in re.findall(r'\[[^\]]*\]\(<?([^\)]+?)>?\)', file.read_text(encoding='utf-8-sig')):
        if '://' in target or target.startswith('#'):
            continue
        path = target.split('#', 1)[0]
        assert (file.parent / path).exists(), f'{file}: broken link {target}'
print('Frozen hashes, 14 API operations, internal boundary and documentation links: PASS')
