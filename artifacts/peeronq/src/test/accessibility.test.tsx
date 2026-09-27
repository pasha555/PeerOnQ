import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { StatusBadge } from '../components/StatusBadge';
import { DeviceId } from '../components/DeviceId';
import React from 'react';

describe('Accessibility Requirements', () => {
  it('StatusBadge uses role="status"', () => {
    render(<StatusBadge status="online" />);
    const badge = screen.getByRole('status');
    expect(badge).toBeInTheDocument();
    expect(badge).toHaveTextContent('Online');
  });

  it('interactive elements have data-testid and aria-labels', () => {
    render(<DeviceId id="123-456-789-012" />);
    
    const container = screen.getByTestId('device-id-display');
    expect(container).toBeInTheDocument();
    
    const copyButton = screen.getByRole('button', { name: /Copy device ID/i });
    expect(copyButton).toBeInTheDocument();
    expect(copyButton).toHaveAttribute('aria-label');
  });
});
