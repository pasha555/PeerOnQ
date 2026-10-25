import '@testing-library/jest-dom/vitest';
import { afterEach, beforeEach, vi } from 'vitest';
import { cleanup } from '@testing-library/react';

// jsdom has no native modal implementation. These model open/close only;
// browser focus containment and visual layout need a real browser check.
HTMLDialogElement.prototype.showModal = function () { this.setAttribute('open', ''); };
HTMLDialogElement.prototype.close = function () { this.removeAttribute('open'); this.dispatchEvent(new Event('close')); };

beforeEach(() => { window.localStorage.clear(); window.sessionStorage.clear(); document.documentElement.classList.remove('dark'); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); window.history.replaceState({}, '', '/'); });
