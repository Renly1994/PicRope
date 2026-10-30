const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');

const source = fs.readFileSync(path.join(__dirname,
  '../vendor/Filerobot/filerobot-image-editor.min.js'), 'utf8');

// Fail explicitly on bundle upgrades instead of silently testing a different block.
function extract(start, end) {
  const at = source.indexOf(start);
  assert.ok(at >= 0 && at === source.lastIndexOf(start), `unique start: ${start}`);
  const stop = source.indexOf(end, at + start.length);
  assert.ok(stop >= 0 && stop === source.lastIndexOf(end), `unique end: ${end}`);
  return source.slice(at + start.length, stop);
}

const exporter = 'function(){function e(){n({type:GC})}' + extract(
  ':function(){function e(){n({type:GC})}', '},pk=');
const nodeMethods = 'toCanvas(e){' + extract('toCanvas(e){', 'toImage(e){');
const encoder = 'toDataURL(e,t){' + extract('toDataURL(e,t){', '}am.Canvas=wb,');

function harness({ failEncoding = 0 } = {}) {
  const renders = [], encodings = [], errors = [], actions = [];
  let destroyed = 0;
  const pixelRatio = { pixelRatio: 2 };
  const attrs = { width: 1600, height: 1000, x: 0, y: 0 };
  const image = { setAttrs() {}, filters: () => [], cache() {}, clearCache() {} };
  for (const key of ['width', 'height', 'offsetX', 'offsetY', 'scaleX', 'scaleY']) {
    image[key] = () => 1;
  }
  const stage = {
    attrs,
    children: [{ setAttrs() {} }, { destroy() {} }],
    findOne: () => image,
    setAttrs(update) { Object.assign(attrs, update); },
    width: () => attrs.width,
    height: () => attrs.height,
    x: () => attrs.x,
    y: () => attrs.y,
    destroy() { destroyed++; },
    _toKonvaCanvas(options) {
      const canvas = {
        toDataURL(...args) {
          encodings.push({ canvas, args });
          if (encodings.length <= failEncoding) throw new Error('encoding failed');
          return `data:${args[0] || 'image/png'};base64,fixture`;
        }
      };
      renders.push({ options: { ...options }, canvas });
      return Object.assign({ _canvas: canvas }, vm.runInNewContext(`({${encoder}})`, {
        mb: { Util: { error(message) { errors.push(message); } } }
      }));
    }
  };
  // Use the bundle's Node methods too, so the unpatched exporter really renders twice.
  Object.setPrototypeOf(stage, vm.runInNewContext(`(class {${nodeMethods}}).prototype`));
  const state = { shownImageDimensions: { width: 640, height: 400, scaledBy: 0.4 } };
  const context = {
    t: state, n: action => actions.push(action),
    r: { attrs: {}, setAttr() {}, getStage: () => ({ clone: () => stage }) },
    o: state.shownImageDimensions, i: { width: 1600, height: 1000 },
    a: {}, l: { noEffect: true }, s: 0, c: 1, u: 2, d: false,
    Dx: pixelRatio, xC: 'FIE_original-image', GC: 'hide', xD: 'saved',
    uk: Object.assign, qy: value => value,
    QO: () => ({ x: 0, y: 0, width: 1600, height: 1000 }),
    JO: (width, height) => ({ width, height }),
    ES: () => ({ annotations: {}, finetunes: [], filter: null }),
    HTMLImageElement: class {}, kC: { IMAGE: 'Image' }
  };
  const run = vm.runInNewContext(`(${exporter})`, context);
  return { run, renders, encodings, errors, actions, pixelRatio,
    get destroyed() { return destroyed; } };
}

test('local exporter renders exactly once for canvas and base64', () => {
  const h = harness();
  const result = h.run({ name: 'fixture', extension: 'png' });
  assert.equal(h.renders.length, 1, 'canvas and base64 must share one stage render');
  assert.equal(result.imageData.imageCanvas, h.encodings[0].canvas);
});

for (const { extension, quality, size, mimeType, encodedQuality, width, height } of [
  { extension: 'png', quality: 0.5, mimeType: 'image/png', encodedQuality: null, width: 1600, height: 1000 },
  { extension: 'jpeg', quality: 0.8, mimeType: 'image/jpeg', encodedQuality: 0.8, width: 1600, height: 1000 },
  { extension: 'jpg', quality: 0.75, size: { width: 800, height: 500 }, mimeType: 'image/jpeg', encodedQuality: 0.75, width: 800, height: 500 },
  { extension: 'webp', mimeType: 'image/webp', encodedQuality: 92, width: 1600, height: 1000 },
  { extension: 'jpeg', quality: 0, mimeType: 'image/jpeg', encodedQuality: null, width: 1600, height: 1000 }
]) {
  test(`preserves ${extension} encoding options (quality ${quality}) and export result`, () => {
    const h = harness();
    const { imageData, designState, hideLoadingSpinner } = h.run({
      name: 'fixture', extension, ...(quality === undefined ? {} : { quality }), size
    });
    assert.equal(imageData.fullName, `fixture.${extension}`);
    assert.equal(imageData.name, 'fixture');
    assert.equal(imageData.extension, extension);
    assert.equal(imageData.mimeType, `image/${extension}`);
    assert.equal(imageData.width, width);
    assert.equal(imageData.height, height);
    assert.equal(imageData.imageCanvas, h.renders[0].canvas);
    assert.equal(imageData.imageBase64, `data:${mimeType};base64,fixture`);
    assert.deepEqual(h.encodings.map(call => call.args), [[mimeType, encodedQuality]]);
    assert.deepEqual(h.renders[0].options, extension === 'png'
      ? { mimeType } : { mimeType, quality: quality === undefined ? 92 : quality });
    assert.equal(Object.hasOwn(imageData, 'quality'), extension !== 'png');
    if (extension !== 'png') assert.equal(imageData.quality, quality === undefined ? 92 : quality);
    assert.equal(designState.shownImageDimensions.scaledBy, 0.4);
    assert.equal(h.destroyed, 1);
    assert.equal(h.pixelRatio.pixelRatio, 2);
    hideLoadingSpinner();
    assert.deepEqual(h.actions.map(action => action.type), ['saved', 'hide', 'hide']);
  });
}

test('retains Konva default-encoding retry without rendering again', () => {
  const h = harness({ failEncoding: 1 });
  const { imageData } = h.run({ name: 'fixture', extension: 'jpeg', quality: 0.8 });
  assert.equal(imageData.imageBase64, 'data:image/png;base64,fixture');
  assert.deepEqual(h.encodings.map(call => call.args), [['image/jpeg', 0.8], []]);
  assert.deepEqual(h.errors, []);
  assert.equal(h.renders.length, 1, 'encoding retry must reuse the rendered wrapper');
  assert.equal(imageData.imageCanvas, h.encodings[1].canvas);
});

test('retains Konva empty result and error reporting when both encodings fail', () => {
  const h = harness({ failEncoding: 2 });
  const { imageData } = h.run({ name: 'fixture', extension: 'png' });
  assert.equal(imageData.imageBase64, '');
  assert.deepEqual(h.encodings.map(call => call.args), [['image/png', null], []]);
  assert.equal(h.errors.length, 1);
  assert.match(h.errors[0], /Unable to get data URL\. encoding failed/);
  assert.equal(h.destroyed, 1);
  assert.equal(h.pixelRatio.pixelRatio, 2);
  assert.equal(h.renders.length, 1, 'failed encoding must not render again');
});
