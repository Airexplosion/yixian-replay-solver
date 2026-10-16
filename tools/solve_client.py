"""Submit a replay or puzzle JSON, show named effects and stream solver progress."""
import argparse
import json
import pathlib
import sys
import time
from urllib.error import HTTPError
from urllib.request import Request, urlopen

parser = argparse.ArgumentParser()
source = parser.add_mutually_exclusive_group(required=True)
source.add_argument('--replay', help='Game replay share code')
source.add_argument('--input', type=pathlib.Path, help='Puzzle request JSON file')
parser.add_argument('--round', type=int)
parser.add_argument('--threads', type=int, default=0, help='0 = all available server cores')
parser.add_argument('--mode', choices=['exhaustive', 'existence', 'rank'], default='exhaustive')
parser.add_argument('--seconds', type=int, default=60, help='Only used by rank mode')
parser.add_argument('--first', choices=['me', 'foe', 'both', 'auto'], default='both')
parser.add_argument('--output', type=pathlib.Path)
parser.add_argument('--api', default='https://154.9.252.167:8443/api/v1')
args = parser.parse_args()
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')
next_request = 0


def call(path, method='GET', data=None):
    global next_request
    for attempt in range(3):
        time.sleep(max(0, next_request - time.monotonic()))
        next_request = time.monotonic() + 1.2
        body = None if data is None else json.dumps(data, ensure_ascii=False).encode('utf-8')
        request = Request(args.api.rstrip('/') + path, data=body, method=method,
                          headers={'Content-Type': 'application/json'})
        try:
            with urlopen(request, timeout=90) as response:
                return json.load(response)
        except HTTPError as error:
            if error.code == 429 and attempt < 2:
                continue
            raise RuntimeError(error.read().decode('utf-8', 'replace')) from error


if args.replay:
    replay = call('/replays/' + args.replay)
    selected = [item for item in replay['rounds'] if args.round is None or item['round'] == args.round]
    if not selected:
        raise SystemExit('复盘没有指定轮次')
    puzzle = selected[-1]['request']
    for side in ('me', 'foe'):
        print('我方' if side == 'me' else '对手')
        for kind in ('talents', 'fate', 'xianMoStrategies', 'keyin'):
            for item in puzzle[side]['details'].get(kind, []):
                print(' ', item['kind'], item['id'], item['name'], '：', item['description'])
else:
    puzzle = json.loads(args.input.read_text(encoding='utf-8-sig'))
puzzle.update(threads=args.threads, searchMode=args.mode, first=args.first, timeLimitMs=args.seconds * 1000)
started = call('/solve', 'POST', puzzle)
job = started['job']
print('任务', job, '使用', started['threads'], '/', started['cores'], '个服务端核心')
try:
    while True:
        report = call('/solve/' + job)
        print('状态', report['state'], '进度', str(round(report.get('progressPercent', 0), 2)) + '%',
              '已遍历', report.get('enumerated', 0), '/', report.get('candidates', 0),
              '已评估', report.get('evaluated', 0), '耗时', round(report.get('elapsedMs', 0) / 1000, 1), '秒')
        if report['state'] in ('done', 'cancelled', 'error'):
            break
        time.sleep(1)
except KeyboardInterrupt:
    call('/solve/' + job, 'DELETE')
    raise SystemExit('已发送取消请求；任务终态可继续用任务 ID 查询')
for index, entry in enumerate(report.get('best', []), 1):
    print(index, ' → '.join(item['name'] for item in entry.get('cardDetails', [])))
    print('  ', json.dumps(entry['orders'], ensure_ascii=False))
if args.output:
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
if report['state'] == 'error':
    raise SystemExit(report.get('error', '求解失败'))
