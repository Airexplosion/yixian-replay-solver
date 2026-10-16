import unittest
import tempfile
import json
import time
import threading
from http.client import HTTPConnection
from http.server import ThreadingHTTPServer
from pathlib import Path
from unittest.mock import patch
import replay_service
from replay_service import decode_code,normalize_replay

class ReplayTests(unittest.TestCase):
    def test_request_reservations_are_spaced_without_holding_network_lock(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(replay_service,'STORE',Path(temp)):
            first = replay_service.reserve_upstream_slot()
            second = replay_service.reserve_upstream_slot()
            self.assertGreaterEqual(second-first,0.19)
            self.assertTrue(replay_service.UPSTREAM_LOCK.acquire(blocking=False))
            replay_service.UPSTREAM_LOCK.release()
            replay_service.upstream_cooldown(30)
            self.assertGreaterEqual(replay_service.reserve_upstream_slot(),time.time()+29)
            self.assertIn('cooldown',json.loads((Path(temp)/'upstream.lock').read_text()))

    def test_real_replay_code(self):
        plain,selected=decode_code('gma6hs2pc2')
        self.assertEqual(int(plain,36),33524279140)
        self.assertEqual(selected,14)

    def test_snapshot_and_score_metadata(self):
        snapshot={'usedCards':[3,8,15,36],'handCards':[47],'unlockGrids':4,'talents':[125],
                  'life':40,'extraMaxHp':5,'level':2,'permanentBuffTempDatas':{'10049':1}}
        player={'publicData':{'uid':'private-user-id','username':'private-name','talents':[999],
                             'lastRoundData':snapshot},'privateData':{'xianMoData':{'tempDatas':{'1001':3}}}}
        foe={'publicData':{'uid':'other-user-id','lastRoundData':dict(snapshot)},'privateData':{}}
        result=normalize_replay({'uid':'private-user-id','beginRankScore':3500,'diffRankScore':-12,
            'roundStats':[{'round':4,'p1':player,'p2':foe,'firstPlayerId':'other-user-id'}]})
        req=result['rounds'][0]['request']
        self.assertEqual(req['me']['talents'],[125])
        self.assertEqual(req['me']['cards'],[3,8,15,36,47])
        self.assertEqual(req['slots'],4)
        self.assertEqual(req['first'],'foe')
        self.assertEqual(req['me']['xianMoTempDatas'],{'1001':3})
        self.assertEqual(req['maxConsumeSustain'],3)
        self.assertEqual(result['match']['rankAfter'],3488)
        self.assertNotIn('private-user-id',str(result))
        self.assertNotIn('private-name',str(result))

    def test_missing_snapshot_rejected(self):
        player={'publicData':{'uid':'a','usedCards':[3]}}
        with self.assertRaises(ValueError):
            normalize_replay({'roundStats':[{'p1':player,'p2':player,'round':1}]})

class ApiHttpTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store = patch.object(replay_service, 'STORE', Path(self.temp.name))
        self.rate = patch.object(replay_service, 'RATE', {})
        self.store.start()
        self.rate.start()
        self.server = ThreadingHTTPServer(('127.0.0.1', 0), replay_service.Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=3)
        self.rate.stop()
        self.store.stop()
        self.temp.cleanup()

    def get(self, path, headers=None):
        connection = HTTPConnection(*self.server.server_address, timeout=5)
        try:
            connection.request('GET', path, headers=headers or {})
            response = connection.getresponse()
            return response.status, json.loads(response.read())
        finally:
            connection.close()

    def test_api_directory_is_available_without_browser(self):
        with patch.object(replay_service, 'upstream', side_effect=AssertionError('unexpected upstream')):
            status, data = self.get('/api/v1')
        self.assertEqual(status, 200)
        self.assertEqual(data['mode'], 'api-only')
        self.assertTrue(data['solver']['available'])
        self.assertIn('/api/v1/collection', [item['path'] for item in data['endpoints']])

    def test_collection_status_uses_stored_data_only(self):
        with patch.object(replay_service, 'upstream', side_effect=AssertionError('unexpected upstream')):
            status, data = self.get('/api/v1/collection')
        self.assertEqual(status, 200)
        self.assertEqual(data['coverage']['playerMatches'], 0)
        self.assertFalse(data['coverage']['complete'])
        self.assertIsInstance(data['generatedAt'], int)

    def test_new_endpoints_observe_rate_limit(self):
        self.assertEqual(self.get('/api/v1/')[0], 200)
        self.assertEqual(self.get('/api/v1/collection')[0], 429)

    def test_new_endpoints_observe_origin_allowlist(self):
        self.assertEqual(self.get('/api/v1', {'Origin': 'https://untrusted.example'})[0], 403)

    def request(self, method, path, body=None):
        connection = HTTPConnection(*self.server.server_address, timeout=5)
        try:
            connection.request(method, path, body=body, headers={'Content-Type': 'application/json'})
            response = connection.getresponse()
            return response.status, json.loads(response.read())
        finally:
            connection.close()

    def test_submit_returns_async_job_and_preserves_request(self):
        request = {'threads': 4, 'searchMode': 'exhaustive', 'me': {'cards': [2]}, 'foe': {'cards': [3]}}
        with patch.object(replay_service.solver_jobs.JOBS, 'start', return_value={'job': 'test', 'threads': 4}) as start:
            status, data = self.request('POST', '/api/v1/solve', json.dumps(request))
        self.assertEqual(status, 202)
        self.assertEqual(data['threads'], 4)
        start.assert_called_once_with(request)

    def test_submit_bad_json_is_a_client_error(self):
        self.assertEqual(self.request('POST', '/api/v1/solve', '{')[0], 400)

    def test_busy_solver_is_reported_as_conflict(self):
        with patch.object(replay_service.solver_jobs.JOBS, 'start', side_effect=replay_service.solver_jobs.BusyError('busy')):
            self.assertEqual(self.request('POST', '/api/v1/solve', '{}')[0], 409)

    def test_unknown_job_is_not_found(self):
        self.assertEqual(self.request('DELETE', '/api/v1/solve/' + 'a' * 32)[0], 404)

if __name__=='__main__':unittest.main()
