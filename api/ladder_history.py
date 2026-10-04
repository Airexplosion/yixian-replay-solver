"""Compare archived public leaderboard snapshots inside a requested interval."""
import datetime as dt
import json
import pathlib


def timestamp(value):
    return int(dt.datetime.fromisoformat(value.replace('Z', '+00:00')).timestamp()*1000)


def snapshots(directory):
    directory = pathlib.Path(directory)
    items = {}
    for path in directory.glob('*Z.json'):
        try:
            ms = int(dt.datetime.strptime(path.stem, '%Y%m%dT%H%M%SZ').replace(tzinfo=dt.timezone.utc).timestamp()*1000)
        except ValueError:
            continue
        items[ms] = path
    latest = directory/'latest.json'
    if latest.exists():
        # Filename precision is seconds, while generatedAt also contains milliseconds.
        ms = timestamp(json.loads(latest.read_text())['generatedAt'])//1000*1000
        items.setdefault(ms, latest)
    return sorted(items.items())


def compare(directory, season, params):
    def bound(key):
        try:
            value = int(params[key][0])
        except (KeyError, TypeError, ValueError):
            raise ValueError('请填写有效的起止时间')
        if not 0 <= value <= 4102444800000:
            raise ValueError('时间超出范围')
        return value
    start, end = bound('from'), bound('to')
    if start >= end:
        raise ValueError('结束时间必须晚于开始时间')
    indexed = snapshots(directory)
    if not indexed:
        raise ValueError('这个赛季还没有采集快照')
    # Narrow by second-granularity filenames, then enforce exact generatedAt bounds.
    selected = [(ms,p) for ms,p in indexed if start//1000*1000 <= ms <= end]
    # Only the boundary payloads need loading; check exact, unrounded timestamps.
    baseline = endpoint = None
    while selected:
        value = json.loads(selected[0][1].read_text())
        if start <= timestamp(value['generatedAt']) <= end:
            baseline = value
            break
        selected.pop(0)
    while selected:
        value = json.loads(selected[-1][1].read_text())
        if start <= timestamp(value['generatedAt']) <= end:
            endpoint = value
            break
        selected.pop()
    count = len(selected) if baseline and endpoint else 0
    first = json.loads(indexed[0][1].read_text())['generatedAt']
    last = json.loads(indexed[-1][1].read_text())['generatedAt']
    comparable = count >= 2 and timestamp(baseline['generatedAt']) < timestamp(endpoint['generatedAt'])
    before = {p['id']:p for p in baseline['players']} if comparable else {}
    after = {p['id']:p for p in endpoint['players']} if endpoint else {}
    players = []
    for key in before.keys() | after.keys():
        a,b = before.get(key),after.get(key)
        players.append({'id':key,'name':(b or a)['name'],
            'rank': b['rank'] if b else None, 'score':b['score'] if b else None,
            'startRank':a['rank'] if a else None,'startScore':a['score'] if a else None,
            'delta':b['score']-a['score'] if a and b else None,
            'rankDelta':a['rank']-b['rank'] if a and b else None,
            'status':('both' if a and b else 'entered' if b else 'left') if comparable else 'uncompared'})
    players.sort(key=lambda p:(p['rank'] is None,p['rank'] or p['startRank'],p['id']))
    return {'seasonId':season,'players':players,'count':len(after),
        'generatedAt': endpoint['generatedAt'] if endpoint else None,
        'baselineAt':baseline['generatedAt'] if comparable else None,
        'intervalSeconds':600,'coverage':'官方天梯前 100 名的历史快照',
        'comparison':{'requestedFrom':start,'requestedTo':end,'snapshotCount':count,
            'availableFrom':first,'availableTo':last,'canCompare':comparable,
            'startTruncated':start < timestamp(first), 'endTruncated':end > timestamp(last),
            'matched':sum(p['status']=='both' for p in players),
            'entered':sum(p['status']=='entered' for p in players),
            'left':sum(p['status']=='left' for p in players)}}
