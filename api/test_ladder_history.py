import datetime as dt
import json
import tempfile
import unittest
from pathlib import Path
import ladder_history as history


class LadderHistoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)

    def save(self, date, players, latest=False):
        value = {'seasonId':11,'generatedAt':date,'players':[
            {'id':key,'name':key,'score':score,'rank':rank,'delta':999,'rankDelta':999}
            for key,score,rank in players]}
        name = dt.datetime.fromisoformat(date).strftime('%Y%m%dT%H%M%SZ')+'.json'
        (self.directory/name).write_text(json.dumps(value))
        if latest:(self.directory/'latest.json').write_text(json.dumps(value))

    def compare(self, start, end):
        return history.compare(self.directory,11,{'from':[str(history.timestamp(start))],'to':[str(history.timestamp(end))]})

    def test_period_gain_uses_boundary_scores_and_keeps_entrants_and_departures(self):
        self.save('2026-10-04T10:00:00+00:00',[('a',100,2),('b',150,1)])
        self.save('2026-10-04T10:10:00+00:00',[('a',110,2),('b',155,1)])
        self.save('2026-10-04T10:20:00+00:00',[('a',180,1),('c',160,2)],latest=True)
        data=self.compare('2026-10-04T10:04:00+00:00','2026-10-04T10:22:00+00:00')
        rows={p['id']:p for p in data['players']}
        self.assertEqual(data['baselineAt'],'2026-10-04T10:10:00+00:00')
        self.assertEqual(data['comparison']['snapshotCount'],2)
        self.assertEqual(rows['a']['delta'],70)
        self.assertEqual(rows['a']['rankDelta'],1)
        self.assertIsNone(rows['b']['score'])
        self.assertEqual(rows['b']['status'],'left')
        self.assertIsNone(rows['c']['startScore'])
        self.assertIsNone(rows['c']['delta'])
        self.assertEqual(rows['c']['status'],'entered')
        self.assertEqual(data['comparison']['matched'],1)
        self.assertEqual(data['comparison']['left'],1)
        self.assertEqual(data['comparison']['entered'],1)
        self.assertTrue(data['comparison']['endTruncated'])

    def test_available_bounds_truncation_and_latest_deduplication(self):
        self.save('2026-10-04T10:00:00.500+00:00',[('a',100,1)])
        self.save('2026-10-04T10:10:00.500+00:00',[('a',110,1)],latest=True)
        data=self.compare('2026-10-03T00:00:00+00:00','2026-10-04T11:00:00+00:00')
        self.assertEqual(data['comparison']['snapshotCount'],2)
        self.assertTrue(data['comparison']['startTruncated'])
        self.assertEqual(data['comparison']['availableFrom'],'2026-10-04T10:00:00.500+00:00')
        self.assertEqual(data['players'][0]['delta'],10)

    def test_exact_boundaries_one_snapshot_and_no_snapshot(self):
        self.save('2026-10-04T10:00:00.500+00:00',[('a',100,1)])
        self.save('2026-10-04T10:10:00.500+00:00',[('a',110,1)],latest=True)
        both=self.compare('2026-10-04T10:00:00.500+00:00','2026-10-04T10:10:00.500+00:00')
        self.assertTrue(both['comparison']['canCompare'])
        one=self.compare('2026-10-04T10:00:00.600+00:00','2026-10-04T10:10:00.500+00:00')
        self.assertEqual(one['comparison']['snapshotCount'],1)
        self.assertFalse(one['comparison']['canCompare'])
        self.assertEqual(one['players'][0]['status'],'uncompared')
        self.assertIsNone(one['players'][0]['delta'])
        empty=self.compare('2026-10-04T09:00:00+00:00','2026-10-04T09:59:00+00:00')
        self.assertEqual(empty['players'],[])
        self.assertEqual(empty['comparison']['snapshotCount'],0)

    def test_invalid_range_and_empty_season(self):
        for params in ({'from':['1']},{'from':['2'],'to':['1']},{'from':['-1'],'to':['10']}):
            with self.assertRaises(ValueError):history.compare(self.directory,11,params)
        with self.assertRaises(ValueError):history.compare(self.directory,11,{'from':['1'],'to':['2']})


if __name__=='__main__':unittest.main()
