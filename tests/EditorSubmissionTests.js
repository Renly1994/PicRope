const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const nodeTest = require('node:test');
const test = (name, run) => nodeTest(name, { timeout: 1000 }, run);
const source = fs.readFileSync(path.join(__dirname, '../src/editor/editor.js'), 'utf8');
const png = () => new Blob(['png fixture'], { type: 'image/png' });
const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};

function harness(options = {}) {
  const messages = [], requests = [], exports = [], encodings = [], completions = [], events = [];
  const nodes = new Map(), classes = new Set(), stage = {};
  const node = () => ({ dataset: {}, style: {}, hidden: true,
    classList: { toggle() {}, add(name) { classes.add(name); }, remove(name) { classes.delete(name); } },
    addEventListener() {}, setAttribute() {}, getBoundingClientRect() { return { x: 0, width: 20 }; } });
  const document = { body: node(), fonts: { ready: options.fontsReady || Promise.resolve() },
    querySelectorAll: () => [], addEventListener() {},
    getElementById(id) { if (!nodes.has(id)) nodes.set(id, node()); return nodes.get(id); },
    querySelector(selector) { return selector.includes('.popup') ? null : this.getElementById(selector); } };
  const state = { annotations: {}, adjustments: {}, resize: {}, finetunes: [], finetunesProps: {}, filter: null,
    isLoadingGlobally: false, shownImageDimensions: { width: 640, height: 480 },
    designLayer: { getStage: () => stage }, selectionsIds: [], hasUndo: false, hasRedo: false };
  const blob = Object.hasOwn(options, 'blob') ? options.blob : png();
  let config, blobCallback, base64Reads = 0;
  const canvas = { toBlob(callback, ...args) {
    events.push('toBlob'); encodings.push(args);
    if (options.blobError) throw new Error('canvas encoding failed');
    blobCallback = callback;
    if (!options.deferBlob) queueMicrotask(() => callback(blob));
  } };
  class Image { set src(value) { this.onload(); } }
  class Filerobot {
    static TABS = { ANNOTATE: 'Annotate' };
    static TOOLS = { RECT: 'Rect' };
    constructor(container, value) { config = value; }
    render() {}
    updateState(update) {
      const change = typeof update === 'function' ? update(state) : update;
      Object.assign(state, change);
      if (Object.keys(change).length) config.onModify(state);
    }
    getCurrentImgData(...args) {
      events.push('export'); exports.push(args);
      if (options.exportError) throw new Error('export failed');
      return { designState: state, imageData: { imageCanvas: canvas,
        get imageBase64() { base64Reads++; return 'data:image/png;base64,forbidden'; } } };
    }
  }
  const window = { devicePixelRatio: 2, addEventListener() {}, chrome: { webview: { postMessage(message) {
    events.push(message.action); messages.push(message);
  } } } };
  const context = vm.createContext({ window, document, Image, HTMLImageElement: Image, Blob,
    FilerobotImageEditor: Filerobot,
    requestAnimationFrame(callback) { queueMicrotask(callback); },
    async fetch(url, init) {
      events.push('fetch'); requests.push({ url, ...init });
      if (options.fetchError) throw new Error('transport failed');
      return options.fetchResponse || { status: options.status ?? 202 };
    } });
  vm.runInContext(source, context);
  const complete = window.completeAction;
  window.completeAction = result => { events.push('complete'); completions.push(result); complete(result); };
  return { window, blob, messages, requests, exports, encodings, completions, events, classes,
    start: () => window.startEditor({ name: 'fixture.png', source: 'fixture.png', exportToken: 'session-token' }),
    inspect: expression => vm.runInContext(expression, context),
    releaseBlob: () => blobCallback(blob),
    modify() { state.adjustments = { rotation: 90 }; config.onModify(state); },
    async pendingOperation() {
      vm.runInContext("toolSession={tool:'Rotate',returnTool:'Rect',snapshot:{}}", context);
      const commit = deferred(); context.pendingText = commit.promise;
      vm.runInContext('textCommit=pendingText', context);
      return commit;
    },
    get base64Reads() { return base64Reads; } };
}

function assertFailure(h) {
  assert.equal(h.messages.filter(message => message.action === 'exportFailed').length, 1,
    'notify host so it can settle save-on-close failure');
  assert.equal(h.completions.length, 1);
  assert.equal(h.completions[0].success, false);
  assert.equal(h.events.indexOf('exportFailed') < h.events.indexOf('complete'), true);
  assert.equal(h.inspect('busy'), false);
  assert.equal(h.classes.has('busy'), false);
  assert.equal(h.inspect('baseline!==currentKey'), true, 'failure must retain unsaved edits');
  assert.equal(h.messages.some(message => Object.hasOwn(message, 'data')), false, 'never retry Base64');
  assert.equal(h.base64Reads, 0);
}

for (const [action, hostAction, close] of [
  ['save', 'save', 'false'], ['saveAs', 'saveAs', 'false'],
  ['copy', 'copy', 'false'], ['finish', 'save', 'true']
]) {
  test(`${action} posts PNG Blob and waits for host completion after HTTP 202`, async () => {
    const h = harness(); await h.start(); h.modify();
    const baseline = h.inspect('baseline');
    await h.window.submit(action);
    assert.deepEqual(JSON.parse(JSON.stringify(h.exports)), [[{ extension: 'png', picropeCanvasOnly: true }, 1]]);
    assert.deepEqual(h.encodings, [['image/png']]);
    assert.equal(h.requests.length, 1);
    assert.equal(h.requests[0].url, 'https://screenline-editor.invalid/export');
    assert.equal(h.requests[0].method, 'POST');
    assert.deepEqual({ ...h.requests[0].headers }, { 'Content-Type': 'image/png',
      'X-PicRope-Token': 'session-token', 'X-PicRope-Action': hostAction, 'X-PicRope-Close': close });
    assert.equal(h.requests[0].body, h.blob);
    assert.equal(h.base64Reads, 0);
    assert.equal(h.completions.length, 0, '202 accepts the request; only the host completes it');
    assert.equal(h.inspect('busy'), true);
    assert.equal(h.classes.has('busy'), true);
    assert.equal(h.inspect('baseline'), baseline);
    assert.equal(h.inspect('submitted===currentKey'), true);
    await h.window.submit(action);
    assert.equal(h.requests.length, 1, 'busy blocks duplicate submission');
    h.window.completeAction({ success: true, saved: action !== 'copy', message: 'done', name: 'saved.png' });
    assert.equal(h.inspect('busy'), false);
    assert.equal(h.inspect('baseline===submitted'), action !== 'copy');
    if (action !== 'copy') assert.equal(h.messages.at(-1).value, false);
    assert.equal(h.messages.some(message => Object.hasOwn(message, 'data')), false);
  });
}

test('awaits tool, text and fonts, then Blob and HTTP, preserving submitted timing', async () => {
  const fonts = deferred(), response = deferred();
  const h = harness({ fontsReady: fonts.promise, deferBlob: true, fetchResponse: response.promise });
  await h.start(); h.modify();
  const text = await h.pendingOperation();
  const submission = h.window.submit('save');
  await new Promise(setImmediate);
  assert.equal(h.inspect('toolSession'), null, 'pending operation is applied');
  assert.equal(h.exports.length, 0);
  assert.equal(h.inspect('busy'), true);
  text.resolve(); await new Promise(setImmediate);
  assert.equal(h.exports.length, 0, 'fonts must also be ready');
  fonts.resolve(); await new Promise(setImmediate);
  assert.equal(h.exports.length, 1);
  assert.equal(h.encodings.length, 1, 'export must start asynchronous PNG encoding');
  assert.equal(h.inspect('submitted===currentKey'), true, 'snapshot is captured before async encoding');
  assert.equal(h.requests.length, 0);
  h.releaseBlob(); await new Promise(setImmediate);
  assert.equal(h.requests.length, 1);
  assert.equal(h.inspect('busy'), true);
  response.resolve({ status: 202 }); await submission;
  assert.equal(h.completions.length, 0);
  h.window.completeAction({ success: false, message: 'host save failed' });
  assert.equal(h.inspect('busy'), false);
  assert.equal(h.inspect('baseline!==currentKey'), true);
});

for (const [name, options] of [
  ['export exception', { exportError: true }], ['toBlob exception', { blobError: true }],
  ['null Blob', { blob: null }], ['empty Blob', { blob: new Blob([], { type: 'image/png' }) }],
  ['wrong MIME', { blob: new Blob(['jpeg'], { type: 'image/jpeg' }) }],
  ['missing MIME', { blob: new Blob(['png']) }],
  ['over 64 MiB', { blob: new (class extends Blob { get size() { return 67108865; } })(['x'], { type: 'image/png' }) }],
  ['HTTP 200', { status: 200 }], ['HTTP 201', { status: 201 }],
  ['HTTP 400', { status: 400 }], ['HTTP 500', { status: 500 }],
  ['transport exception', { fetchError: true }]
]) {
  test(`${name} notifies host before failure completion without Base64 retry`, async () => {
    const h = harness(options); await h.start(); h.modify();
    await h.window.submit('finish');
    assertFailure(h);
    assert.equal(h.exports.length, 1);
    assert.equal(h.requests.length, options.status || options.fetchError ? 1 : 0);
  });
}

test('exactly 64 MiB is accepted', async () => {
  const blob = new (class extends Blob { get size() { return 67108864; } })(['x'], { type: 'image/png' });
  const h = harness({ blob }); await h.start();
  await h.window.submit('save');
  assert.equal(h.requests.length, 1);
  assert.equal(h.requests[0].body, blob);
  assert.equal(h.completions.length, 0);
});

test('submit before ready does nothing', async () => {
  const h = harness(); await h.window.submit('save');
  assert.equal(h.exports.length, 0);
  assert.equal(h.requests.length, 0);
  assert.equal(h.completions.length, 0);
});
