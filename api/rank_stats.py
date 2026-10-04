"""Compact ranked match statistics. Raw replay payloads are never written here."""
import contextlib
import datetime as dt
import hashlib
import json
import os
import pathlib
import re
import sqlite3
import time


def connect(store):
    store = pathlib.Path(store)
    store.mkdir(parents=True, exist_ok=True)
    db = sqlite3.connect(str(store / 'rank-stats.sqlite3'), timeout=20)
    db.row_factory = sqlite3.Row
    db.execute('PRAGMA journal_mode=WAL')
    db.executescript('''
        CREATE TABLE IF NOT EXISTS matches (
          code_id INTEGER NOT NULL, player_id TEXT NOT NULL, name TEXT NOT NULL,
          character_id INTEGER NOT NULL, begin_ts INTEGER NOT NULL, end_ts INTEGER NOT NULL,
          score_before INTEGER NOT NULL, score_delta INTEGER NOT NULL,
          random_character INTEGER NOT NULL, version TEXT NOT NULL,
          PRIMARY KEY(code_id, player_id));
        CREATE INDEX IF NOT EXISTS matches_time ON matches(end_ts, score_before);
        CREATE INDEX IF NOT EXISTS matches_player_time ON matches(player_id, end_ts);
        CREATE TABLE IF NOT EXISTS record_links (
          code_id INTEGER NOT NULL, player_id TEXT NOT NULL, lookup_rank INTEGER NOT NULL,
          PRIMARY KEY(code_id, player_id), CHECK(lookup_rank BETWEEN 0 AND 7));
        CREATE TABLE IF NOT EXISTS scan (
          code_id INTEGER PRIMARY KEY, status TEXT NOT NULL DEFAULT 'pending',
          next_rank INTEGER NOT NULL DEFAULT 0, attempts INTEGER NOT NULL DEFAULT 0,
          next_retry REAL NOT NULL DEFAULT 0, updated_at REAL NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS roster (
          code_id INTEGER NOT NULL, player_id TEXT NOT NULL, is_ai INTEGER NOT NULL,
          PRIMARY KEY(code_id,player_id));
        CREATE TABLE IF NOT EXISTS entries (
          code_id INTEGER NOT NULL, lookup_rank INTEGER NOT NULL, outcome TEXT NOT NULL,
          player_id TEXT, updated_at REAL NOT NULL,
          PRIMARY KEY(code_id,lookup_rank));
        CREATE TABLE IF NOT EXISTS code_info (
          code_id INTEGER PRIMARY KEY, game_mode INTEGER NOT NULL, begin_ts INTEGER);
    ''')
    return db


def meta(db, key, default=None):
    row = db.execute('SELECT value FROM meta WHERE key=?', (key,)).fetchone()
    return json.loads(row[0]) if row else default


def put_meta(db, key, value):
    db.execute('INSERT OR REPLACE INTO meta VALUES (?,?)', (key, json.dumps(value)))


def integer(value):
    if isinstance(value, bool) or not isinstance(value, int):
        raise ValueError('invalid integer')
    return value


def compact_match(data, expected_code=None):
    """Only root player's result is known; opponents' scores must never be inferred."""
    if not isinstance(data, dict) or data.get('gameMode') != 3:
        return None
    code = integer(data.get('codeId'))
    if code <= 0 or (expected_code is not None and code != expected_code):
        raise ValueError('record code mismatch')
    uid = data.get('uid')
    character = integer(data.get('charId'))
    begin = integer(data.get('beginTs'))
    end = integer(data.get('endTs'))
    before = integer(data.get('beginRankScore'))
    change = integer(data.get('diffRankScore'))
    if not isinstance(uid, str) or not uid or character <= 0 or not 0 < begin < end:
        raise ValueError('incomplete ranked record')
    if end - begin > 48 * 3600 * 1000:
        raise ValueError('invalid duration')
    observations = []
    for battle in data.get('roundStats') or []:
        for side in ('p1', 'p2'):
            public = (battle.get(side) or {}).get('publicData') or {}
            if public.get('uid') == uid:
                observations.append(public)
    if not observations or any(p.get('characterId') != character or p.get('isAI') for p in observations):
        raise ValueError('player identity could not be validated')
    # battleRank can differ for repeated UID responses; it is intentionally not used.
    return (code, hashlib.sha256(uid.encode()).hexdigest()[:20],
            str(observations[-1].get('username') or '未命名玩家')[:80], character,
            begin, end, before, change, int(bool(data.get('randomCharacter'))),
            str(data.get('version') or '')[:40])


def save_match(db, data, expected_code=None, lookup_rank=None):
    row = compact_match(data, expected_code)
    if row is None:
        return False
    # Repeated/retried share codes must not inflate counts or overwrite a result.
    existing = db.execute('SELECT character_id,begin_ts,end_ts,score_before,score_delta FROM matches WHERE code_id=? AND player_id=?', row[:2]).fetchone()
    if existing and tuple(existing) != (row[3], row[4], row[5], row[6], row[7]):
        raise ValueError('conflicting duplicate result')
    if lookup_rank is not None and (type(lookup_rank) is not int or not 0 <= lookup_rank <= 7):
        raise ValueError('invalid lookup entry')
    db.execute('INSERT OR IGNORE INTO matches VALUES (?,?,?,?,?,?,?,?,?,?)', row)
    if lookup_rank is not None:
        # Save the actual requested entry, never infer final placement from battleRank.
        db.execute('INSERT OR IGNORE INTO record_links VALUES (?,?,?)', (*row[:2], lookup_rank))
    save_roster(db, row[0], compact_roster(data))
    return not bool(existing)


def compact_roster(data):
    """Keep only hashed participant identities and bot flags, never replay snapshots."""
    players = {}
    for battle in data.get('roundStats') or []:
        for side in ('p1', 'p2'):
            public = (battle.get(side) or {}).get('publicData') or {}
            uid = public.get('uid')
            if isinstance(uid, str) and uid:
                key = hashlib.sha256(uid.encode()).hexdigest()[:20]
                players[key] = max(players.get(key, 0), int(bool(public.get('isAI'))))
    return players


def save_roster(db, code, players):
    db.executemany('''INSERT INTO roster VALUES (?,?,?) ON CONFLICT(code_id,player_id)
        DO UPDATE SET is_ai=MAX(roster.is_ai,excluded.is_ai)''',
        [(code, player, ai) for player, ai in players.items()])


def roster_complete(players, stored):
    return len(players) == 8 and all(ai or player in stored for player, ai in players.items())


def record_replay(store, data, lookup_rank=None):
    with contextlib.closing(connect(store)) as db:
        with db:
            return save_match(db, data, lookup_rank=lookup_rank)


def base36(number):
    result = ''
    while number:
        number, r = divmod(number, 36)
        result = '0123456789abcdefghijklmnopqrstuvwxyz'[r] + result
    return result


def share_code(code_id, lookup_rank):
    raw = str(code_id * 1000 + lookup_rank)
    return base36(int(raw[0] + raw[:0:-1]) ^ 0x5FF17843B6B1F)


def player_history(store, player_id, params):
    if not re.fullmatch(r'[a-f0-9]{20}', player_id):
        raise ValueError('玩家标识无效')
    def number(key, default, low, high):
        try:
            value = int(params.get(key, [str(default)])[0])
        except (TypeError, ValueError):
            raise ValueError('筛选参数无效')
        if not low <= value <= high:
            raise ValueError('筛选参数超出范围')
        return value
    now = int(time.time() * 1000)
    start = number('from', 0, 0, 4102444800000)
    end = number('to', now + 60000, 0, 4102444800000)
    low = number('minScore', 0, 0, 100000)
    high = number('maxScore', 100001, 1, 100001)
    character = number('character', 0, 0, 99999999)
    offset = number('offset', 0, 0, 10000000)
    limit = number('limit', 50, 1, 100)
    if start >= end or low >= high:
        raise ValueError('请检查时间或积分范围')
    where = 'm.player_id=? AND m.end_ts>=? AND m.end_ts<? AND m.score_before>=? AND m.score_before<?'
    values = [player_id, start, end, low, high]
    if character:
        where += ' AND m.character_id=?'
        values.append(character)
    with contextlib.closing(connect(store)) as db:
        db.execute('BEGIN')
        bounds = db.execute('SELECT COUNT(*),MIN(end_ts),MAX(end_ts) FROM matches WHERE player_id=?', (player_id,)).fetchone()
        name = db.execute('SELECT name FROM matches WHERE player_id=? ORDER BY end_ts DESC,code_id DESC LIMIT 1', (player_id,)).fetchone()
        summary = dict(db.execute('''SELECT COUNT(*) AS matches, SUM(score_delta) AS net,
            1.0*SUM(score_delta)/COUNT(*) AS average, SUM(end_ts-begin_ts)/60000.0 AS minutes,
            SUM(score_delta)*3600000.0/SUM(end_ts-begin_ts) AS hourly FROM matches m WHERE '''+where, values).fetchone())
        characters = [dict(r) for r in db.execute('''SELECT character_id AS id,COUNT(*) AS matches,
            SUM(score_delta) AS net,1.0*SUM(score_delta)/COUNT(*) AS average
            FROM matches m WHERE '''+where+' GROUP BY character_id ORDER BY matches DESC,character_id', values)]
        rows = db.execute('''SELECT m.*,r.lookup_rank FROM matches m LEFT JOIN record_links r
            ON m.code_id=r.code_id AND m.player_id=r.player_id WHERE '''+where+
            ' ORDER BY m.end_ts DESC,m.code_id DESC LIMIT ? OFFSET ?', values+[limit, offset])
        matches = []
        for row in rows:
            matches.append({'codeId': row['code_id'], 'characterId': row['character_id'],
                'beginTs': row['begin_ts'], 'endTs': row['end_ts'], 'scoreBefore': row['score_before'],
                'scoreChange': row['score_delta'], 'scoreAfter': row['score_before']+row['score_delta'],
                'minutes': (row['end_ts']-row['begin_ts'])/60000,
                'randomCharacter': bool(row['random_character']), 'version': row['version'],
                'replayCode': share_code(row['code_id'], row['lookup_rank']) if row['lookup_rank'] is not None else None})
        return {'player': {'id': player_id, 'name': name[0] if name else None,
                          'totalCollected': bounds[0], 'earliestEnd': bounds[1], 'latestEnd': bounds[2]},
                'filters': {'from': start, 'to': end, 'minScore': low, 'maxScore': high, 'character': character},
                'summary': summary, 'characters': characters, 'matches': matches,
                'offset': offset, 'limit': limit, 'generatedAt': now, 'coverage': coverage(db)}


def collect(store, fetch, start_code=None, max_calls=2400, seconds=480, workers=4):
    from rank_collector import collect as run
    return run(store, fetch, start_code, max_calls, seconds, workers)


def coverage(db):
    bounds = db.execute('SELECT MIN(end_ts),MAX(end_ts),COUNT(*),COUNT(DISTINCT code_id) FROM matches').fetchone()
    states = dict(db.execute('SELECT status,COUNT(*) FROM scan GROUP BY status').fetchall())
    audit = db.execute("""WITH r AS (
        SELECT r.code_id,COUNT(*) AS participants,
        SUM(CASE WHEN r.is_ai=0 AND m.player_id IS NULL THEN 1 ELSE 0 END) AS missing
        FROM roster r LEFT JOIN matches m ON r.code_id=m.code_id AND r.player_id=m.player_id
        GROUP BY r.code_id), g AS (SELECT DISTINCT code_id FROM matches)
        SELECT SUM(CASE WHEN participants=8 AND missing=0 THEN 1 ELSE 0 END),
        SUM(CASE WHEN COALESCE(participants,0)<>8 THEN 1 ELSE 0 END),
        SUM(COALESCE(missing,0)) FROM g LEFT JOIN r USING(code_id)""").fetchone()
    start = meta(db, 'backfillStart', meta(db, 'startCode'))
    observed = db.execute('SELECT MAX(code_id),MAX(begin_ts) FROM code_info').fetchone()
    end = max(observed[0] or 0, db.execute('SELECT MAX(code_id) FROM matches').fetchone()[0] or 0)
    checked = db.execute('SELECT COUNT(*) FROM scan WHERE code_id>=? AND code_id<=?', (start or 0, end)).fetchone()[0]
    gaps = max(0, end-start+1-checked) if start and end>=start else 0
    unavailable = db.execute("SELECT COUNT(*) FROM scan WHERE status='unavailable' AND code_id BETWEEN ? AND ?", (start or 0, end)).fetchone()[0]
    pending = db.execute("SELECT COUNT(*) FROM scan WHERE status='pending' AND code_id BETWEEN ? AND ?", (start or 0, end)).fetchone()[0]
    partial = db.execute("SELECT COUNT(*) FROM scan WHERE status='partial' AND code_id BETWEEN ? AND ?", (start or 0, end)).fetchone()[0]
    backfill = meta(db, 'backfillCursor')
    backfill_end = meta(db, 'backfillEnd')
    complete = bool(start and end>=start and not (gaps or unavailable or pending or partial or audit[1] or audit[2]))
    return {'earliestEnd': bounds[0], 'latestEnd': bounds[1], 'playerMatches': bounds[2], 'games': bounds[3],
            'verifiedGames': audit[0] or 0, 'rosterUnknownGames': audit[1] or 0,
            'missingPlayerMatches': audit[2] or 0, 'unscannedCodes': gaps,
            'unavailableCodes': unavailable, 'pendingCodes': pending, 'partialCodes': partial,
            'scanStartCode': start, 'scanThroughCode': end or None,
            'nextCode': meta(db, 'cursor'), 'latestObservedBegin': observed[1],
            'backfillNextCode': backfill, 'backfillRemaining': max(0,backfill_end-backfill) if backfill is not None and backfill_end else 0,
            'scannedCodes': sum(states.values()), 'states': states, 'lastRun': meta(db, 'lastRun'),
            'intervalSeconds': 600, 'complete': False, 'intervalComplete': complete,
            'scope': '按编号补查历史与新增对局，按参赛名单核验真人战绩；AI 不计缺口。覆盖仅限已检查编号范围，尚未证明全服或全赛季完整。'}


def query(store, params):
    def number(key, default, low, high):
        try:
            value = int(params.get(key, [str(default)])[0])
        except (TypeError, ValueError):
            raise ValueError('筛选参数无效')
        if not low <= value <= high:
            raise ValueError('筛选参数超出范围')
        return value
    now = int(time.time() * 1000)
    start = number('from', now - 7 * 86400000, 0, 4102444800000)
    end = number('to', now + 60000, 0, 4102444800000)
    low = number('minScore', 3000, 0, 100000)
    high = number('maxScore', 4000, 1, 100001)
    character = number('character', 0, 0, 99999999)
    minimum = number('minGames', 5, 1, 100000)
    offset = number('offset', 0, 0, 10000000)
    limit = number('limit', 50, 1, 100)
    search = str(params.get('q', [''])[0])[:80]
    sort = str(params.get('sort', ['count'])[0])
    sort_sql = {'count': 'matches DESC,net DESC', 'average': 'average DESC,matches DESC',
                'net': 'net DESC,matches DESC', 'hourly': 'hourly DESC,matches DESC'}.get(sort)
    if not sort_sql or start >= end or low >= high:
        raise ValueError('请检查时间、积分范围或排序选项')
    where = 'end_ts>=? AND end_ts<? AND score_before>=? AND score_before<?'
    values = [start, end, low, high]
    aggregate = '''COUNT(*) AS matches, SUM(score_delta) AS net,
        1.0*SUM(score_delta)/COUNT(*) AS average,
        SUM(end_ts-begin_ts)/60000.0 AS minutes,
        SUM(score_delta)*3600000.0/SUM(end_ts-begin_ts) AS hourly'''
    with contextlib.closing(connect(store)) as db:
        # Use one SQLite snapshot while the collector continues committing new rows.
        db.execute('BEGIN')
        status = coverage(db)
        total = dict(db.execute('SELECT '+aggregate+',COUNT(DISTINCT code_id) AS games,COUNT(DISTINCT player_id) AS players FROM matches WHERE '+where, values).fetchone())
        chars = [dict(r) for r in db.execute(
            'SELECT character_id AS id,'+aggregate+' FROM matches WHERE '+where+' GROUP BY character_id ORDER BY matches DESC,id', values)]
        for char in chars:
            char['share'] = char['matches'] / total['matches'] if total['matches'] else 0
            char['eligible'] = char['matches'] >= minimum
        pw = where + (' AND character_id=?' if character else '')
        pv = values + ([character] if character else [])
        if search:
            # Find players by any recorded alias, then count all their selected games.
            search_where, search_values = pw, list(pv)
            pw += " AND player_id IN (SELECT player_id FROM matches WHERE " + search_where + " AND name LIKE ? ESCAPE '\\')"
            pv.extend(search_values)
            pv.append('%'+search.replace('\\', '\\\\').replace('%', '\\%').replace('_', '\\_')+'%')
        count = db.execute('SELECT COUNT(DISTINCT player_id) FROM matches WHERE '+pw, pv).fetchone()[0]
        # A player's latest recorded name within this selection, not an arbitrary GROUP BY name.
        player_sql = ('WITH filtered AS (SELECT *,ROW_NUMBER() OVER(PARTITION BY player_id ORDER BY end_ts DESC,code_id DESC) AS rn FROM matches WHERE '+pw+') '
                      'SELECT player_id AS id,MAX(CASE WHEN rn=1 THEN name END) AS name,'+aggregate+
                      ' FROM filtered GROUP BY player_id ORDER BY '+sort_sql+',player_id LIMIT ? OFFSET ?')
        players = [dict(r) for r in db.execute(player_sql, pv+[limit, offset])]
        if players:
            ids = [p['id'] for p in players]
            role_rows = db.execute('SELECT player_id,character_id AS id,'+aggregate+' FROM matches WHERE '+pw+
                                  ' AND player_id IN ('+','.join('?' for _ in ids)+') GROUP BY player_id,character_id ORDER BY matches DESC,character_id', pv+ids)
            grouped = {}
            for r in role_rows:
                role = dict(r)
                grouped.setdefault(role.pop('player_id'), []).append(role)
            for p in players:
                p['characters'] = grouped.get(p['id'], [])
                p['eligible'] = p['matches'] >= minimum
        return {'filters': {'from': start, 'to': end, 'minScore': low, 'maxScore': high,
                            'character': character, 'minGames': minimum, 'sort': sort, 'q': search},
                'generatedAt': now, 'coverage': status, 'summary': total, 'characters': chars,
                'players': players, 'playerCount': count, 'offset': offset, 'limit': limit}
