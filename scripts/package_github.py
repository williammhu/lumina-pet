"""Create two ordinary, complementary ZIPs below GitHub's 2 GiB asset limit."""
import argparse
import hashlib
import json
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--release-dir', type=Path, default=ROOT / 'outputs/Lumina-Desktop')
    parser.add_argument('--output-dir', type=Path, default=ROOT / 'dist')
    args = parser.parse_args()
    release = args.release_dir.resolve()
    version = (ROOT / 'release-version.txt').read_text(encoding='utf8').strip()
    manifest_path = release / 'assets/animations.json'
    manifest = json.loads(manifest_path.read_text(encoding='utf8'))
    assert len(manifest['states']) == 23 and len(manifest['actions']) == 21
    assert sum(s['frames'] for s in manifest['states'].values()) == 1340
    groups = [[], []]
    totals = [0, 0]
    for state in sorted(manifest['states'].values(), key=lambda s: s['bytes'], reverse=True):
        assert state['width'] == 1280 and state['height'] == 1536 and state['codec'] == 'windows-xpress'
        path = release / 'assets' / state['path']
        assert path.is_relative_to(release) and path.stat().st_size == state['bytes']
        assert digest(path) == state['sha256'], 'Motion pack SHA-256 mismatch'
        index = min(range(2), key=lambda i: totals[i])
        groups[index].append((path, path.relative_to(release).as_posix()))
        totals[index] += path.stat().st_size
    for name in ['Lumina.exe', 'Lumina.exe.config', 'assets/portrait.png', 'assets/lumina.ico', 'assets/animations.json']:
        groups[0].append((release / name, name))
    groups[0].extend([(ROOT / 'docs/RELEASE-v4.md', 'README.md'), (ROOT / 'LICENSE', 'LICENSE')])
    assert max(totals) < 2 * 1024**3 - 16 * 1024**2
    args.output_dir.mkdir(parents=True, exist_ok=True)
    receipts = []
    observed = set()
    for number, files in enumerate(groups, 1):
        archive = args.output_dir / f'Lumina-Windows-v{version}-part{number}.zip'
        with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=3, strict_timestamps=False) as z:
            for path, relative in sorted(files, key=lambda item: item[1]):
                arcname = 'Lumina-Desktop/' + relative
                assert arcname not in observed
                observed.add(arcname)
                z.write(path, arcname)
        assert archive.stat().st_size < 2 * 1024**3
        with zipfile.ZipFile(archive) as z:
            assert z.testzip() is None, 'Corrupt ZIP'
        receipts.append({'name': archive.name, 'bytes': archive.stat().st_size, 'sha256': digest(archive)})
        print(json.dumps(receipts[-1]), flush=True)
    expected = {'Lumina-Desktop/assets/' + s['path'] for s in manifest['states'].values()}
    assert expected <= observed
    (args.output_dir / 'SHA256SUMS.txt').write_text(''.join(f"{r['sha256']}  {r['name']}\n" for r in receipts), encoding='utf8')
    (args.output_dir / 'release-files.json').write_text(json.dumps({'version': version, 'files': receipts}, indent=2) + '\n', encoding='utf8')


if __name__ == '__main__':
    main()
