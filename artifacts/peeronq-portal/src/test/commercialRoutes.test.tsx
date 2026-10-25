import { render, screen } from '@testing-library/react';
import { vi } from 'vitest';
import { App } from '../App';

vi.mock('../auth', () => ({ useAuth: () => ({ status: 'authenticated' }) }));
vi.mock('../shell', async () => {
  const React = await import('react');
  return {
    OrganizationProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
    Shell: ({ children }: { children: React.ReactNode }) => <>{children}</>,
    useOrganization: () => ({ organizations: [], selected: null, loading: false, reload: async () => undefined }),
  };
});

describe.each(['/billing', '/pricing', '/invoices', '/subscriptions', '/entitlements'])('commercial route %s', (path) => {
  it('is absent from the active portal', () => {
    window.history.replaceState({}, '', path);
    render(<App />);
    expect(screen.getByRole('heading', { name: 'Page not found' })).toBeVisible();
  });
});
