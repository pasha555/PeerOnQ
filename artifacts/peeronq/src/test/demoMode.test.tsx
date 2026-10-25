import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { SessionsPage } from '../pages/SessionsPage';
import React from 'react';

// Mock ResizeObserver for any Shadcn components
class ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
window.ResizeObserver = ResizeObserver;

// Mock window.matchMedia
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: vi.fn().mockImplementation(query => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: vi.fn(), // deprecated
    removeListener: vi.fn(), // deprecated
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(),
  })),
});

// We need to mock import.meta.env for these tests
describe('SessionsPage Demo Mode', () => {
  beforeEach(() => {
    vi.resetModules();
  });

  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it('shows demo banner when VITE_ENABLE_DEMO_DATA=true', async () => {
    vi.stubEnv('VITE_ENABLE_DEMO_DATA', 'true');
    render(<SessionsPage />);
    
    expect(await screen.findByText(/Demo Data Enabled/i)).toBeInTheDocument();
    expect(screen.getByText(/Demo data is enabled. No records represent real activity./i)).toBeInTheDocument();
  });

  it('does NOT show banner when VITE_ENABLE_DEMO_DATA=false', async () => {
    vi.stubEnv('VITE_ENABLE_DEMO_DATA', 'false');
    render(<SessionsPage />);
    
    const banner = screen.queryByText(/Demo Data Enabled/i);
    expect(banner).not.toBeInTheDocument();
  });
});
