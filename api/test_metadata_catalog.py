import json
import os
import pathlib
import tempfile
import unittest
import importlib.util
from unittest.mock import patch

import metadata_catalog


class CatalogTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        kinds = {kind: [] for kind in ('talent', 'fate', 'xianmo', 'resonance', 'card', 'keyin', 'buff', 'character')}
        for kind in ('talent', 'fate', 'xianmo'):
            kinds[kind] = [{'id': 10005, 'kind': kind, 'name': kind + ' test', 'description': 'Synthetic detailed effect', 'known': True}]
        pathlib.Path(self.temp.name, 'catalog.json').write_text(json.dumps({'version': 'test', 'kinds': kinds,
            'configVersion': 'test', 'gameRecordVersion': '001.0008.0002'}), encoding='utf-8')
        self.environment = patch.dict(os.environ, {'YX_SOLVER_DATA': self.temp.name})
        self.environment.start()
        metadata_catalog.load.cache_clear()
        metadata_catalog.index.cache_clear()

    def tearDown(self):
        self.environment.stop()
        metadata_catalog.load.cache_clear()
        metadata_catalog.index.cache_clear()
        self.temp.cleanup()

    def test_same_numeric_id_has_distinct_namespaces(self):
        self.assertNotEqual(metadata_catalog.lookup('talent', 10005)['name'],
                            metadata_catalog.lookup('xianmo', 10005)['name'])
        self.assertEqual(metadata_catalog.lookup('talent', 10005)['description'], 'Synthetic detailed effect')

    def test_unknown_identifier_is_explicit(self):
        self.assertFalse(metadata_catalog.lookup('talent', 99999)['known'])
        with self.assertRaises(ValueError):
            metadata_catalog.lookup('invalid', 10005)

    def test_search_and_invalid_pagination(self):
        self.assertEqual(metadata_catalog.query({'kind': ['talent'], 'q': ['10005']})['total'], 1)
        self.assertEqual(metadata_catalog.query({})['kinds']['talent'], 1)
        with self.assertRaises(ValueError):
            metadata_catalog.query({'kind': ['talent'], 'limit': ['101']})

    def test_decorate_retains_ids_and_adds_descriptions(self):
        side = {'talents': [10005], 'xianMoStrategies': [10005], 'cards': [], 'characterId': 0}
        replay = {'rounds': [{'request': {'me': dict(side), 'foe': dict(side)}}]}
        result = metadata_catalog.decorate_replay(replay)
        self.assertEqual(result['rounds'][0]['request']['me']['talents'], [10005])
        self.assertEqual(result['rounds'][0]['request']['me']['details']['xianMoStrategies'][0]['name'], 'xianmo test')


class ExportDescriptionTests(unittest.TestCase):
    def test_keywords_linked_talents_and_def_parameter_have_readable_text(self):
        path = pathlib.Path(__file__).resolve().parents[1] / 'tools/export_catalog.py'
        spec = importlib.util.spec_from_file_location('export_catalog', path)
        exporter = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(exporter)
        text, unresolved = exporter.describe('【KEYWORD_状态甲】 【TALENT_7】 +{def} /{otherParams[0]}',
            {'def': 2, 'otherParams': [3]}, {}, {'Talent_7': '虚构仙命'})
        self.assertEqual(text, '【状态甲】 【虚构仙命】 +2 /3')
        self.assertEqual(unresolved, [])


if __name__ == '__main__':
    unittest.main()
