import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { ActionDialog } from '../components/ActionDialog';
import { StatePanel } from '../components/StatePanel';
import { StatusPill } from '../components/StatusPill';
import { ApiError } from '../services/apiClient';
import { resourceConfigs } from '../pages/resourceConfig';
import { InfrastructureMetricsContent } from '../pages/ResourcePage';
import { canAccessPolicy } from '../auth/AuthProvider';
import type { AdminSession, InfrastructureMetricsResponse } from '../types/api';

describe('administration UI state contracts', () => {
  it('exposes privileged confirmations as a labelled modal and keeps invalid actions disabled', async () => {
    const user = userEvent.setup();
    render(
      <ActionDialog
        title="Revoke administrator session"
        description="Immediately invalidates the selected session."
        confirmLabel="Revoke session"
        destructive
        confirmDisabled
        onClose={() => undefined}
        onConfirm={() => undefined}
      >
        <label htmlFor="reason">Audit reason</label><textarea id="reason" />
      </ActionDialog>,
    );

    expect(screen.getByRole('dialog', { name: 'Revoke administrator session' })).toHaveAccessibleDescription('Immediately invalidates the selected session.');
    const confirm = screen.getByRole('button', { name: 'Revoke session' });
    expect(confirm).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Close dialog' }));
  });

  it('explains permission denial without presenting a retry action', () => {
    render(<StatePanel state="error" error={new ApiError(403, 'forbidden', 'Role denied.')} onRetry={() => undefined} />);
    expect(screen.getByRole('alert')).toHaveTextContent('Permission denied');
    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
  });

  it('pairs status text with a non-color indicator', () => {
    render(<StatusPill value="Degraded" />);
    expect(screen.getByText('Degraded')).toBeVisible();
  });

  it('uses distinct semantic tones for failed and recovered platform upgrades', () => {
    const view = render(<StatusPill value="Failed" />);
    expect(screen.getByText('Failed')).toHaveClass('danger');
    view.rerender(<StatusPill value="Rolled back" />);
    expect(screen.getByText('Rolled back')).toHaveClass('success');
  });

  it('defines every required Phase 6 administration route without raw device or installation identifier columns', () => {
    expect(Object.keys(resourceConfigs)).toEqual([
      'devices', 'installations', 'presence', 'sessions', 'downloads', 'releases', 'diagnostics', 'infrastructure', 'audit', 'alerts',
    ]);
    const deviceColumns = resourceConfigs.devices.columns.map((column) => column.key);
    expect(deviceColumns).toContain('maskedPublicDeviceId');
    expect(deviceColumns).not.toContain('deviceId');
    for (const resource of Object.values(resourceConfigs)) {
      const columns = resource.columns.map((column) => column.key);
      expect(columns).not.toContain('deviceId');
      expect(columns).not.toContain('installationId');
    }
    expect(resourceConfigs.sessions.columns.map((column) => column.key)).toEqual(expect.arrayContaining(['maskedViewer', 'maskedHost']));
    expect(Object.fromEntries(Object.entries(resourceConfigs).map(([name, config]) => [name, config.defaultSort]))).toEqual({
      devices: 'lastSeen',
      installations: 'lastSeen',
      presence: 'expiresAt',
      sessions: 'startedAt',
      downloads: 'startedAt',
      releases: 'publishedAt',
      diagnostics: 'createdAt',
      infrastructure: 'observedAt',
      audit: 'timestamp',
      alerts: 'startedAt',
    });
  });

  it('mirrors role policies conservatively while leaving final authorization to the API', () => {
    const session = (roles: AdminSession['roles'], mfaEnabled: boolean): AdminSession => ({
      userId: '00000000-0000-0000-0000-000000000001',
      roles,
      mfaEnabled,
      expiresAtUtc: '2026-08-11T12:00:00Z',
      releasePublicationEnabled: false,
      websitePublicationEnabled: false,
    });

    expect(canAccessPolicy(session(['ReadOnlyAnalyst'], false), 'admin.read')).toBe(true);
    expect(canAccessPolicy(session(['ReadOnlyAnalyst'], false), 'admin.diagnostics')).toBe(false);
    expect(canAccessPolicy(session(['SupportAgent'], true), 'admin.diagnostics')).toBe(true);
    expect(canAccessPolicy(session(['SupportAgent'], true), 'admin.operations')).toBe(false);
    expect(canAccessPolicy(session(['OperationsAdministrator'], true), 'admin.operations')).toBe(true);
    expect(canAccessPolicy(session(['Owner'], false), 'admin.security')).toBe(false);
    expect(canAccessPolicy(session(['ReleaseManager'], true), 'admin.release')).toBe(true);
    expect(canAccessPolicy(session(['ReleaseManager'], true), 'admin.platform-upgrade')).toBe(false);
    expect(canAccessPolicy(session(['Owner'], true), 'admin.platform-upgrade')).toBe(true);
  });

  it('renders explicit loading and unavailable infrastructure metric states', () => {
    const onRetry = () => undefined;
    const { rerender } = render(
      <InfrastructureMetricsContent data={null} error={null} loading online onRetry={onRetry} />,
    );
    expect(screen.getByText('Loading infrastructure metrics')).toBeVisible();

    const unavailable = {
      state: 'unavailable',
      observedAtUtc: '2026-08-11T12:00:00Z',
      apiLatencyP95Milliseconds: { state: 'unavailable', value: null, unit: 'milliseconds' },
      webSocketConnections: { state: 'unavailable', value: null, unit: 'connections' },
      turnAllocationsPerSecond: { state: 'unavailable', value: null, unit: 'allocations_per_second' },
      turnBandwidthBytesPerSecond: { state: 'unavailable', value: null, unit: 'bytes_per_second' },
      databasePoolUsage: { state: 'unavailable', value: null, unit: 'connections' },
      queueDepth: { state: 'unavailable', value: null, unit: 'items' },
    } satisfies InfrastructureMetricsResponse;
    rerender(<InfrastructureMetricsContent data={unavailable} error={null} loading={false} online onRetry={onRetry} />);
    expect(screen.getByText('Metrics source unavailable')).toBeVisible();
  });

  it('keeps available infrastructure values visible when the response is partial', () => {
    const partial = {
      state: 'partial',
      observedAtUtc: '2026-08-11T12:00:00Z',
      apiLatencyP95Milliseconds: { state: 'available', value: 12.5, unit: 'milliseconds' },
      webSocketConnections: { state: 'available', value: 42, unit: 'connections' },
      turnAllocationsPerSecond: { state: 'unavailable', value: null, unit: 'allocations_per_second' },
      turnBandwidthBytesPerSecond: { state: 'available', value: 1_000_000, unit: 'bytes_per_second' },
      databasePoolUsage: { state: 'available', value: 8, unit: 'connections' },
      queueDepth: { state: 'available', value: 3, unit: 'items' },
    } satisfies InfrastructureMetricsResponse;

    render(<InfrastructureMetricsContent data={partial} error={null} loading={false} online onRetry={() => undefined} />);
    expect(screen.getByRole('status')).toHaveTextContent('Some infrastructure measurements are unavailable');
    expect(screen.getByText('12.5 ms')).toBeVisible();
    expect(screen.getByText('42')).toBeVisible();
    expect(screen.getByText('8.0 Mb/s')).toBeVisible();
    expect(screen.getByText('Metric unavailable')).toBeVisible();
  });

  it('keeps the infrastructure page renderable when one external metric is missing', () => {
    const malformed = {
      state: 'partial',
      observedAtUtc: '2026-08-11T12:00:00Z',
      apiLatencyP95Milliseconds: { state: 'available', value: 12.5, unit: 'milliseconds' },
      webSocketConnections: undefined,
      turnAllocationsPerSecond: { state: 'unavailable', value: null, unit: 'allocations_per_second' },
      turnBandwidthBytesPerSecond: { state: 'unavailable', value: null, unit: 'bytes_per_second' },
      databasePoolUsage: { state: 'unavailable', value: null, unit: 'connections' },
      queueDepth: { state: 'unavailable', value: null, unit: 'items' },
    } as unknown as InfrastructureMetricsResponse;

    const view = render(<InfrastructureMetricsContent data={malformed} error={null} loading={false} online onRetry={() => undefined} />);
    expect(view.container.querySelector('#live-infrastructure-metrics')).toHaveTextContent('Live infrastructure metrics');
    expect([...view.container.querySelectorAll('small')].filter((node) => node.textContent === 'Metric unavailable')).toHaveLength(5);
  });
});
