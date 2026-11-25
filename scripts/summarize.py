"""Derive compact test counters from the successful run, never from expected counts."""
from pathlib import Path
import json
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
file = root / 'evidence/verification-summary.json'
summary = json.loads(file.read_text(encoding='utf-8-sig'))
run = Path(summary['run']).resolve()
assert run.is_relative_to((root / 'evidence/runs').resolve()), run
counts = {}
for name in ('unit', 'http-adapter', 'postgres', 'gateway', 'services'):
    if name == 'services' and summary['services'] != 'PASSED':
        continue
    files = list((run / name).glob('*.trx'))
    assert len(files) == 1, files
    tree = ET.parse(files[0])
    node = next(n for n in tree.iter() if n.tag.rsplit('}', 1)[-1] == 'Counters')
    values = {k: int(node.attrib[k]) for k in ('total', 'passed', 'failed')}
    assert values['total'] > 0 and values['total'] == values['passed'] and values['failed'] == 0, values
    counts[name] = values
summary['testCounts'] = counts
summary['sourceRun'] = run.relative_to(root).as_posix()
summary['status'] = 'PASSED'
summary['chatTests'] = sum(value['passed'] for name, value in counts.items() if name != 'gateway')
file.write_text(json.dumps(summary, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
print(json.dumps({'status': 'PASSED', 'chatTests': summary['chatTests'], 'testCounts': counts}))
