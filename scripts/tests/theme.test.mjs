import assert from 'node:assert/strict';
import { test } from 'node:test';
import { initialize } from '../../PersonalAgent.Web/Components/ThemePicker.razor.js';

function environment(saved = null, dark = false, blocked = false) {
    const media = new EventTarget();
    media.matches = dark;
    globalThis.matchMedia = () => media;
    globalThis.window = new EventTarget();
    globalThis.document = { documentElement: { dataset: {} } };
    globalThis.localStorage = {
        getItem() { if (blocked) throw Error('denied'); return saved; },
        setItem(key, value) { if (blocked) throw Error('denied'); saved = value; }
    };
    const calls = [];
    return { media, calls, saved: () => saved, reference: { async invokeMethodAsync(...args) { calls.push(args); } } };
}

test('system follows OS changes; explicit preference wins and survives recreation', async () => {
    const env = environment(null, true);
    const controller = await initialize(env.reference);
    assert.equal(document.documentElement.dataset.theme, 'dark');
    assert.deepEqual(env.calls.at(-1), ['OnThemeChanged', 'system', true]);
    await controller.setPreference('light');
    env.media.dispatchEvent(new Event('change'));
    assert.equal(document.documentElement.dataset.theme, 'light');
    assert.equal(env.saved(), 'light');
    controller.dispose();
    const next = await initialize(env.reference);
    assert.deepEqual(env.calls.at(-1), ['OnThemeChanged', 'light', false]);
    await next.setPreference('system');
    env.media.matches = false;
    env.media.dispatchEvent(new Event('change'));
    assert.equal(document.documentElement.dataset.theme, 'light');
    next.dispose();
    const count = env.calls.length;
    env.media.dispatchEvent(new Event('change'));
    assert.equal(env.calls.length, count);
});

test('blocked storage still allows switching and disconnects do not reject', async () => {
    const env = environment(null, false, true);
    const controller = await initialize(env.reference);
    await controller.setPreference('dark');
    assert.equal(document.documentElement.dataset.theme, 'dark');
    env.reference.invokeMethodAsync = async () => { throw Error('disconnected'); };
    await controller.setPreference('light');
    controller.dispose();
});

test('invalid storage falls back to system and cross-tab changes stay synchronized', async () => {
    const env = environment('invalid', true);
    const controller = await initialize(env.reference);
    assert.deepEqual(env.calls.at(-1), ['OnThemeChanged', 'system', true]);
    const event = new Event('storage');
    Object.assign(event, { key: 'personalagent.color-theme', newValue: 'light' });
    window.dispatchEvent(event);
    assert.deepEqual(env.calls.at(-1), ['OnThemeChanged', 'light', false]);
    const cleared = new Event('storage');
    Object.assign(cleared, { key: null, newValue: null });
    window.dispatchEvent(cleared);
    assert.deepEqual(env.calls.at(-1), ['OnThemeChanged', 'system', true]);
    controller.dispose();
});
