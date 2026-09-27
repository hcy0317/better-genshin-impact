import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import test from 'node:test';

const source = fs.readFileSync(new URL('./onedragon-probes/inventory-read-only/main.js', import.meta.url), 'utf8');
async function run({selected = true, fail = false} = {}) {
    const events = [];
    const context = vm.createContext({
        file: {
            ReadImageMatSync: name => ({name, dispose() {}}),
            WriteImageSync: name => { events.push('image:' + name); return true; },
            WriteTextSync: (name, value) => { events.push(JSON.parse(value).status); return true; },
        },
        RecognitionObject: {TemplateMatch: mat => mat},
        genshin: {returnMainUi: async () => { events.push('main'); }},
        keyPress: key => { events.push(key); }, sleep: async () => {},
        log: {info() {}},
        captureGameRegion() {
            if (fail) throw new Error('cancelled');
            events.push('capture');
            return {Width: 1920, Height: 1080, SrcMat: {}, dispose() {}, DeriveCrop() {
                return {dispose() {}, find: ro => ({dispose() {},
                    isExist: () => ro.name.includes('_unchecked') || selected,
                    click: () => { events.push('select-tab'); },
                })};
            }};
        },
    });
    try { await vm.runInContext(source, context); }
    catch (error) { return {events, error}; }
    return {events};
}

test('probe only captures a confirmed precious-items page and never uses an item', async () => {
    const result = await run();
    assert.ifError(result.error);
    assert.deepEqual(result.events, ['main', 'B', 'capture', 'image:evidence/precious-page.png', 'captured']);
});
test('an unconfirmed page is bounded and cannot yield successful evidence', async () => {
    const result = await run({selected: false});
    assert.match(result.error.message, /PAGE_NOT_CONFIRMED/);
    assert.equal(result.events.filter(x => x === 'capture').length, 5);
    assert(!result.events.includes('captured'));
});
test('cancellation does not cause recovery or a second key press', async () => {
    const result = await run({fail: true});
    assert.match(result.error.message, /cancelled/);
    assert.deepEqual(result.events, ['main', 'B']);
});
