import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../services/apiClient';
import type { PlatformUpgradeAcceptedV1, PlatformUpgradeStatus } from '../types/api';

const mocks = vi.hoisted(() => ({
  online: true,
  releaseAccess: true,
  ownerAccess: true,
  logout: vi.fn(),
  api: {
    getPlatformUpgradeStatus: vi.fn(),
    stagePlatformUpgrade: vi.fn(),
    applyPlatformUpgrade: vi.fn(),
    rollbackPlatformUpgrade: vi.fn(),
  },
}));

vi.mock('../auth/AuthProvider', () => ({
  useAuth: () => ({
    api: mocks.api,
    session: { roles: ['ReleaseManager'] },
    logout: mocks.logout,
    can: (policy: string) => policy === 'admin.release' ? mocks.releaseAccess
      : policy === 'admin.platform-upgrade' ? mocks.ownerAccess : false,
  }),
}));

vi.mock('../hooks/useOnlineStatus', () => ({
  useOnlineStatus: () => mocks.online,
}));

import {
  UpgradePage,
  isTransientUpgradeState,
  platformAuditReasonValidationError,
  platformStageValidationError,
} from '../pages/UpgradePage';
import { AppShell } from '../components/AppShell';

function status(overrides: Partial<PlatformUpgradeStatus> = {}): PlatformUpgradeStatus {
  return {
    enabled: true,
    environment: 'Production',
    schemaVersion: 1,
    state: 'ready',
    operationId: 'a'.repeat(32),
    currentVersion: '0.9.57',
    targetVersion: '0.9.58',
    rollbackVersion: '0.9.56',
    progressPercent: 100,
    canApply: true,
    canRollback: true,
    blockingReason: null,
    message: 'All checks passed.',
    logReference: 'upgrade-a1b2c3',
    updatedAtUtc: '2026-08-24T17:00:00Z',
    checks: [{ code: 'signature', label: 'Bundle signature', state: 'passed', message: 'Signature is valid.' }],
    ...overrides,
  };
}

function accepted(
  action: PlatformUpgradeAcceptedV1['action'],
  targetVersion: string,
  requestId = 'b'.repeat(32),
): PlatformUpgradeAcceptedV1 {
  return { requestId, action, targetVersion, state: 'queued', acceptedAtUtc: '2026-08-24T17:01:00Z' };
}

describe('Platform Upgrade page', () => {
  beforeEach(() => {
    mocks.online = true;
    mocks.releaseAccess = true;
    mocks.ownerAccess = true;
    mocks.api.getPlatformUpgradeStatus.mockReset();
    mocks.api.stagePlatformUpgrade.mockReset();
    mocks.api.applyPlatformUpgrade.mockReset();
    mocks.api.rollbackPlatformUpgrade.mockReset();
    mocks.logout.mockReset();
    mocks.api.getPlatformUpgradeStatus.mockResolvedValue(status());
    mocks.api.stagePlatformUpgrade.mockResolvedValue(accepted('stage', '0.9.58'));
    mocks.api.applyPlatformUpgrade.mockResolvedValue(accepted('apply', '0.9.58'));
    mocks.api.rollbackPlatformUpgrade.mockResolvedValue(accepted('rollback', '0.9.56'));
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('recognizes only controller transient states and validates the signed artifact triplet', () => {
    expect(isTransientUpgradeState('queued')).toBe(true);
    expect(isTransientUpgradeState('verifying_deployment')).toBe(true);
    expect(isTransientUpgradeState('succeeded')).toBe(false);
    expect(platformStageValidationError(
      new File(['bundle'], 'release.zip'),
      new File(['digest'], 'release.sha256'),
      new File(['signature'], 'release.asc'),
    )).toMatch(/\.run/);
    expect(platformAuditReasonValidationError('ab')).toMatch(/at least 3/);
    expect(platformAuditReasonValidationError('valid reason\nsecond line')).toMatch(/one line/);
    expect(platformAuditReasonValidationError('Valid production reason')).toBeNull();
  });

  it('lets a Release Manager inspect and stage while keeping Owner controls absent', async () => {
    mocks.ownerAccess = false;
    mocks.api.getPlatformUpgradeStatus.mockResolvedValue(status({ state: 'idle', targetVersion: null, progressPercent: 0, canApply: false }));
    const user = userEvent.setup();
    render(<UpgradePage />);

    expect(await screen.findByRole('heading', { name: 'Platform upgrade' })).toBeVisible();
    expect(screen.getByText('0.9.57')).toBeVisible();
    expect(screen.getByText('Bundle signature')).toBeVisible();
    expect(screen.getByText('Owner approval required')).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Apply to production' })).not.toBeInTheDocument();

    const bundleInput = screen.getByLabelText(/^Platform bundle \(\.run\)/) as HTMLInputElement;
    const checksumInput = screen.getByLabelText(/^Checksum \(\.sha256\)/) as HTMLInputElement;
    const signatureInput = screen.getByLabelText(/^Detached signature \(\.asc\)/) as HTMLInputElement;
    await user.upload(bundleInput,
      new File(['bundle'], 'PeerOnQ-platform-0.9.58.run', { type: 'application/octet-stream' }));
    await user.upload(checksumInput,
      new File(['digest'], 'PeerOnQ-platform-0.9.58.run.sha256', { type: 'text/plain' }));
    await user.upload(signatureInput,
      new File(['signature'], 'PeerOnQ-platform-0.9.58.run.asc', { type: 'application/pgp-signature' }));
    await user.type(screen.getByLabelText('Audit reason'), 'Stage full production release');
    expect(bundleInput.files).toHaveLength(1);
    expect(checksumInput.files).toHaveLength(1);
    expect(signatureInput.files).toHaveLength(1);
    const stageButton = screen.getByRole('button', { name: 'Stage signed release' });
    expect(stageButton).toBeEnabled();
    await user.click(stageButton);

    await waitFor(() => expect(mocks.api.stagePlatformUpgrade).toHaveBeenCalledTimes(1));
    expect(mocks.api.stagePlatformUpgrade.mock.calls[0].slice(0, 3).map((file: File) => file.name)).toEqual([
      'PeerOnQ-platform-0.9.58.run',
      'PeerOnQ-platform-0.9.58.run.sha256',
      'PeerOnQ-platform-0.9.58.run.asc',
    ]);
    expect(mocks.api.stagePlatformUpgrade.mock.calls[0][3]).toBe('Stage full production release');
  });

  it('requires an Owner to type the exact target version before applying', async () => {
    const user = userEvent.setup();
    render(<UpgradePage />);
    await screen.findByRole('heading', { name: 'Platform upgrade' });

    await user.click(screen.getByRole('button', { name: 'Apply to production' }));
    const dialog = screen.getByRole('dialog', { name: 'Apply platform 0.9.58' });
    const confirm = within(dialog).getByRole('button', { name: 'Apply to production' });
    await user.type(within(dialog).getByLabelText('Type 0.9.58 to confirm'), '0.9.5');
    await user.type(within(dialog).getByLabelText('Audit reason'), 'Apply verified release');
    expect(confirm).toBeDisabled();
    await user.type(within(dialog).getByLabelText('Type 0.9.58 to confirm'), '8');
    expect(confirm).toBeEnabled();
    await user.click(confirm);

    await waitFor(() => expect(mocks.api.applyPlatformUpgrade).toHaveBeenCalledWith(
      '0.9.58', '0.9.57', 'Apply verified release',
    ));
    expect(screen.getByText('All checks passed.')).toBeVisible();
    expect(screen.getByText(new RegExp(`apply request ${'b'.repeat(32)} was accepted`, 'i'))).toBeVisible();
  });

  it('requires the exact retained version before rollback', async () => {
    const user = userEvent.setup();
    render(<UpgradePage />);
    await screen.findByRole('heading', { name: 'Platform upgrade' });

    await user.click(screen.getByRole('button', { name: 'Rollback platform' }));
    const dialog = screen.getByRole('dialog', { name: 'Rollback platform 0.9.56' });
    await user.type(within(dialog).getByLabelText('Type 0.9.56 to confirm'), '0.9.56');
    await user.type(within(dialog).getByLabelText('Audit reason'), 'Recover previous release');
    await user.click(within(dialog).getByRole('button', { name: 'Rollback platform' }));

    await waitFor(() => expect(mocks.api.rollbackPlatformUpgrade).toHaveBeenCalledWith(
      '0.9.56', '0.9.57', 'Recover previous release',
    ));
  });

  it('preserves the last authoritative state across a temporary controller restart and stops polling on unmount', async () => {
    vi.useFakeTimers();
    mocks.api.getPlatformUpgradeStatus
      .mockResolvedValueOnce(status({ state: 'applying', progressPercent: 35, message: 'Applying services.' }))
      .mockRejectedValueOnce(new ApiError(0, 'network_unavailable', 'The PeerOnQ service could not be reached.'));
    const view = render(<UpgradePage />);
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByText('Applying services.')).toBeVisible();

    await act(async () => { await vi.advanceTimersByTimeAsync(2_000); });
    expect(mocks.api.getPlatformUpgradeStatus).toHaveBeenCalledTimes(2);
    expect(screen.getByText('Applying services.')).toBeVisible();
    expect(screen.getByText(/last authoritative status remains visible/i)).toBeVisible();

    view.unmount();
    await act(async () => { await vi.advanceTimersByTimeAsync(4_000); });
    expect(mocks.api.getPlatformUpgradeStatus).toHaveBeenCalledTimes(2);
  });

  it('polls through an immediate old-status race, then follows the matching request to its terminal state', async () => {
    vi.useFakeTimers();
    const requestId = 'b'.repeat(32);
    mocks.api.getPlatformUpgradeStatus.mockReset()
      .mockResolvedValueOnce(status({ operationId: 'a'.repeat(32), state: 'ready', message: 'Old ready status.' }))
      .mockResolvedValueOnce(status({ operationId: 'a'.repeat(32), state: 'ready', message: 'Old status still visible.' }))
      .mockResolvedValueOnce(status({ operationId: requestId, state: 'queued', progressPercent: 5, canApply: false, message: 'Request is queued.' }))
      .mockResolvedValueOnce(status({ operationId: requestId, state: 'succeeded', currentVersion: '0.9.58', progressPercent: 100, canApply: false, message: 'Deployment succeeded.' }));
    mocks.api.applyPlatformUpgrade.mockResolvedValueOnce(accepted('apply', '0.9.58', requestId));
    const view = render(<UpgradePage />);
    await act(async () => { await Promise.resolve(); });

    fireEvent.click(screen.getByRole('button', { name: 'Apply to production' }));
    const dialog = screen.getByRole('dialog', { name: 'Apply platform 0.9.58' });
    fireEvent.change(within(dialog).getByLabelText('Type 0.9.58 to confirm'), { target: { value: '0.9.58' } });
    fireEvent.change(within(dialog).getByLabelText('Audit reason'), { target: { value: 'Apply verified release' } });
    await act(async () => {
      fireEvent.click(within(dialog).getByRole('button', { name: 'Apply to production' }));
      await Promise.resolve();
    });

    expect(screen.getByText('Old ready status.')).toBeVisible();
    expect(mocks.api.getPlatformUpgradeStatus).toHaveBeenCalledTimes(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(2_000); });
    expect(screen.getByText('Old status still visible.')).toBeVisible();
    expect(mocks.api.getPlatformUpgradeStatus).toHaveBeenCalledTimes(2);

    await act(async () => { await vi.advanceTimersByTimeAsync(2_000); });
    expect(screen.getByText('Request is queued.')).toBeVisible();
    expect(mocks.api.getPlatformUpgradeStatus).toHaveBeenCalledTimes(3);

    await act(async () => { await vi.advanceTimersByTimeAsync(2_000); });
    expect(screen.getByText('Deployment succeeded.')).toBeVisible();
    expect(mocks.api.getPlatformUpgradeStatus).toHaveBeenCalledTimes(4);
    await act(async () => { await vi.advanceTimersByTimeAsync(4_000); });
    expect(mocks.api.getPlatformUpgradeStatus).toHaveBeenCalledTimes(4);
    view.unmount();
  });

  it('ignores an older overlapping poll response after a newer refresh response', async () => {
    vi.useFakeTimers();
    let resolvePoll!: (value: PlatformUpgradeStatus) => void;
    let resolveRefresh!: (value: PlatformUpgradeStatus) => void;
    mocks.api.getPlatformUpgradeStatus.mockReset()
      .mockResolvedValueOnce(status({ state: 'applying', progressPercent: 20, message: 'Initial apply status.' }))
      .mockImplementationOnce(() => new Promise<PlatformUpgradeStatus>((resolve) => { resolvePoll = resolve; }))
      .mockImplementationOnce(() => new Promise<PlatformUpgradeStatus>((resolve) => { resolveRefresh = resolve; }));
    render(<UpgradePage />);
    await act(async () => { await Promise.resolve(); });

    await act(async () => { await vi.advanceTimersByTimeAsync(2_000); });
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await act(async () => {
      resolveRefresh(status({
        state: 'succeeded',
        operationId: 'b'.repeat(32),
        currentVersion: '0.9.58',
        progressPercent: 100,
        canApply: false,
        message: 'Newer terminal status.',
        updatedAtUtc: '2026-08-24T17:03:00Z',
      }));
      await Promise.resolve();
    });
    expect(screen.getByText('Newer terminal status.')).toBeVisible();

    await act(async () => {
      resolvePoll(status({
        state: 'applying',
        progressPercent: 40,
        canApply: false,
        message: 'Older delayed status.',
        updatedAtUtc: '2026-08-24T17:02:00Z',
      }));
      await Promise.resolve();
    });
    expect(screen.getByText('Newer terminal status.')).toBeVisible();
    expect(screen.queryByText('Older delayed status.')).not.toBeInTheDocument();
  });

  it('stops tracking an accepted request when a newer host operation supersedes it', async () => {
    vi.useFakeTimers();
    const acceptedRequestId = 'b'.repeat(32);
    const newerRequestId = 'c'.repeat(32);
    mocks.api.getPlatformUpgradeStatus.mockReset()
      .mockResolvedValueOnce(status({ operationId: 'a'.repeat(32), state: 'ready', updatedAtUtc: '2026-08-24T17:00:00Z' }))
      .mockResolvedValueOnce(status({
        operationId: newerRequestId,
        state: 'queued',
        progressPercent: 5,
        canApply: false,
        message: 'A newer operation is queued.',
        updatedAtUtc: '2026-08-24T17:02:00Z',
      }))
      .mockResolvedValueOnce(status({
        operationId: newerRequestId,
        state: 'succeeded',
        currentVersion: '0.9.59',
        progressPercent: 100,
        canApply: false,
        message: 'The newer operation completed.',
        updatedAtUtc: '2026-08-24T17:03:00Z',
      }));
    mocks.api.applyPlatformUpgrade.mockResolvedValueOnce(accepted('apply', '0.9.58', acceptedRequestId));
    render(<UpgradePage />);
    await act(async () => { await Promise.resolve(); });

    fireEvent.click(screen.getByRole('button', { name: 'Apply to production' }));
    const dialog = screen.getByRole('dialog', { name: 'Apply platform 0.9.58' });
    fireEvent.change(within(dialog).getByLabelText('Type 0.9.58 to confirm'), { target: { value: '0.9.58' } });
    fireEvent.change(within(dialog).getByLabelText('Audit reason'), { target: { value: 'Apply verified release' } });
    await act(async () => {
      fireEvent.click(within(dialog).getByRole('button', { name: 'Apply to production' }));
      await Promise.resolve();
    });

    await act(async () => { await vi.advanceTimersByTimeAsync(2_000); });
    expect(screen.getByText(/another platform operation superseded this tab's accepted request/i)).toBeVisible();
    expect(screen.getByText('A newer operation is queued.')).toBeVisible();

    await act(async () => { await vi.advanceTimersByTimeAsync(2_000); });
    expect(screen.getByText('The newer operation completed.')).toBeVisible();
    await act(async () => { await vi.advanceTimersByTimeAsync(4_000); });
    expect(mocks.api.getPlatformUpgradeStatus).toHaveBeenCalledTimes(3);
  });

  it('ends accepted-request tracking after the bounded host publication deadline', async () => {
    vi.useFakeTimers();
    mocks.api.getPlatformUpgradeStatus.mockReset()
      .mockResolvedValueOnce(status({ operationId: 'a'.repeat(32), state: 'ready' }))
      .mockRejectedValue(new ApiError(0, 'network_unavailable', 'Controller unavailable.'));
    render(<UpgradePage />);
    await act(async () => { await Promise.resolve(); });

    fireEvent.click(screen.getByRole('button', { name: 'Apply to production' }));
    const dialog = screen.getByRole('dialog', { name: 'Apply platform 0.9.58' });
    fireEvent.change(within(dialog).getByLabelText('Type 0.9.58 to confirm'), { target: { value: '0.9.58' } });
    fireEvent.change(within(dialog).getByLabelText('Audit reason'), { target: { value: 'Apply verified release' } });
    await act(async () => {
      fireEvent.click(within(dialog).getByRole('button', { name: 'Apply to production' }));
      await Promise.resolve();
    });

    await act(async () => { await vi.advanceTimersByTimeAsync(120_000); });
    expect(screen.getByText(/did not publish this tab's accepted request before the tracking deadline/i)).toBeVisible();
    const callsAtDeadline = mocks.api.getPlatformUpgradeStatus.mock.calls.length;
    await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });
    expect(mocks.api.getPlatformUpgradeStatus).toHaveBeenCalledTimes(callsAtDeadline);
  });

  it('denies the direct route without an MFA release role and does not call the status API', () => {
    mocks.releaseAccess = false;
    render(<UpgradePage />);
    expect(screen.getByRole('alert')).toHaveTextContent('Permission denied');
    expect(mocks.api.getPlatformUpgradeStatus).not.toHaveBeenCalled();
  });

  it('allows release preparation while keeping stage, apply, and rollback unavailable until the controller knows the current version', async () => {
    mocks.api.getPlatformUpgradeStatus.mockResolvedValue(status({
      state: 'idle',
      currentVersion: null,
      targetVersion: '0.9.58',
      rollbackVersion: '0.9.56',
      canApply: true,
      canRollback: true,
    }));
    render(<UpgradePage />);
    await screen.findByRole('heading', { name: 'Platform upgrade' });

    const bundleInput = screen.getByLabelText(/^Platform bundle \(\.run\)/) as HTMLInputElement;
    expect(bundleInput).toBeEnabled();
    fireEvent.change(bundleInput, {
      target: { files: [new File(['bundle'], 'peeronq-server-0.9.58.run')] },
    });
    expect(bundleInput.files).toHaveLength(1);
    expect(screen.getByText(/controller reports the current version/i)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Stage signed release' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Apply to production' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Rollback platform' })).toBeDisabled();
  });

  it('keeps file selection available when the development host updater is not configured', async () => {
    mocks.api.getPlatformUpgradeStatus.mockResolvedValue(status({
      enabled: false,
      environment: 'Development',
      state: 'idle',
      operationId: null,
      currentVersion: null,
      targetVersion: null,
      rollbackVersion: null,
      progressPercent: 0,
      canApply: false,
      canRollback: false,
      blockingReason: 'platform_upgrade_disabled',
      message: 'Platform upgrades are not configured.',
      checks: [],
    }));
    render(<UpgradePage />);
    await screen.findByRole('heading', { name: 'Platform upgrade' });

    const bundleInput = screen.getByLabelText(/^Platform bundle \(\.run\)/) as HTMLInputElement;
    expect(bundleInput).toBeEnabled();
    fireEvent.change(bundleInput, {
      target: { files: [new File(['bundle'], 'peeronq-server-0.9.58.run')] },
    });
    expect(bundleInput.files).toHaveLength(1);
    expect(screen.getByText(/host updater is configured/i)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Stage signed release' })).toBeDisabled();
    expect(mocks.api.stagePlatformUpgrade).not.toHaveBeenCalled();
  });

  it('shows the Upgrade navigation only to an MFA release role', () => {
    const first = render(<AppShell><div>Content</div></AppShell>);
    expect(screen.getByRole('link', { name: 'Upgrade' })).toHaveAttribute('href', '/upgrade');
    first.unmount();

    mocks.releaseAccess = false;
    render(<AppShell><div>Content</div></AppShell>);
    expect(screen.queryByRole('link', { name: 'Upgrade' })).not.toBeInTheDocument();
  });
});
