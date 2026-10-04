import json
import sys
import tempfile
import hashlib
import contextlib
import unittest
from pathlib import Path
import rank_stats as stats


def record(code=10, uid='private-a', char=1000006, before=3500, delta=20, begin=1000000, minutes=10, mode=3):
    return {'codeId': code, 'gameMode': mode, 'uid': uid, 'charId': char,
            'beginTs': begin, 'endTs': begin + minutes * 60000,
            'beginRankScore': before, 'diffRankScore': delta, 'battleRank': 0,
            'roundStats': [{'p1': {'publicData': {'uid': uid, 'characterId': char, 'username': '测试玩家'}},
                            'p2': {'publicData': {'uid': 'opponent', 'characterId': 4000001}}}]}


class RankStatsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.store = Path(self.temp.name)

    def test_deduplicated_player_matches_and_weighted_rates(self):
        # Eighteen round snapshots are still one player-match. A repeated rank code is too.
        first = record(delta=30)
        first['roundStats'] *= 18
        self.assertTrue(stats.record_replay(self.store, first))
        first['battleRank'] = 7
        self.assertFalse(stats.record_replay(self.store, first))
        stats.record_replay(self.store, record(code=11, delta=-10, minutes=20))
        stats.record_replay(self.store, record(code=12, delta=0, minutes=10))
        stats.record_replay(self.store, record(code=13, before=4000, delta=100))
        self.assertFalse(stats.record_replay(self.store, record(code=14, mode=2)))
        result = stats.query(self.store, {'from': ['0'], 'to': ['99999999']})
        self.assertEqual(result['summary']['matches'], 3)
        self.assertEqual(result['summary']['games'], 3)
        self.assertEqual(result['summary']['players'], 1)
        self.assertEqual(result['summary']['net'], 20)
        self.assertAlmostEqual(result['summary']['average'], 20 / 3)
        self.assertAlmostEqual(result['summary']['hourly'], 30)
        self.assertFalse(result['characters'][0]['eligible'])
        self.assertNotIn('private-a', json.dumps(result))
        self.assertNotIn('roundStats', json.dumps(result))

    def test_settlement_time_tier_role_and_name_filters(self):
        stats.record_replay(self.store, record(delta=10))  # end=1,600,000
        stats.record_replay(self.store, record(code=11, uid='private-b', char=4000003, delta=-40, begin=1600000))
        bounds = {'from': ['1600000'], 'to': ['2200000'], 'minGames': ['1']}
        result = stats.query(self.store, bounds)
        self.assertEqual(result['summary']['matches'], 1)  # from inclusive, to exclusive
        self.assertEqual(result['summary']['net'], 10)
        role = stats.query(self.store, {'from': ['0'], 'to': ['9999999'], 'character': ['4000003'], 'q': ['测试']})
        self.assertEqual(len(role['characters']), 2)  # overview remains across roles
        self.assertEqual(role['playerCount'], 1)
        self.assertEqual(role['players'][0]['net'], -40)
        self.assertEqual(role['players'][0]['characters'][0]['id'], 4000003)
        self.assertEqual(stats.query(self.store, dict(bounds, q=['%']))['playerCount'], 0)

    def test_identity_and_conflicting_result_rejected(self):
        bad = record()
        bad['roundStats'][0]['p1']['publicData']['characterId'] = 123
        with self.assertRaises(ValueError):
            stats.record_replay(self.store, bad)
        stats.record_replay(self.store, record())
        with self.assertRaises(ValueError):
            stats.record_replay(self.store, record(delta=-200))
        with self.assertRaises(ValueError):
            stats.query(self.store, {'from': ['10'], 'to': ['9']})

    def test_search_alias_keeps_all_player_games_and_pagination(self):
        first = record()
        first['roundStats'][0]['p1']['publicData']['username'] = '旧昵称'
        stats.record_replay(self.store, first)
        second = record(code=11, begin=2000000)
        second['roundStats'][0]['p1']['publicData']['username'] = '新昵称'
        stats.record_replay(self.store, second)
        stats.record_replay(self.store, record(code=12, uid='private-b'))
        values = {'from': ['0'], 'to': ['9999999'], 'q': ['旧昵称']}
        result = stats.query(self.store, values)
        self.assertEqual(result['playerCount'], 1)
        self.assertEqual(result['players'][0]['matches'], 2)
        self.assertEqual(result['players'][0]['name'], '新昵称')
        page = stats.query(self.store, dict(values, q=[''], limit=['1'], offset=['1']))
        self.assertEqual(page['playerCount'], 2)
        self.assertEqual(len(page['players']), 1)
        self.assertEqual(page['players'][0]['matches'], 1)

    def test_player_history_isolates_identity_and_filters_pagination(self):
        stats.record_replay(self.store, record(code=10, delta=-10), lookup_rank=5)
        stats.record_replay(self.store, record(code=11, char=4000003, begin=2000000, delta=30))
        stats.record_replay(self.store, record(code=12, uid='another-player', delta=200))
        player = hashlib.sha256(b'private-a').hexdigest()[:20]
        result = stats.player_history(self.store, player, {'to': ['9999999'], 'limit': ['1']})
        self.assertEqual(result['player']['totalCollected'], 2)
        self.assertEqual(result['summary']['net'], 20)
        self.assertEqual(result['matches'][0]['codeId'], 11)
        self.assertIsNone(result['matches'][0]['replayCode'])
        page = stats.player_history(self.store, player, {'to': ['9999999'], 'limit': ['1'], 'offset': ['1']})
        self.assertEqual(page['matches'][0]['scoreAfter'], 3490)
        from replay_service import decode_code
        plain, selected = decode_code(page['matches'][0]['replayCode'])
        self.assertEqual(int(plain, 36), 10005)
        self.assertEqual(selected, 0)
        filtered = stats.player_history(self.store, player, {'from': ['1600000'], 'to': ['2600000'], 'character': ['1000006']})
        self.assertEqual(filtered['summary']['matches'], 1)
        self.assertNotIn('private-a', json.dumps(result))
        self.assertNotIn('another-player', json.dumps(result))
        self.assertNotIn('roundStats', json.dumps(result))

    def test_lookup_entry_can_be_backfilled_without_overwriting_result(self):
        stats.record_replay(self.store, record())
        stats.record_replay(self.store, record(), lookup_rank=3)
        stats.record_replay(self.store, record(), lookup_rank=7)
        with contextlib.closing(stats.connect(self.store)) as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM matches').fetchone()[0], 1)
            self.assertEqual(db.execute('SELECT lookup_rank FROM record_links').fetchone()[0], 3)
        empty = stats.player_history(self.store, '0'*20, {})
        self.assertEqual(empty['summary']['matches'], 0)
        with self.assertRaises(ValueError):
            stats.player_history(self.store, 'private-a', {})

    @unittest.skipIf(sys.platform == 'win32', 'Linux collector lock')
    def test_collector_resumes_without_counting_repeated_player(self):
        calls = []
        def fetch(path, params):
            raw = int(params['code'], 36)
            code, rank = divmod(raw, 1000)
            calls.append((code, rank))
            return record(code=code, uid='private-' + str(min(rank, 6)))
        stats.collect(self.store, fetch, start_code=10, max_calls=3)
        stats.collect(self.store, fetch, start_code=999, max_calls=5)
        self.assertEqual(calls, [(10, i) for i in range(8)])
        with stats.connect(self.store) as db:
            self.assertEqual(stats.meta(db, 'cursor'), 11)
            self.assertEqual(db.execute('SELECT COUNT(*) FROM matches').fetchone()[0], 7)
            self.assertEqual(db.execute('SELECT status FROM scan').fetchone()[0], 'partial')

    @unittest.skipIf(sys.platform == 'win32', 'Linux collector lock')
    def test_collector_stops_on_service_error_keeps_cursor(self):
        def unavailable(path, params):
            raise RuntimeError('secret upstream message')
        run = stats.collect(self.store, unavailable, start_code=10)
        self.assertEqual(run['status'], 'error')
        self.assertNotIn('secret', json.dumps(run))
        with stats.connect(self.store) as db:
            self.assertEqual(stats.meta(db, 'cursor'), 10)


if __name__ == '__main__':
    unittest.main()
