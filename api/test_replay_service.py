import unittest
from replay_service import decode_code,normalize_replay

class ReplayTests(unittest.TestCase):
    def test_real_replay_code(self):
        plain,selected=decode_code('gma6hs2pc2')
        self.assertEqual(int(plain,36),33524279140)
        self.assertEqual(selected,14)

    def test_snapshot_and_score_metadata(self):
        snapshot={'usedCards':[3,8,15,36],'handCards':[47],'unlockGrids':4,'talents':[125],
                  'life':40,'extraMaxHp':5,'level':2,'permanentBuffTempDatas':{'10049':1}}
        player={'publicData':{'uid':'private-user-id','username':'private-name','talents':[999],
                             'lastRoundData':snapshot},'privateData':{}}
        foe={'publicData':{'uid':'other-user-id','lastRoundData':dict(snapshot)},'privateData':{}}
        result=normalize_replay({'uid':'private-user-id','beginRankScore':3500,'diffRankScore':-12,
            'roundStats':[{'round':4,'p1':player,'p2':foe,'firstPlayerId':'other-user-id'}]})
        req=result['rounds'][0]['request']
        self.assertEqual(req['me']['talents'],[125])
        self.assertEqual(req['me']['cards'],[3,8,15,36,47])
        self.assertEqual(req['slots'],4)
        self.assertEqual(req['first'],'foe')
        self.assertEqual(req['maxConsumeSustain'],3)
        self.assertEqual(result['match']['rankAfter'],3488)
        self.assertNotIn('private-user-id',str(result))
        self.assertNotIn('private-name',str(result))

    def test_missing_snapshot_rejected(self):
        player={'publicData':{'uid':'a','usedCards':[3]}}
        with self.assertRaises(ValueError):
            normalize_replay({'roundStats':[{'p1':player,'p2':player,'round':1}]})

if __name__=='__main__':unittest.main()
