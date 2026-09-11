import assert from 'node:assert/strict';
import { test } from 'node:test';
import { initialize, dispose } from '../../PersonalAgent.Web/Components/Pages/ChatMessageList.razor.js';

test('timestamp clicks open the matching drawer action without navigating, including nested link content and new messages', () => {
    const listeners = new Map();
    let clicks = 0;
    const url = '/evidence/80b16b37-10ac-4c41-9352-4faf4d06d2b6?profileId=owner&startMs=4000';
    const buttons = [];
    const message = { querySelectorAll: () => buttons };
    const link = { closest: () => message, getAttribute: () => url };
    const root = {
        contains: candidate => candidate === link,
        addEventListener: (name, handler) => listeners.set(name, handler),
        removeEventListener: (name, handler) => { if (listeners.get(name) === handler) listeners.delete(name); }
    };
    initialize(root);
    // Delegation must work for messages rendered after initialization.
    buttons.push({ dataset: { evidenceUrl: url }, click: () => clicks++ });
    let prevented = false;
    let stopped = false;
    listeners.get('click')({
        target: { closest: () => link },
        preventDefault: () => prevented = true,
        stopPropagation: () => stopped = true
    });
    assert.equal(clicks, 1);
    assert.equal(prevented, true);
    assert.equal(stopped, true);
    link.getAttribute = () => 'https://example.com';
    listeners.get('click')({ target: { closest: () => link }, preventDefault: () => assert.fail('ordinary links must navigate') });
    assert.equal(clicks, 1);
    initialize(root);
    assert.equal(listeners.size, 1);
    dispose(root);
    assert.equal(listeners.size, 0);
});
