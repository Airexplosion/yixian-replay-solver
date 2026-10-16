"""Copy only the maintained simulator source; never copy game assemblies or data."""
import argparse
import pathlib
import shutil

parser = argparse.ArgumentParser()
parser.add_argument('--source', required=True, type=pathlib.Path)
args = parser.parse_args()
target = pathlib.Path(__file__).resolve().parents[1] / 'vendor/Yx.BattleSim'
source = args.source / 'Yx.BattleSim'
if not (source / 'Yx.BattleSim.csproj').is_file():
    raise SystemExit('Expected the maintained sim source directory')
files = {p.relative_to(source): p for p in source.rglob('*')
         if p.suffix in ('.cs', '.csproj') and not {'bin', 'obj'}.intersection(p.relative_to(source).parts)}
for relative, path in files.items():
    dest = target / relative
    dest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(path, dest)
for path in target.rglob('*.cs'):
    relative = path.relative_to(target)
    if not {'bin', 'obj'}.intersection(relative.parts) and relative not in files:
        path.unlink()
print('Synchronized', len(files), 'engine source files')
