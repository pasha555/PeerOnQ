import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi } from 'vitest';
import { PermissionDialog } from '../components/PermissionDialog';
import { ConnectionForm } from '../components/ConnectionForm';
import React from 'react';

// Mock matchMedia for Dialog
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

// Mock ResizeObserver for Dialog
class ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
window.ResizeObserver = ResizeObserver;

describe('PermissionDialog', () => {
  it('opens on connect button click in ConnectionForm', async () => {
    const user = userEvent.setup();
    render(<ConnectionForm />);
    
    const input = screen.getByPlaceholderText(/Enter PeerOnQ ID/i);
    await user.type(input, '123-456-789-012');
    
    const connectButton = screen.getByRole('button', { name: /Connect/i });
    await user.click(connectButton);
    
    // Check if dialog opens
    expect(await screen.findByRole('dialog')).toBeInTheDocument();
    expect(screen.getByText(/Frontend Prototype/i)).toBeInTheDocument();
    
    // Check if ID is displayed
    expect(screen.getByText('123-456-789-012')).toBeInTheDocument();
    
    // Check if it closes on "Understood"
    const understoodBtn = screen.getByRole('button', { name: /Understood/i });
    await user.click(understoodBtn);
    
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });

  it('closes on Escape', async () => {
    const onOpenChange = vi.fn();
    render(<PermissionDialog open={true} onOpenChange={onOpenChange} deviceId="111-222-333-444" />);

    expect(screen.getByRole('dialog')).toBeInTheDocument();

    fireEvent.keyDown(document.body, { key: 'Escape', code: 'Escape', keyCode: 27 });

    await waitFor(() => {
      expect(onOpenChange).toHaveBeenCalledWith(false);
    });
  });

  it('is keyboard accessible: labelled, focused, and focus stays trapped', async () => {
    const user = userEvent.setup();
    render(<PermissionDialog open={true} onOpenChange={vi.fn()} deviceId="111-222-333-444" mode="control" />);

    const dialog = screen.getByRole('dialog');
    expect(dialog).toHaveAccessibleName();

    // Focus must move into the dialog when it opens.
    await waitFor(() => {
      expect(dialog.contains(document.activeElement)).toBe(true);
    });

    // Tabbing through every focusable element must never escape the dialog.
    for (let i = 0; i < 8; i++) {
      await user.tab();
      expect(dialog.contains(document.activeElement)).toBe(true);
    }
  });
});
