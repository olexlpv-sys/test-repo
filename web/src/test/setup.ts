import '@testing-library/jest-dom/vitest';
import { afterEach, vi } from 'vitest';
import { cleanup } from '@testing-library/react';

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.restoreAllMocks();
});

// jsdom lacks these browser APIs used by Mantine.
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  }),
});
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
window.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver;
window.HTMLElement.prototype.scrollIntoView = () => {};

// Mantine's autosizing textarea listens to font loading.
Object.defineProperty(document, 'fonts', {
  configurable: true,
  value: { addEventListener: () => {}, removeEventListener: () => {}, ready: Promise.resolve() },
});

// jsdom lays nothing out: the virtualized scroll containers (TanStack Virtual reads offsetWidth/offsetHeight) get a
// viewport-sized box so their first rows render.
const scrollBox = (element: HTMLElement) =>
  element.classList.contains('dh-page-scroll') || element.classList.contains('dh-tree-body');
for (const [property, size] of [
  ['offsetHeight', 900],
  ['offsetWidth', 800],
] as const) {
  const original = Object.getOwnPropertyDescriptor(window.HTMLElement.prototype, property);
  Object.defineProperty(window.HTMLElement.prototype, property, {
    configurable: true,
    get(this: HTMLElement) {
      return scrollBox(this) ? size : ((original?.get?.call(this) as number | undefined) ?? 0);
    },
  });
}
