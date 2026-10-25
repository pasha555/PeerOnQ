import { renderHook, act } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { useSettings } from '../features/settings/useSettings';

describe('useSettings', () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it('default settings returned when no localStorage', () => {
    const { result } = renderHook(() => useSettings());
    expect(result.current.settings.analytics).toBe(false);
    expect(result.current.settings.unattendedAccessEnabled).toBe(false);
    expect(result.current.settings.theme).toBe('system');
  });

  it('settings persist on write', () => {
    const { result } = renderHook(() => useSettings());
    act(() => {
      result.current.updateSetting('theme', 'dark');
      result.current.updateSetting('analytics', true);
    });
    
    expect(result.current.settings.theme).toBe('dark');
    expect(result.current.settings.analytics).toBe(true);
    
    const stored = JSON.parse(window.localStorage.getItem('peeronq_settings') || '{}');
    expect(stored.theme).toBe('dark');
    expect(stored.analytics).toBe(true);
  });
});
