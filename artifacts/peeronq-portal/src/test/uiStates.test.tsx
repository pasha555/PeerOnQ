import { render, screen } from '@testing-library/react';
import { EmptyState, ErrorState, LoadingState } from '../components';

describe('portal async states', () => {
  it('renders an accessible loading state', () => {
    render(<LoadingState label="Loading tenant devices…" />);
    expect(screen.getByRole('status')).toHaveTextContent('Loading tenant devices');
  });

  it('renders an actionable error state', () => {
    render(<ErrorState message="Tenant service unavailable" retry={() => undefined} />);
    expect(screen.getByRole('alert')).toHaveTextContent('Tenant service unavailable');
    expect(screen.getByRole('button', { name: 'Retry' })).toBeEnabled();
  });

  it('renders an explicit empty state', () => {
    render(<EmptyState title="No managed devices">Assign a device explicitly.</EmptyState>);
    expect(screen.getByRole('heading', { name: 'No managed devices' })).toBeVisible();
  });
});
