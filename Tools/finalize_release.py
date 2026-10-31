"""Finalize a versioned release after isolated Unity validation; never publishes or runs Unity."""
import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tarfile
import xml.etree.ElementTree as ET
import zipfile

EXCLUDED_DIRS = {'.git', 'Library', 'Temp', 'Logs', 'Builds', 'Distribution',
                 '__pycache__', 'obj', '.vs', '.idea'}
TEXT_EXTENSIONS = {'.cs', '.md', '.json', '.unity', '.asset', '.py', '.csv',
                   '.shader', '.compute', '.hlsl', '.cginc', '.xml', '.asmdef', '.txt'}


def source_files(root):
    for directory, dirs, files in os.walk(root, followlinks=False):
        dirs[:] = sorted(d for d in dirs if d not in EXCLUDED_DIRS)
        for name in sorted(files):
            path = Path(directory) / name
            if path.suffix in {'.unitypackage', '.zip', '.pyc'} or '.partial-' in name:
                continue
            if path.is_symlink() or not path.resolve().is_relative_to(root.resolve()):
                raise ValueError('Unexpected source link: ' + str(path.relative_to(root)))
            yield path


def assert_public_text(path):
    if path.suffix.lower() not in TEXT_EXTENSIONS:
        return
    content = path.read_text(encoding='utf-8-sig', errors='strict')
    normalized = re.sub('/+', '/', content.replace('\\', '/').casefold())
    if re.search(r'[a-z]:/(?:unityproject/|suit/|users/sakuraink)', normalized):
        raise ValueError('Private development path in public text: ' + path.name)


def test_summary(path):
    root = ET.parse(path).getroot()
    keys = ('total', 'passed', 'failed', 'skipped', 'inconclusive')
    values = {key: int(root.get(key, '0')) for key in keys}
    if (root.get('result') != 'Passed' or values['total'] <= 0
            or values['total'] != values['passed']
            or any(values[key] for key in ('failed', 'skipped', 'inconclusive'))):
        raise ValueError('Tests must all pass: ' + path.name + ' ' + str(values))
    cases = list(root.iter('test-case'))
    if len(cases) != values['total'] or any(case.get('result') != 'Passed' for case in cases):
        raise ValueError('Test case results disagree with totals: ' + path.name)
    return dict(result=root.get('result'), **values,
                durationSeconds=float(root.get('duration', '0')),
                source=path.name,
                assemblies=sorted({node.get('name') for node in root.iter('test-suite')
                                   if node.get('type') == 'Assembly' and node.get('name')}))


def passed_json(path, tag=None, version=None):
    result = json.loads(path.read_text(encoding='utf-8-sig'))
    if result.get('status') != 'passed':
        raise ValueError('Validation did not pass: ' + path.name)
    if tag is not None and result.get('tag') != tag:
        raise ValueError('Wrong release tag in ' + path.name)
    if version is not None and result.get('packageVersion') != version:
        raise ValueError('Wrong package version in ' + path.name)
    assert_public_text(path)
    return result


def digest(path):
    checksum = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            checksum.update(block)
    return checksum.hexdigest()


def package_paths(package):
    paths = []
    with tarfile.open(package, 'r:gz') as archive:
        for entry in archive:
            if not entry.isfile() or Path(entry.name).name != 'pathname':
                continue
            with archive.extractfile(entry) as stream:
                value = stream.read().decode('utf-8-sig').strip()
            normalized = value.replace('\\', '/')
            roots = ('Assets/UnityGaussianSplatLOD',
                     'Assets/StreamingAssets/GaussianSplatLOD/Demo')
            if ('..' in normalized.split('/') or not any(
                    normalized == root or normalized.startswith(root + '/') for root in roots)):
                raise ValueError('Unexpected resource package asset path: ' + value)
            paths.append(normalized)
    if not paths or len(paths) != len(set(paths)):
        raise ValueError('Resource package asset list is empty or duplicated.')
    return sorted(paths)


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--work', required=True, type=Path)
    parser.add_argument('--repo', required=True, type=Path)
    parser.add_argument('--version', required=True)
    parser.add_argument('--tag', required=True)
    parser.add_argument('--upm-editor-results', type=Path,
                        help='Default: WORK/upm-editmode-tests.xml; accepts older upm-tests.xml.')
    parser.add_argument('--upm-playmode-results', type=Path,
                        help='Default: WORK/upm-playmode-tests.xml.')
    parser.add_argument('--date', default=dt.datetime.now(
        dt.timezone(dt.timedelta(hours=8))).date().isoformat(), help='Validation date in Asia/Shanghai.')
    parser.add_argument('--graphics-api', default='Direct3D11')
    parser.add_argument('--test-environment-note', action='append', default=[],
                        help='Operational validation limitation; repeat for additional notes.')
    return parser.parse_args()


def main():
    args = parse_args()
    work, repo = args.work.resolve(strict=True), args.repo.resolve(strict=True)
    artifacts = work / 'artifacts'
    if not re.fullmatch(r'\d+\.\d+\.\d+', args.version):
        raise ValueError('Expected a stable semantic version such as 0.2.0.')
    valid_tags = {'v' + args.version}
    if args.version.endswith('.0'):
        valid_tags.add('v' + args.version.rsplit('.', 1)[0])
    if args.tag not in valid_tags:
        raise ValueError('Tag does not match package version.')
    metadata = json.loads((repo / 'package.json').read_text(encoding='utf-8-sig'))
    if metadata['version'] != args.version:
        raise ValueError('Source package version differs from release version.')
    dt.date.fromisoformat(args.date)
    package = artifacts / ('Unity-GaussianLOD-' + args.tag + '.unitypackage')
    paths = package_paths(package)
    contents = artifacts / 'package-contents.json'
    if contents.exists() and sorted(json.loads(contents.read_text(encoding='utf-8-sig'))) != paths:
        raise ValueError('Recorded resource package asset list differs from the actual archive.')
    upm_editor = args.upm_editor_results or work / 'upm-editmode-tests.xml'
    if args.upm_editor_results is None and not upm_editor.exists():
        upm_editor = work / 'upm-tests.xml'
    native = list((repo / 'Editor').rglob('splat-transform.exe'))
    if len(native) != 1:
        raise ValueError('Expected exactly one native Aholo converter in the package.')
    report = {
        'release': args.tag, 'packageVersion': args.version, 'dateLocal': args.date,
        'platform': 'Windows x64', 'graphicsAPI': args.graphics_api,
        'testEnvironmentNotes': args.test_environment_note,
        'upmEditorTests': test_summary(upm_editor),
        'upmPlayModeTests': test_summary(args.upm_playmode_results or work / 'upm-playmode-tests.xml'),
        'importedResourcePackageEditorTests': test_summary(work / 'import-tests.xml'),
        'importedResourcePackagePlayModeTests': test_summary(work / 'import-playmode-tests.xml'),
        'export': passed_json(artifacts / 'export-validation.json', args.tag, args.version),
        'import': passed_json(artifacts / 'import-validation.json', args.tag, args.version),
        'importedSyntheticDemo': passed_json(artifacts / 'import-smoke-validation.json'),
        'resourcePackageAssetPaths': len(paths), 'nativeSHA256': digest(native[0]),
        'dependencies': 'URP 14.0.12, Newtonsoft JSON 3.2.1, Unity Test Framework 1.1.33; isolated projects used local cached packages.',
        'scope': 'Fresh resource package import and UPM package tests use a synthetic demo; development-host SUIT performance measurements are separate.',
        'notVerified': ['APK build', 'PICO 4 Ultra Android/Vulkan/XR and 90 Hz',
                        'iPhone 14 iOS/Metal/IL2CPP'],
        'excludedFromPublication': ['User campus PLY/cache datasets and screenshots',
                                    'Host project and other rendering plugins',
                                    'Host DemoPICO scene and independent camera destination UI'],
        'resourcePackageReportNote': 'The resource package was exported before final release reports and checksums were generated; those reports are included in the repository, UPM archive and release attachments.',
    }
    report['unity'] = report['export'].get('unity')
    if not report['unity']:
        raise ValueError('Unity version is missing from export validation.')
    image = artifacts / 'import-smoke-validation.png'
    with image.open('rb') as stream:
        if stream.read(8) != b'\x89PNG\r\n\x1a\n':
            raise ValueError('Synthetic demo screenshot is not a PNG.')
    notes = work / 'release-notes.md'
    assert_public_text(notes)
    evidence = list((work / 'evidence').iterdir())
    if not evidence or any(not p.is_file() or p.suffix.lower() not in {'.json', '.csv', '.md'}
                           for p in evidence):
        raise ValueError('Public evidence must contain only JSON/CSV/Markdown numeric summaries.')
    for path in [*source_files(repo), *evidence]:
        assert_public_text(path)

    # No mutations occur before all required input checks pass. Previous version
    # directories, Unity validation projects and source package are never cleared.
    validation = repo / 'Validation'
    validation.mkdir(exist_ok=True)
    public_evidence = validation / args.tag
    public_evidence.mkdir(exist_ok=True)
    for path in evidence:
        shutil.copy2(path, public_evidence / path.name)
    serialized = json.dumps(report, ensure_ascii=False, indent=2) + '\n'
    (validation / 'release-validation.json').write_text(serialized, encoding='utf-8')
    (artifacts / 'release-validation.json').write_text(serialized, encoding='utf-8')
    shutil.copy2(image, validation / 'synthetic-demo.png')
    shutil.copy2(notes, repo / 'RELEASE_NOTES.md')
    contents.write_text(json.dumps(paths, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    archive_path = artifacts / ('Unity-GaussianLOD-UPM-' + args.tag + '.zip')
    temporary = archive_path.with_suffix('.zip.partial-finalize')
    with zipfile.ZipFile(temporary, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in source_files(repo):
            archive.write(path, Path(metadata['name']) / path.relative_to(repo))
    os.replace(temporary, archive_path)
    names = [package.name, archive_path.name, 'release-validation.json']
    checksums = []
    for name in names:
        path = artifacts / name
        checksum = digest(path)
        checksums.append(checksum + '  ' + name)
        print(name, path.stat().st_size, checksum)
    (artifacts / 'SHA256SUMS.txt').write_text('\n'.join(checksums) + '\n', encoding='utf-8')
    print('Final artifacts and publication scope verified for', args.tag)


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, KeyError, ET.ParseError, tarfile.TarError) as error:
        print(type(error).__name__ + ': ' + str(error), file=sys.stderr)
        sys.exit(1)
