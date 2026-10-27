"""Publish explicitly requested release assets using Git Credential Manager, without logging credentials."""
import json, os, pathlib, subprocess, sys, urllib.request, urllib.error

REPOSITORY = 'Hakutyan-bai/Unity-GaussianLOD'

def authentication():
    env = dict(os.environ, GCM_INTERACTIVE='never', GIT_TERMINAL_PROMPT='0')
    result = subprocess.run(['git', 'credential', 'fill'], input='protocol=https\nhost=github.com\npath='+REPOSITORY+'.git\n\n',
                            capture_output=True, text=True, env=env, timeout=30)
    credentials = dict(line.split('=', 1) for line in result.stdout.splitlines() if '=' in line)
    if result.returncode or not credentials.get('password'):
        raise RuntimeError('GitHub credentials unavailable from Git Credential Manager; authenticate GitHub locally first.')
    return credentials['password']

def request(token, endpoint, data=None, method=None, content_type='application/json'):
    url = endpoint if endpoint.startswith('https://') else 'https://api.github.com/repos/'+REPOSITORY+endpoint
    payload = json.dumps(data).encode() if isinstance(data, dict) else data
    headers = {'Authorization':'Bearer '+token, 'Accept':'application/vnd.github+json',
               'X-GitHub-Api-Version':'2022-11-28', 'User-Agent':'Unity-GaussianLOD-release', 'Content-Type':content_type}
    with urllib.request.urlopen(urllib.request.Request(url, data=payload, headers=headers, method=method), timeout=180) as response:
        return json.load(response)

def main():
    token = authentication()
    if sys.argv[1] == 'inspect':
        repo = request(token, '')
        print(json.dumps({k:repo.get(k) for k in ('html_url','default_branch','private','permissions')}, indent=2))
        return
    if sys.argv[1] == 'publish':
        notes = pathlib.Path(sys.argv[2]).read_text(encoding='utf-8')
        try: release = request(token, '/releases/tags/v0.1')
        except urllib.error.HTTPError as e:
            if e.code != 404: raise
            release = request(token, '/releases', {'tag_name':'v0.1','name':'Unity Gaussian Splat LOD v0.1',
                              'body':notes,'draft':True,'prerelease':False})
        print('Prepared release:', release['html_url'], flush=True)
        from urllib.parse import urlencode
        for filename in sys.argv[3:]:
            path = pathlib.Path(filename)
            existing = next((a for a in release['assets'] if a['name'] == path.name),None)
            if existing:
                if existing['size'] != path.stat().st_size: raise RuntimeError('Existing release asset differs: '+path.name)
                print('Already uploaded:',path.name,flush=True);continue
            asset = request(token, release['upload_url'].split('{')[0]+'?'+urlencode({'name':path.name}),
                            path.read_bytes(), 'POST', 'application/octet-stream')
            print('Uploaded:',asset['name'],asset['size'],asset['browser_download_url'],flush=True)
        release=request(token,'/releases/'+str(release['id']),{'draft':False,'body':notes,'make_latest':'true'},'PATCH')
        print('Published:',release['html_url'],flush=True)
        return
    if sys.argv[1] == 'verify':
        release = request(token, '/releases/tags/v0.1')
        print(json.dumps({'url':release['html_url'],'tag':release['tag_name'],'draft':release['draft'],
                          'assets':[{k:a[k] for k in ('name','size','state','browser_download_url')} for a in release['assets']]},indent=2))

if __name__ == '__main__':
    try: main()
    except urllib.error.HTTPError as e:
        print('GitHub API error:',e.code,e.reason,file=sys.stderr);sys.exit(1)
    except Exception as e:
        print(type(e).__name__+': '+str(e),file=sys.stderr);sys.exit(1)
