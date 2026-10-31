"""Publish an explicitly requested version using GCM; never print credentials.

Usage: github_release.py publish --tag v0.2 --target <commit-SHA>
       --notes Distribution/v0.2/release-notes.md <asset> [<asset> ...]
"""
import argparse
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request

REPOSITORY = 'Hakutyan-bai/Unity-GaussianLOD'
API = 'https://api.github.com/repos/' + REPOSITORY


def authentication():
    env = dict(os.environ, GCM_INTERACTIVE='never', GIT_TERMINAL_PROMPT='0')
    result = subprocess.run(['git', 'credential', 'fill'],
                            input='protocol=https\nhost=github.com\npath=' + REPOSITORY + '.git\n\n',
                            capture_output=True, text=True, env=env, timeout=30)
    credentials = dict(line.split('=', 1) for line in result.stdout.splitlines() if '=' in line)
    if result.returncode or not credentials.get('password'):
        raise RuntimeError('GitHub credentials unavailable from Git Credential Manager; authenticate GitHub locally first.')
    return credentials['password']


class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if urllib.parse.urlsplit(newurl).scheme != 'https':
            raise RuntimeError('Refusing a non-HTTPS GitHub download redirect.')
        redirected = super().redirect_request(req, fp, code, msg, headers, newurl)
        if redirected is not None and urllib.parse.urlsplit(req.full_url).netloc != urllib.parse.urlsplit(newurl).netloc:
            redirected.remove_header('Authorization')
        return redirected


def open_request(token, endpoint, data=None, method=None, content_type='application/json',
                 accept='application/vnd.github+json'):
    url = endpoint if endpoint.startswith('https://') else API + endpoint
    if urllib.parse.urlsplit(url).hostname not in ('api.github.com', 'uploads.github.com'):
        raise ValueError('Authenticated requests must target the GitHub API or upload endpoint.')
    payload = json.dumps(data).encode('utf-8') if isinstance(data, dict) else data
    headers = {'Authorization': 'Bearer ' + token, 'Accept': accept,
               'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'Unity-GaussianLOD-release',
               'Content-Type': content_type}
    req = urllib.request.Request(url, data=payload, headers=headers, method=method)
    return urllib.request.build_opener(SafeRedirect()).open(req, timeout=180)


def request(token, endpoint, data=None, method=None, content_type='application/json'):
    with open_request(token, endpoint, data, method, content_type) as response:
        return json.load(response)


def file_sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def remote_asset_sha256(token, asset):
    digest = asset.get('digest') or ''
    if re.fullmatch(r'sha256:[0-9a-fA-F]{64}', digest):
        return digest[7:].lower()
    # Older assets may omit a server digest; hash the authenticated binary.
    # Matching byte length alone never proves that a repeated upload is safe.
    with open_request(token, asset['url'], accept='application/octet-stream') as response:
        if 'application/json' in response.headers.get('Content-Type', ''):
            raise RuntimeError('GitHub did not provide binary bytes for verification: ' + asset['name'])
        digest = hashlib.sha256()
        for block in iter(lambda: response.read(1024 * 1024), b''):
            digest.update(block)
        return digest.hexdigest()


def assert_asset_matches(token, path, asset):
    if asset.get('state') != 'uploaded' or asset['size'] != path.stat().st_size:
        raise RuntimeError('Existing release asset differs or is incomplete: ' + path.name)
    if remote_asset_sha256(token, asset) != file_sha256(path):
        raise RuntimeError('Existing release asset SHA256 differs: ' + path.name)


def tag_commit(token, tag):
    try:
        reference = request(token, '/git/ref/tags/' + urllib.parse.quote(tag, safe=''))
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return None
        raise
    obj = reference['object']
    for _ in range(8):
        if obj['type'] == 'commit':
            return obj['sha']
        if obj['type'] != 'tag':
            break
        obj = request(token, '/git/tags/' + obj['sha'])['object']
    raise RuntimeError('Release tag does not resolve to a commit: ' + tag)


def resolve_target(token, tag, supplied):
    tagged = tag_commit(token, tag)
    if supplied:
        if not re.fullmatch(r'[0-9a-fA-F]{40}', supplied):
            raise ValueError('--target must be a full 40-character commit SHA.')
        target = supplied.lower()
        if request(token, '/commits/' + target)['sha'].lower() != target:
            raise RuntimeError('GitHub resolved a different release target commit.')
    elif tagged:
        target = tagged
    else:
        branch = request(token, '')['default_branch']
        target = request(token, '/commits/' + urllib.parse.quote(branch, safe=''))['sha']
    if tagged is not None and tagged.lower() != target.lower():
        raise RuntimeError('Existing tag points to a different commit; it will not be moved.')
    return target


def release_by_tag(token, tag):
    try:
        return request(token, '/releases/tags/' + urllib.parse.quote(tag, safe=''))
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return None
        raise


def publish(token, args):
    notes = args.notes.read_text(encoding='utf-8-sig')
    assets = [path.resolve(strict=True) for path in args.assets]
    if any(not path.is_file() for path in assets) or len({path.name for path in assets}) != len(assets):
        raise ValueError('Release assets must be files with unique basenames.')
    target = resolve_target(token, args.tag, args.target)
    name = args.name or 'Unity Gaussian Splat LOD ' + args.tag
    release = release_by_tag(token, args.tag)
    if release is not None and not release['draft']:
        if tag_commit(token, args.tag) is None:
            raise RuntimeError('Published release tag is missing; its source commit cannot be verified.')
        if release['body'] != notes or release['name'] != name or release['prerelease']:
            raise RuntimeError('Published release metadata differs; published releases will not be overwritten.')
        existing = {asset['name']: asset for asset in release['assets']}
        if set(existing) != {path.name for path in assets}:
            raise RuntimeError('Published release asset set differs; published releases will not be modified.')
        for path in assets:
            assert_asset_matches(token, path, existing[path.name])
        print('Already published and SHA256 verified:', release['html_url'], flush=True)
        return release
    if release is None:
        release = request(token, '/releases', {'tag_name': args.tag, 'target_commitish': target,
                          'name': name, 'body': notes, 'draft': True, 'prerelease': False})
    print('Prepared draft:', release['html_url'], 'target:', target, flush=True)
    existing = {asset['name']: asset for asset in release['assets']}
    if set(existing) - {path.name for path in assets}:
        raise RuntimeError('Draft contains unexpected assets; they will not be silently removed.')
    for path in assets:
        if path.name in existing:
            assert_asset_matches(token, path, existing[path.name])
            print('Already uploaded and SHA256 verified:', path.name, flush=True)
            continue
        endpoint = release['upload_url'].split('{')[0] + '?' + urllib.parse.urlencode({'name': path.name})
        asset = request(token, endpoint, path.read_bytes(), 'POST', 'application/octet-stream')
        assert_asset_matches(token, path, asset)
        print('Uploaded and SHA256 verified:', asset['name'], asset['size'], flush=True)
    tagged = tag_commit(token, args.tag)
    if tagged is not None and tagged.lower() != target.lower():
        raise RuntimeError('Release tag changed during upload; draft remains unpublished.')
    release = request(token, '/releases/' + str(release['id']),
                      {'draft': False, 'name': name, 'body': notes, 'target_commitish': target,
                       'prerelease': False, 'make_latest': 'true'}, 'PATCH')
    print('Published:', release['html_url'], flush=True)
    return release


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    commands = result.add_subparsers(dest='command', required=True)
    commands.add_parser('inspect', help='Inspect repository access without publishing.')
    publication = commands.add_parser('publish', help='Create a draft, verify attachments, then publish.')
    publication.add_argument('--tag', required=True)
    publication.add_argument('--target', help='Full commit SHA; defaults to existing tag or default-branch HEAD.')
    publication.add_argument('--name')
    publication.add_argument('--notes', type=pathlib.Path, required=True)
    publication.add_argument('assets', nargs='+', type=pathlib.Path)
    verification = commands.add_parser('verify', help='Read release metadata and asset digests.')
    verification.add_argument('--tag', required=True)
    return result


def main():
    args = parser().parse_args()
    if args.command in ('publish', 'verify') and not re.fullmatch(r'v[0-9]+\.[0-9]+(?:\.[0-9]+)?', args.tag):
        raise ValueError('--tag must be an explicit version tag such as v0.2.')
    token = authentication()
    if args.command == 'inspect':
        repo = request(token, '')
        print(json.dumps({key: repo.get(key) for key in ('html_url', 'default_branch', 'private', 'permissions')}, indent=2))
    elif args.command == 'publish':
        publish(token, args)
    else:
        release = release_by_tag(token, args.tag)
        if release is None:
            raise RuntimeError('Release not found: ' + args.tag)
        print(json.dumps({'url': release['html_url'], 'tag': release['tag_name'], 'draft': release['draft'],
                          'commit': tag_commit(token, args.tag),
                          'assets': [{key: asset.get(key) for key in
                                     ('name', 'size', 'state', 'digest', 'browser_download_url')}
                                    for asset in release['assets']]}, indent=2))


if __name__ == '__main__':
    try:
        main()
    except urllib.error.HTTPError as error:
        print('GitHub API error:', error.code, error.reason, file=sys.stderr)
        sys.exit(1)
    except Exception as error:
        print(type(error).__name__ + ': ' + str(error), file=sys.stderr)
        sys.exit(1)
