"""Real process integration tests use invented config data, never game assets."""
import json
import os
import pathlib
import tempfile
import time
import unittest
from unittest.mock import patch

import metadata_catalog
import solver_jobs


@unittest.skipUnless(os.environ.get('YX_SOLVER_TEST_COMMAND'), 'Build SolverHost and set YX_SOLVER_TEST_COMMAND')
class SolverProcessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        directory = pathlib.Path(self.temp.name)
        config = {'version': 'synthetic-test', 'card': {
            str(i): {'n': 'Invented card ' + str(i), 'a': 2, 'c': 1} for i in range(9000001, 9000021)},
            'level': {'1': 20}}
        (directory / 'battle_config.json').write_text(json.dumps(config), encoding='utf-8')
        effects = {'999999': {'ops': [{'op': 'hp', 'target': 'src', 'val': 1}]}}
        (directory / 'card_effects.json').write_text(json.dumps(effects), encoding='utf-8')
        kinds = {kind: [] for kind in ('talent', 'fate', 'xianmo', 'keyin', 'buff', 'character', 'resonance')}
        kinds['card'] = [{'id': int(key), 'kind': 'card', 'name': value['n'], 'description': 'Invented effect', 'known': True}
                         for key, value in config['card'].items()]
        (directory / 'catalog.json').write_text(json.dumps({'version': 'synthetic-test', 'configVersion': 'synthetic-test',
            'gameRecordVersion': '001.0008.0002', 'kinds': kinds}), encoding='utf-8')
        self.environment = patch.dict(os.environ, {'YX_SOLVER_DATA': self.temp.name,
            'YX_SOLVER_COMMAND': os.environ['YX_SOLVER_TEST_COMMAND']})
        self.environment.start()
        metadata_catalog.load.cache_clear()
        metadata_catalog.index.cache_clear()
        self.jobs = solver_jobs.Jobs()

    def tearDown(self):
        self.jobs.stop_all()
        for job in self.jobs.jobs.values():
            job.thread.join(timeout=10)
        self.environment.stop()
        metadata_catalog.load.cache_clear()
        metadata_catalog.index.cache_clear()
        self.temp.cleanup()

    def request(self):
        return {'round': 1, 'slots': 2, 'threads': 0, 'timeLimitMs': 1, 'exhaustiveLimit': 0,
                'me': {'level': 1, 'life': 100, 'cards': [9000001, 9000002]},
                'foe': {'level': 1, 'life': 100, 'cards': [9000001, 9000001]}}

    def wait(self, token):
        for _ in range(200):
            result = self.jobs.get(token).snapshot()
            if result['state'] in ('done', 'cancelled', 'error'):
                return result
            time.sleep(0.05)
        self.fail('Solver did not reach a terminal state')

    def test_real_full_job_uses_all_cores_and_preserves_repeatable_results(self):
        initial = self.jobs.start(self.request())
        self.assertEqual(initial['threads'], initial['cores'])
        self.assertEqual(initial['searchMode'], 'exhaustive')
        report = self.wait(initial['job'])
        self.assertEqual(report['state'], 'done', report)
        self.assertTrue(report['exhaustive'])
        self.assertEqual(report['enumerated'], report['candidates'])
        self.assertEqual(report['progressPercent'], 100)
        self.assertGreater(report['elapsedMs'], 1)
        self.assertEqual(self.jobs.get(initial['job']).snapshot(), report)
        decorated = solver_jobs.decorate_report(report)
        self.assertTrue(decorated['best'][0]['cardDetails'][0]['known'] or
                        decorated['best'][0]['cardDetails'][0]['id'] == 0)

    def test_requested_threads_are_clamped_and_long_job_can_be_cancelled(self):
        request = self.request()
        request.update(slots=8, threads=1)
        request['me']['cards'] = list(range(9000001, 9000021))
        initial = self.jobs.start(request)
        self.assertEqual(initial['threads'], 1)
        with self.assertRaises(solver_jobs.BusyError):
            self.jobs.start(request)
        self.jobs.get(initial['job']).cancel()
        self.assertEqual(self.wait(initial['job'])['state'], 'cancelled')

    def test_bad_card_is_rejected_and_slot_is_reusable(self):
        request = self.request()
        request['me']['cards'] = [123456789]
        with self.assertRaises(ValueError):
            self.jobs.start(request)
        self.assertEqual(self.wait(self.jobs.start(self.request())['job'])['state'], 'done')

    def test_capabilities_and_explicit_large_core_request(self):
        info = self.jobs.capabilities()
        self.assertIn('exhaustive', info['modes'])
        self.assertTrue(info['baijia'])
        request = self.request()
        request['threads'] = 100000
        initial = self.jobs.start(request)
        self.assertEqual(initial['threads'], info['cores'])
        self.assertEqual(self.wait(initial['job'])['state'], 'done')


if __name__ == '__main__':
    unittest.main()
