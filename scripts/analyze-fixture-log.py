#!/usr/bin/env python3
"""Read local fixture JSONL and optional manually matched take-relative observations.

This reports target delivery, callback counts and observation uncertainty. It does
not measure hardware contact time, prove global zero-input, or equate UI clocks
with physical synchronization. Input/output files stay local by default.
"""
import argparse
import json
import math
from pathlib import Path


def integer(value, name):
    if type(value) is not int or value < 0:
        raise ValueError(f'{name} must be a nonnegative exact integer')
    return value


def analyze(rows):
    if not rows or rows[0].get('recordType') != 'header' or rows[0].get('schemaVersion') != 1:
        raise ValueError('unsupported fixture log schema')
    result = {'samples': 0, 'completePairs': 0, 'unmatchedUps': 0, 'incompleteDowns': 0,
              'scrollCallbacks': 0, 'scrollDirections': [], 'referenceMarkers': 0,
              'outsideEvents': 0, 'physicalTimingClaim': None}
    held = set()
    directions = set()
    previous = 0
    for index, row in enumerate(rows[1:], 1):
        if row.get('recordType') != 'sample' or row.get('sequence') != index:
            raise ValueError('noncontiguous sample sequence')
        stamp = integer(row.get('hostStamp'), 'host stamp')
        if stamp < previous:
            raise ValueError('reversed host stamp')
        previous = stamp
        geometry = row.get('geometry', {})
        try:
            numbers = [geometry[k] for k in ('originX', 'originY', 'width', 'height', 'scale')]
            valid = all(type(v) in (int, float) and math.isfinite(v) for v in numbers)
            valid = valid and all(geometry[k] > 0 for k in ('width', 'height', 'scale'))
        except (KeyError, TypeError):
            valid = False
        if not valid:
            raise ValueError('invalid canvas geometry')
        kind, button = row.get('kind'), row.get('button')
        if kind in ('down', 'up'):
            if type(button) is not int or button not in (0, 1, 2):
                raise ValueError('unsupported mouse button')
            if kind == 'down':
                if button in held:
                    result['incompleteDowns'] += 1
                held.add(button)
            elif button in held:
                result['completePairs'] += 1
                held.remove(button)
            else:
                result['unmatchedUps'] += 1
        elif kind == 'scroll':
            result['scrollCallbacks'] += 1
            delta = row.get('deltaY', 0)
            if not math.isfinite(delta):
                raise ValueError('nonfinite scroll delta')
            if delta:
                directions.add(1 if delta > 0 else -1)
        elif kind == 'marker':
            result['referenceMarkers'] += 1
        elif kind not in ('move', 'drag'):
            raise ValueError('unknown sample kind')
        result['outsideEvents'] += int(row.get('inside') is False and kind != 'marker')
        result['samples'] += 1
    result['incompleteDowns'] += len(held)
    result['scrollDirections'] = sorted(directions)
    return result


def distribution(values):
    if not values:
        return None
    ordered = sorted(values)
    def percentile(p):
        return ordered[max(0, math.ceil(p * len(ordered)) - 1)]
    return {'min': ordered[0], 'median': percentile(.5), 'p95': percentile(.95),
            'p99': percentile(.99), 'max': ordered[-1], 'samples': len(ordered)}


def observations(rows):
    delivery, hook, uncertainty = [], [], []
    missing = 0
    for row in rows:
        uncertainty.append(integer(row.get('uncertaintyUs'), 'uncertainty'))
        values = [row.get(k) for k in ('hookTimeUs', 'targetDeliveryTimeUs', 'visibleSourceTimeUs')]
        if any(v is None for v in values):
            missing += 1
            continue
        h, d, v = [integer(value, 'take-relative time') for value in values]
        delivery.append(v - d)
        hook.append(v - h)
    return {'matched': len(delivery), 'missingOrAmbiguous': missing,
            'deliveryToVisibleUs': distribution(delivery), 'hookToVisibleUs': distribution(hook),
            'maxUncertaintyUs': max(uncertainty, default=0), 'hardwareContactMeasured': False,
            'basis': 'Operator matched source-time observations; no clock-only physical precision claim'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('log', type=Path)
    parser.add_argument('--observations', type=Path, help='JSON array of independently matched take-relative times')
    args = parser.parse_args()
    result = analyze([json.loads(line) for line in args.log.read_text().splitlines() if line.strip()])
    if args.observations:
        result['observations'] = observations(json.loads(args.observations.read_text()))
    print(json.dumps(result, indent=2, allow_nan=False))


if __name__ == '__main__':
    main()
