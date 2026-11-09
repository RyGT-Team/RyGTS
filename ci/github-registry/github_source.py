"""Wait for an exact revision to be available from GitHub."""
import json
import re
import time
import urllib.error
import urllib.request

def wait_for_commit(api, commit, attempts=120):
    if not re.fullmatch(r'[0-9a-f]{40}', commit or ''):
        raise ValueError('Expected a full lowercase Git commit SHA')
    for attempt in range(attempts):
        try:
            with urllib.request.urlopen(api + '/commits/' + commit, timeout=30) as response:
                actual = json.load(response)['sha']
            if actual != commit:
                raise ValueError('GitHub returned a different commit')
            return commit
        except urllib.error.HTTPError as error:
            if error.code != 404:
                raise
            if attempt + 1 == attempts:
                raise RuntimeError('GitHub did not provide the requested revision') from None
            time.sleep(5)
