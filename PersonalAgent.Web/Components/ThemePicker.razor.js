const storageKey = 'personalagent.color-theme';
const changedMethod = 'OnThemeChanged';
const normalize = value => ['light', 'dark'].includes(value) ? value : 'system';

class ThemeController {
    #reference;
    #media = matchMedia('(prefers-color-scheme: dark)');
    #events = new AbortController();
    #preference = 'system';

    constructor(reference) {
        this.#reference = reference;
        try { this.#preference = normalize(localStorage.getItem(storageKey)); } catch { /* Storage may be blocked. */ }
        this.#media.addEventListener('change', () => this.#apply(), { signal: this.#events.signal });
        window.addEventListener('storage', event => {
            if (event.key !== storageKey && event.key !== null) return;
            this.#preference = normalize(event.newValue);
            this.#apply();
        }, { signal: this.#events.signal });
    }

    async #apply() {
        const dark = this.#preference === 'dark' || (this.#preference === 'system' && this.#media.matches);
        document.documentElement.dataset.theme = dark ? 'dark' : 'light';
        try { await this.#reference.invokeMethodAsync(changedMethod, this.#preference, dark); }
        catch { /* The server circuit may have disconnected. */ }
    }

    async setPreference(value) {
        this.#preference = normalize(value);
        try { localStorage.setItem(storageKey, this.#preference); } catch { /* Keep the choice for this page. */ }
        await this.#apply();
    }

    async initialize() { await this.#apply(); }
    dispose() { this.#events.abort(); }
}

export async function initialize(reference) {
    const controller = new ThemeController(reference);
    await controller.initialize();
    return controller;
}
