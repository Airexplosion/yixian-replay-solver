"""Concurrent, resumable code scanning. Workers return compact facts only."""
import concurrent.futures
import contextlib
import pathlib
import threading
import time
import rank_stats as stats


def scan_game(code, fetch, seed, reserve, stop):
    players, stored, entries = seed
    players, stored, entries = dict(players), set(stored), dict(entries)
    result = {'code': code, 'rows': [], 'roster': players, 'entries': [],
              'status': 'pending', 'calls': 0, 'info': None, 'error': False}
    if stats.roster_complete(players, stored):
        result['status'] = 'scanned'
        return result
    # Existing successful entries need no download. Recheck duplicate/missing entries.
    ranks = [r for r in range(8) if entries.get(r) != 'valid']
    if not ranks:
        ranks = list(range(8))
    for rank in ranks:
        if stop.is_set() or not reserve():
            return result
        result['calls'] += 1
        try:
            data = fetch('/gameStat/fetchPlayerBattleInfo', {'code': stats.base36(code * 1000 + rank)})
            if data is None:
                result['entries'].append((rank, 'unavailable', None))
                if rank == 0 and not stored and entries.get(0) != 'unavailable':
                    result['status'] = 'unavailable'
                    return result
                continue
            if not isinstance(data, dict) or data.get('codeId') != code:
                raise ValueError('record mismatch')
            if data.get('gameMode') != 3:
                if stored:
                    raise ValueError('mode mismatch')
                result['info'] = (code, data['gameMode'], data.get('beginTs'))
                result['status'] = 'nonrank'
                return result
            result['info'] = (code, 3, data.get('beginTs'))
            for player, ai in stats.compact_roster(data).items():
                players[player] = max(players.get(player, 0), ai)
            try:
                row = stats.compact_match(data, code)
            except ValueError:
                row = None
            if row is None:
                result['entries'].append((rank, 'invalid', None))
            else:
                duplicate = row[1] in stored
                stored.add(row[1])
                result['rows'].append((row, rank))
                result['entries'].append((rank, 'duplicate' if duplicate else 'valid', row[1]))
            data = None
            if stats.roster_complete(players, stored):
                result['status'] = 'scanned'
                return result
        except Exception:
            # No raw IDs, credentials or upstream error text in metadata.
            stop.set()
            result['error'] = True
            return result
    result['status'] = 'partial' if stored or result['info'] else 'unavailable'
    return result


def collect(store, fetch, start_code=None, max_calls=2400, seconds=480, workers=4):
    import fcntl
    store = pathlib.Path(store)
    store.mkdir(parents=True, exist_ok=True)
    workers = max(1, min(8, int(workers)))
    with (store / 'rank-collector.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return {'status': 'already-running'}
        with contextlib.closing(stats.connect(store)) as db:
            cursor = stats.meta(db, 'cursor')
            if cursor is None:
                if not start_code or start_code <= 0:
                    raise ValueError('Set a verified starting record')
                cursor = start_code
                with db:
                    stats.put_meta(db, 'cursor', cursor)
                    stats.put_meta(db, 'startCode', cursor)
            start = stats.meta(db, 'startCode', cursor)
            if stats.meta(db, 'rosterAuditVersion') != 1:
                with db:
                    db.execute("UPDATE scan SET status='partial',next_retry=0 WHERE status='scanned' AND code_id NOT IN (SELECT code_id FROM roster GROUP BY code_id HAVING COUNT(*)=8)")
                    stats.put_meta(db, 'rosterAuditVersion', 1)
            earliest = db.execute('SELECT MIN(code_id) FROM matches').fetchone()[0]
            backfill = stats.meta(db, 'backfillCursor')
            if earliest is not None and earliest < start and backfill is None:
                backfill = earliest
                with db:
                    stats.put_meta(db, 'backfillStart', earliest)
                    stats.put_meta(db, 'backfillCursor', earliest)
                    stats.put_meta(db, 'backfillEnd', start)
            started = time.time()
            deadline = time.monotonic() + seconds
            stop, budget_lock = threading.Event(), threading.Lock()
            reserved = 0

            def reserve():
                nonlocal reserved
                with budget_lock:
                    if reserved >= max_calls or time.monotonic() >= deadline or stop.is_set():
                        return False
                    reserved += 1
                    return True

            run = {'startedAt': int(started * 1000), 'finishedAt': None, 'calls': 0,
                   'added': 0, 'codes': 0, 'status': 'running', 'error': None,
                   'workers': workers, 'requestsPerSecond': 0, 'backfillCodes': 0, 'retryCodes': 0}
            # Repair interrupted jobs too, including pending jobs behind the forward cursor.
            retry_rows = list(db.execute('''SELECT code_id,status FROM scan
                WHERE status IN ('pending','partial','unavailable') AND next_retry<=?
                AND code_id<? ORDER BY CASE status WHEN 'pending' THEN 0 WHEN 'partial' THEN 1 ELSE 2 END,
                next_retry,code_id LIMIT 2000''', (started, cursor)))
            interrupted = [r[0] for r in retry_rows if r[1]=='pending']
            retries = [r[0] for r in retry_rows if r[1]!='pending']
            retry_index = 0
            active, handled = {}, set()
            turn = 0
            forward_paused = False

            def choose():
                nonlocal cursor, backfill, retry_index, turn
                # 60% forward discovery, 20% player/code repairs, 20% older code backfill.
                for _ in range(10000):
                    if interrupted:
                        lane, code = 'retry', interrupted.pop(0)
                    else:
                        lane = ('forward', 'forward', 'retry', 'forward', 'backfill')[turn % 5]
                        turn += 1
                        code = None
                    if code is not None:
                        pass
                    elif lane == 'retry':
                        if retry_index >= len(retries):
                            continue
                        code = retries[retry_index]
                        retry_index += 1
                    elif lane == 'backfill':
                        if backfill is None or backfill >= start:
                            continue
                        code = backfill
                        backfill += 1
                        stats.put_meta(db, 'backfillCursor', backfill)
                    else:
                        if forward_paused:
                            continue
                        code = cursor
                        cursor += 1
                        stats.put_meta(db, 'cursor', cursor)
                    if code in handled or code in active:
                        continue
                    prior = db.execute('SELECT * FROM scan WHERE code_id=?', (code,)).fetchone()
                    if prior and prior['status'] in ('scanned', 'nonrank'):
                        continue
                    # Persist the work before dispatch. A killed process leaves a repairable job.
                    db.execute('INSERT OR IGNORE INTO scan(code_id) VALUES (?)', (code,))
                    players = dict(db.execute('SELECT player_id,is_ai FROM roster WHERE code_id=?', (code,)))
                    stored = {r[0] for r in db.execute('SELECT player_id FROM matches WHERE code_id=?', (code,))}
                    entries = dict(db.execute('SELECT lookup_rank,outcome FROM entries WHERE code_id=?', (code,)))
                    for r in db.execute('SELECT lookup_rank FROM record_links WHERE code_id=?', (code,)):
                        entries.setdefault(r[0], 'valid')
                    db.commit()
                    active[code] = lane
                    return code, (players, stored, entries)
                db.commit()
                return None

            def save(result):
                nonlocal forward_paused, cursor
                code = result['code']
                lane = active.pop(code)
                handled.add(code)
                status = result['status']
                added = 0
                with db:
                    stats.save_roster(db, code, result['roster'])
                    for row, rank in result['rows']:
                        old = db.execute('SELECT character_id,begin_ts,end_ts,score_before,score_delta FROM matches WHERE code_id=? AND player_id=?', row[:2]).fetchone()
                        if old and tuple(old) != tuple(row[3:8]):
                            status = 'partial'
                            db.execute('DELETE FROM entries WHERE code_id=? AND lookup_rank=?', (code, rank))
                            result['entries'] = [e for e in result['entries'] if e[0] != rank]
                            continue
                        added += int(not old)
                        db.execute('INSERT OR IGNORE INTO matches VALUES (?,?,?,?,?,?,?,?,?,?)', row)
                        db.execute('INSERT OR IGNORE INTO record_links VALUES (?,?,?)', (*row[:2], rank))
                    db.executemany('INSERT OR REPLACE INTO entries VALUES (?,?,?,?,?)',
                                   [(code, r, outcome, player, time.time()) for r, outcome, player in result['entries']])
                    if result['info']:
                        db.execute('INSERT OR REPLACE INTO code_info VALUES (?,?,?)', result['info'])
                    prior = db.execute('SELECT attempts FROM scan WHERE code_id=?', (code,)).fetchone()[0]
                    delay = 0 if status == 'pending' else min(86400, 600 * 2 ** min(prior, 7))
                    db.execute('UPDATE scan SET status=?,next_rank=0,attempts=attempts+1,next_retry=?,updated_at=? WHERE code_id=?',
                               (status, time.time()+delay, time.time(), code))
                    run['calls'] += result['calls']
                    run['added'] += added
                    run['codes'] += 1
                    run['backfillCodes'] += int(lane == 'backfill')
                    run['retryCodes'] += int(lane == 'retry')
                    run['requestsPerSecond'] = round(run['calls']/max(0.01, time.time()-started), 2)
                    if result['error']:
                        run['status'] = 'error'
                        run['error'] = '上游取数失败，本轮停止新请求；保存缺口后下轮继续。'
                    stats.put_meta(db, 'lastRun', run)
                if lane == 'forward' and status == 'unavailable':
                    recent = db.execute('SELECT code_id,status FROM scan WHERE code_id<=? ORDER BY code_id DESC LIMIT 12', (code,)).fetchall()
                    if len(recent) == 12 and recent[-1]['code_id'] == code-11 and all(r['status']=='unavailable' for r in recent):
                        # Revisit this tentative boundary next run; do not march into future IDs.
                        forward_paused = True
                        cursor = min(cursor, code-11)
                        with db:
                            stats.put_meta(db, 'cursor', cursor)

            with db:
                stats.put_meta(db, 'lastRun', run)
            try:
                with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as pool:
                    futures = {}
                    while True:
                        while len(futures) < workers and not stop.is_set() and reserved < max_calls and time.monotonic() < deadline:
                            task = choose()
                            if task is None:
                                break
                            code, seed = task
                            futures[pool.submit(scan_game, code, fetch, seed, reserve, stop)] = code
                        if not futures:
                            break
                        done, _ = concurrent.futures.wait(futures, return_when=concurrent.futures.FIRST_COMPLETED)
                        for future in done:
                            futures.pop(future)
                            save(future.result())
                if run['status'] != 'error':
                    run['status'] = 'ok'
            except Exception:
                stop.set()
                run['status'] = 'error'
                run['error'] = '采集暂停，本轮未完成编号将在下轮补采。'
            finally:
                run['finishedAt'] = int(time.time()*1000)
                with db:
                    stats.put_meta(db, 'lastRun', run)
            return run
