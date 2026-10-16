"""Build the Windows app and its animation packs from committed source art."""
import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'lumina-desktop'
RELEASE = ROOT / 'outputs/Lumina-Desktop'


def run(*args):
    subprocess.run(list(map(str, args)), cwd=ROOT, check=True)


def main():
    if os.name != 'nt':
        raise SystemExit('Build on Windows x64: Windows Compression API is required.')
    shutil.copytree(ROOT / 'assets-src', RELEASE / 'assets', dirs_exist_ok=True)
    run(sys.executable, SOURCE / 'tools/build_motion_packs.py')
    run(sys.executable, SOURCE / 'tools/build_extra_actions.py')
    assets = RELEASE / 'assets'
    shutil.copyfile(assets / 'animations.json', assets / 'animations-v3.json')
    run(sys.executable, SOURCE / 'tools/build_1k.py')
    shutil.copyfile(assets / 'animations-1k.json', assets / 'animations.json')
    run('powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', SOURCE / 'build.ps1')


if __name__ == '__main__':
    main()
