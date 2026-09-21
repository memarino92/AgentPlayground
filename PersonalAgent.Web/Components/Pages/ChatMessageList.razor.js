const handlers = new WeakMap();

export function revealMessage(root, sequence) {
    const message = Array.from(root.querySelectorAll('[data-sequence]'))
        .find(element => element.dataset.sequence === String(sequence));
    message?.scrollIntoView({ block: 'center', behavior: 'instant' });
}

export function initialize(root) {
    dispose(root);
    const handler = event => {
        const link = event.target.closest('a');
        if (!link || !root.contains(link)) return;
        const message = link.closest('.message-list__content');
        const button = Array.from(message?.querySelectorAll('button[data-evidence-url]') ?? [])
            .find(candidate => candidate.dataset.evidenceUrl === link.getAttribute('href'));
        if (!button) return;
        event.preventDefault();
        event.stopPropagation();
        button.click();
    };
    root.addEventListener('click', handler);
    handlers.set(root, handler);
}

export function dispose(root) {
    const handler = handlers.get(root);
    if (handler) root.removeEventListener('click', handler);
    handlers.delete(root);
}
