"""Names and full effect descriptions with explicit, separate ID namespaces."""
import functools
import json
import os
import pathlib


def data_directory():
    return pathlib.Path(os.environ.get('YX_SOLVER_DATA', '/root/yx-replay/solver/data'))


@functools.lru_cache(maxsize=1)
def load():
    return json.loads((data_directory() / 'catalog.json').read_text(encoding='utf-8-sig'))


@functools.lru_cache(maxsize=1)
def index():
    return {kind: {row['id']: row for row in rows} for kind, rows in load()['kinds'].items()}


def lookup(kind, identifier):
    if kind not in index():
        raise ValueError('未知编号类型')
    row = index()[kind].get(identifier)
    return dict(row) if row is not None else {'id': identifier, 'kind': kind, 'known': False,
        'name': '未收录的编号 ' + str(identifier), 'description': '', 'unresolvedFields': []}


def query(params):
    data = load()
    kind = params.get('kind', [''])[0]
    if not kind:
        return {'version': data['version'], 'gameRecordVersion': data['gameRecordVersion'],
                'configVersion': data['configVersion'], 'kinds': {key: len(rows) for key, rows in data['kinds'].items()}}
    if kind not in data['kinds']:
        raise ValueError('未知编号类型')
    offset = int(params.get('offset', ['0'])[0])
    limit = int(params.get('limit', ['50'])[0])
    if not 0 <= offset <= 100000 or not 1 <= limit <= 100:
        raise ValueError('分页参数超出范围')
    q = params.get('q', [''])[0][:100].casefold()
    rows = [row for row in data['kinds'][kind]
            if not q or q in (str(row['id']) + row['name'] + row['description']).casefold()]
    return {'version': data['version'], 'kind': kind, 'total': len(rows),
            'offset': offset, 'limit': limit, 'items': rows[offset:offset + limit]}


def side_details(side):
    fields = {'talents': 'talent', 'fate': 'fate', 'xianMoStrategies': 'xianmo', 'keyin': 'keyin',
              'cards': 'card', 'handCards': 'card'}
    result = {field: [lookup(kind, identifier) for identifier in side.get(field, [])]
              for field, kind in fields.items()}
    result['resonance'] = lookup('resonance', side['resonance']) if side.get('resonance') else None
    result['character'] = lookup('character', side.get('characterId', 0))
    result['permanentBuffs'] = [dict(lookup('buff', int(key)), value=value)
                              for key, value in side.get('permanentBuffs', {}).items()]
    return result


def decorate_replay(replay):
    for round_data in replay['rounds']:
        for side in ('me', 'foe'):
            data = round_data['request'][side]
            data['details'] = side_details(data)
    replay['catalogVersion'] = load()['version']
    return replay
