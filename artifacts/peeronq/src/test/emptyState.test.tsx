import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { EmptyState } from '../components/EmptyState';
import React from 'react';

describe('EmptyState', () => {
  it('renders title and description', () => {
    render(<EmptyState title="No items" description="Please add an item" />);
    
    expect(screen.getByText('No items')).toBeInTheDocument();
    expect(screen.getByText('Please add an item')).toBeInTheDocument();
  });
});
