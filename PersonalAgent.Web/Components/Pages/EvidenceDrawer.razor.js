const players = new WeakMap();

export function stop(root) {
    const player = players.get(root);
    if (player) { player.abort.abort(); player.observer.disconnect(); player.audio?.pause(); if (player.opener?.isConnected) player.opener.focus(); }
    players.delete(root);
}

export function initialize(root, startMs) {
    stop(root);
    const audio = root.querySelector('audio');
    const abort = new AbortController();
    const opener = document.activeElement;
    const observer = new MutationObserver(() => { if (!root.isConnected || (audio && !audio.isConnected)) stop(root); });
    observer.observe(document.body, { childList: true, subtree: true });
    players.set(root, { audio, abort, observer, opener });
    root.querySelector('h2')?.focus();
    root.querySelector('[data-cited="true"]')?.scrollIntoView({ block: 'nearest' });
    if (!audio) return;
    const status = root.querySelector('[data-player-status]');
    const seek = seconds => {
        if (!Number.isFinite(seconds) || !Number.isFinite(audio.duration)) return;
        audio.currentTime = Math.max(0, Math.min(seconds, audio.duration));
    };
    const ready = () => {
        status.textContent = '';
        if (Number.isFinite(startMs) && startMs >= 0) seek(startMs / 1000);
    };
    audio.addEventListener('loadedmetadata', ready, { signal: abort.signal });
    audio.addEventListener('error', () => { status.textContent = 'Recording could not be played. It may be unavailable, or this browser may not support its format.'; }, { signal: abort.signal });
    audio.addEventListener('waiting', () => { status.textContent = 'Buffering recording…'; }, { signal: abort.signal });
    audio.addEventListener('playing', () => { status.textContent = ''; }, { signal: abort.signal });
    root.addEventListener('click', event => {
        const button = event.target.closest('button');
        if (!button || button.disabled) return;
        if (button.hasAttribute('data-skip')) seek(audio.currentTime + Number(button.dataset.skip));
        if (button.hasAttribute('data-seek')) seek(Number(button.dataset.seek) / 1000);
    }, { signal: abort.signal });
    root.querySelector('[data-speed]').addEventListener('change', event => { audio.playbackRate = Number(event.target.value); }, { signal: abort.signal });
    if (audio.readyState >= 1) ready();
}
