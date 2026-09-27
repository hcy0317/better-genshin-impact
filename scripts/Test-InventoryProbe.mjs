import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import test from 'node:test';

const source = fs.readFileSync(new URL('./onedragon-probes/inventory-read-only/main.js', import.meta.url), 'utf8');
async function run({selected = true, fail = false, capturePages = false, changePage = false} = {}) {
    const events = [];
    let captures = 0;
    const context = vm.createContext({
        file: {
            ReadImageMatSync: name => ({name, dispose() {}}),
            WriteImageSync: name => { events.push('image:' + name); return true; },
            WriteTextSync: (name, value) => { events.push(JSON.parse(value).status); return true; },
        },
        RecognitionObject: {TemplateMatch: mat => mat},
        genshin: {returnMainUi: async () => { events.push('main'); }},
        keyPress: key => { events.push(key); }, sleep: async () => {},
        settings: {capturePages},
        moveMouseTo: (x, y) => { events.push(`move:${x},${y}`); },
        verticalScroll: amount => { events.push(`scroll:${amount}`); },
        log: {info() {}},
        captureGameRegion() {
            if (fail) throw new Error('cancelled');
            events.push('capture');
            captures++;
            return {Width: 1920, Height: 1080, SrcMat: {}, dispose() {}, DeriveCrop() {
                return {dispose() {}, find: ro => ({dispose() {},
                    isExist: () => ro.name.includes('_unchecked') || selected && (!changePage || captures < 3),
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

test('page probe is bounded and retains thirteen images without using any item', async () => {
    const result = await run({capturePages: true});
    assert.ifError(result.error);
    assert.equal(result.events.filter(x => x.startsWith('image:evidence/precious-scroll-')).length, 13);
    assert.equal(result.events.filter(x => x === 'scroll:-3').length, 12);
    assert.equal(result.events.filter(x => x === 'scroll:50').length, 1);
    assert.equal(result.events.at(-1), 'captured');
});

test('page identity loss stops further scrolling and does not report success', async () => {
    const result = await run({capturePages: true, changePage: true});
    assert.match(result.error.message, /PROBE_PAGE_CHANGED/);
    assert.equal(result.events.filter(x => x === 'scroll:-3').length, 1);
    assert(!result.events.includes('captured'));
});
