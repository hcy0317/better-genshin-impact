import assert from 'node:assert/strict';
import test from 'node:test';
import { parseLog, summarizeLog } from './Analyze-OneDragonLog.mjs';

test('multiline errors retain their source lines and separate run identities', () => {
    const result = parseLog('[01:00:00.000] [WRN] [Primary:S1:P1:T1] BGI.Task logQueueMs=0.0\nretry\n\n' +
        '[01:00:01.000] [ERR] [Primary:S2:P2:T2] BGI.Task logQueueMs=1.2\nfailed\nSystem.Exception: original\n   at Task.Run()\n');
    assert.equal(result.entries.length, 2);
    assert.equal(result.entries[0].run, 'Primary:S1:P1:T1');
    assert.equal(result.entries[1].run, 'Primary:S2:P2:T2');
    assert.equal(result.entries[1].line, 4);
    assert.match(result.entries[1].message, /System.Exception: original\n   at Task.Run\(\)/);
    assert.deepEqual(result.unparsed, []);
});

test('all warning/error occurrences survive grouping and low-level failures are separate', () => {
    const summary = summarizeLog(parseLog('[01:00:00.000] [WRN] [run1] BGI.Task\nretry\n' +
        '[01:00:01.000] [WRN] [run1] BGI.Task\nretry\n' +
        '[01:00:02.000] [ERR] [run2] BGI.Task\nfailed\n' +
        '[01:00:03.000] [DBG] [run2] BGI.Task\nEVIDENCE_CAPTURE_MISSING reason=run-disk-budget\n' +
        '[01:00:04.000] [DBG] [run2] BGI.Task\nNATIVE_INPUT_RESULT status=Sent errorType=null\n'));
    assert.equal(summary.warningErrors, 3);
    assert.equal(summary.lowLevelFailures, 1);
    assert.equal(summary.groups.length, 3);
    assert.deepEqual(summary.groups[0].occurrences.map(item => item.line), [1, 3]);
    assert.equal(summary.groups.reduce((sum, group) => sum + group.count, 0), 4);
});

test('malformed headers are reported without being hidden in the previous exception', () => {
    const result = parseLog('orphan\n[01:00:00.000] [INF] BGI.Task\nnormal\n[01:00:01.000] [BAD] broken\nunknown body');
    assert.equal(result.entries.length, 1);
    assert.equal(result.entries[0].message, 'normal');
    assert.equal(result.unparsed.length, 3);
    assert.equal(result.unparsed[1].line, 4);
});
