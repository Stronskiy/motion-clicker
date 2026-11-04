import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('fixture_analysis', Path(__file__).parents[1] / 'analyze-fixture-log.py')
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


def rows(kinds):
    header = {'recordType': 'header', 'schemaVersion': 1, 'platform': 'synthetic', 'canvasId': 'test',
              'hostClock': {'name': 'synthetic', 'ticksPerSecond': 10_000_000},
              'coordinateSpace': 'desktopPixelsTopLeft'}
    result = [header]
    for i, (kind, button, delta) in enumerate(kinds, 1):
        result.append({'recordType': 'sample', 'sequence': i, 'hostStamp': 9_007_199_254_740_992 + i,
                       'kind': kind, 'button': button, 'deltaY': delta, 'inside': True,
                       'geometry': {'originX': -1920, 'originY': 0, 'width': 1280, 'height': 720, 'scale': 1.5}})
    return result


class FixtureAnalysisTests(unittest.TestCase):
    def test_double_click_is_two_pairs_and_scroll_callbacks_are_not_gestures(self):
        data = rows([('down', 0, 0), ('up', 0, 0)] * 7 + [('scroll', None, 2), ('scroll', None, 3),
                    ('scroll', None, -4), ('marker', None, 0)])
        result = m.analyze(data)
        self.assertEqual(result['completePairs'], 7)
        self.assertEqual(result['scrollCallbacks'], 3)
        self.assertEqual(result['scrollDirections'], [-1, 1])
        self.assertEqual(result['referenceMarkers'], 1)
        self.assertIsNone(result['physicalTimingClaim'])

    def test_interrupted_hold_and_mismatched_release_do_not_fabricate_pairs(self):
        result = m.analyze(rows([('up', 2, 0), ('down', 0, 0), ('up', 1, 0)]))
        self.assertEqual(result['completePairs'], 0)
        self.assertEqual(result['unmatchedUps'], 2)
        self.assertEqual(result['incompleteDowns'], 1)

    def test_exact_integer_stamp_order_and_sequence_are_validated(self):
        data = rows([('marker', None, 0)] * 2)
        self.assertEqual(m.analyze(data)['samples'], 2)
        data[2]['hostStamp'] = data[1]['hostStamp'] - 1
        with self.assertRaisesRegex(ValueError, 'stamp'):
            m.analyze(data)
        data[2]['hostStamp'] = float(data[1]['hostStamp'])
        with self.assertRaisesRegex(ValueError, 'integer'):
            m.analyze(data)
        data[2]['hostStamp'] = data[1]['hostStamp']
        data[2]['sequence'] = 4
        with self.assertRaisesRegex(ValueError, 'sequence'):
            m.analyze(data)

    def test_invalid_geometry_and_protocol_are_rejected(self):
        data = rows([('marker', None, 0)])
        data[1]['geometry']['width'] = float('nan')
        with self.assertRaisesRegex(ValueError, 'geometry'):
            m.analyze(data)
        data[0]['schemaVersion'] = 2
        with self.assertRaisesRegex(ValueError, 'schema'):
            m.analyze(data)

    def test_observation_summary_retains_missing_samples_and_uncertainty(self):
        result = m.observations([{'hookTimeUs': 100, 'targetDeliveryTimeUs': 120,
                                  'visibleSourceTimeUs': 40120, 'uncertaintyUs': 33334},
                                 {'hookTimeUs': 200, 'targetDeliveryTimeUs': 240,
                                  'visibleSourceTimeUs': None, 'uncertaintyUs': 33334}])
        self.assertEqual(result['matched'], 1)
        self.assertEqual(result['missingOrAmbiguous'], 1)
        self.assertEqual(result['deliveryToVisibleUs']['max'], 40000)
        self.assertEqual(result['maxUncertaintyUs'], 33334)
        self.assertFalse(result['hardwareContactMeasured'])
