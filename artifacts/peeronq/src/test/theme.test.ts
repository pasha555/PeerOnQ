import { renderHook, act } from '@testing-library/react';
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { useTheme } from '../hooks/useTheme';

Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: vi.fn().mockImplementation(query => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(),
  })),
});

describe('useTheme', () => {
  beforeEach(() => {
    window.localStorage.clear();
    document.documentElement.className = '';
  });

  it('defaults to system when no localStorage', () => {
    const { result } = renderHook(() => useTheme());
    expect(result.current.theme).toBe('system');
  });

  it('persists to localStorage', () => {
    const { result } = renderHook(() => useTheme());
    act(() => {
      result.current.setTheme('dark');
    });
    expect(window.localStorage.getItem('peeronq_theme')).toBe('dark');
    expect(result.current.theme).toBe('dark');
  });

  it('toggles .dark class on document.documentElement', () => {
    const { result } = renderHook(() => useTheme());
    act(() => {
      result.current.setTheme('dark');
    });
    expect(document.documentElement.classList.contains('dark')).toBe(true);
    
    act(() => {
      result.current.setTheme('light');
    });
    expect(document.documentElement.classList.contains('dark')).toBe(false);
    expect(document.documentElement.classList.contains('light')).toBe(true);
  });
});
