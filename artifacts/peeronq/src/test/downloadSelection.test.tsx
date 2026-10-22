import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DownloadsPage } from '../pages/DownloadsPage';

beforeEach(() => {
  vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('Mozilla/5.0 (Windows NT 10.0; Win64; x64)');
  vi.spyOn(navigator, 'platform', 'get').mockReturnValue('Win32');
  vi.stubEnv('VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE', 'true');
  vi.stubEnv('VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION', '0.9.76');
  vi.stubEnv('VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER', 'unsigned-public-pilot');
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllEnvs(); vi.useRealTimers(); Reflect.deleteProperty(navigator, 'userAgentData'); });

function hints(result: Promise<{ architecture?: string; bitness?: string; wow64?: boolean }>, platform?: string) {
  const getHighEntropyValues = vi.fn(() => result);
  Object.defineProperty(navigator, 'userAgentData', { configurable: true, value: { getHighEntropyValues, platform } });
  return getHighEntropyValues;
}

describe('automatic platform and architecture downloads', () => {
  it('automatically recommends x64 for Firefox Windows64 without claiming native detection', () => {
    vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:144.0) Gecko/20100101 Firefox/144.0');
    render(<DownloadsPage />);
    expect(screen.getByRole('link', { name: 'Download PeerOnQ for Windows x64' })).toHaveAttribute('href', '/downloads/PeerOnQ-0.9.76-unsigned-public-pilot-x64.msi');
    expect(screen.getByText('Windows', { selector: 'p' })).toBeVisible();
    expect(screen.getByText('Recommended installer: Windows x64')).toBeVisible();
    expect(screen.queryByText('Windows · x64')).not.toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Native architecture could not be verified');
    expect(screen.getByText('Choose another device or architecture').closest('details')).not.toHaveAttribute('open');
  });

  it('preserves the labeled recommendation when the local Client Hints API throws synchronously', async () => {
    Object.defineProperty(navigator, 'userAgentData', { configurable: true, value: { getHighEntropyValues() { throw new DOMException('Denied', 'SecurityError'); } } });
    render(<DownloadsPage />);
    expect(await screen.findByRole('link', { name: 'Download PeerOnQ for Windows x64' })).toBeVisible();
    expect(screen.getByRole('status')).toHaveTextContent('Native architecture could not be verified');
  });

  it('uses local ARM64 hints instead of the frozen x64 UA without a network request', async () => {
    const localHints = hints(Promise.resolve({ architecture: 'arm', bitness: '64' }));
    const network = vi.spyOn(globalThis, 'fetch');
    render(<DownloadsPage />);
    expect(screen.queryByRole('link', { name: /Download PeerOnQ/ })).not.toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'Download PeerOnQ for Windows ARM64' })).toHaveAttribute('href', '/downloads/PeerOnQ-0.9.76-unsigned-public-pilot-arm64.msi');
    expect(localHints).toHaveBeenCalledWith(['architecture', 'bitness', 'wow64']);
    expect(screen.queryByText(/Recommended installer/)).not.toBeInTheDocument();
    expect(network).not.toHaveBeenCalled();
  });

  it('requires a manual architecture when browser hints are unavailable or unsupported', async () => {
    hints(Promise.resolve({ architecture: 'x86', bitness: '32' }));
    const user = userEvent.setup(); render(<DownloadsPage />);
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Choose the target device'));
    expect(screen.queryByRole('link', { name: /Download PeerOnQ/ })).not.toBeInTheDocument();
    expect(screen.getByText('Choose another device or architecture').closest('details')).toHaveAttribute('open');
    await user.selectOptions(screen.getByLabelText('Architecture'), 'arm64');
    expect(screen.getByRole('link', { name: 'Download PeerOnQ for Windows ARM64' })).toBeVisible();
  });

  it('lets a phone select a Windows host package and requires an explicit architecture', async () => {
    vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)');
    const user = userEvent.setup(); render(<DownloadsPage />);
    await user.click(screen.getByText('Choose another device or architecture'));
    await user.selectOptions(screen.getByLabelText('Platform'), 'windows');
    expect(screen.queryByRole('link', { name: /Download PeerOnQ/ })).not.toBeInTheDocument();
    await user.selectOptions(screen.getByLabelText('Architecture'), 'x64');
    expect(screen.getByRole('link', { name: 'Download PeerOnQ for Windows x64' })).toBeVisible();
  });

  it('does not substitute an x64-only embedded release for requested ARM64', async () => {
    vi.stubEnv('VITE_PEERONQ_EMBEDDED_WINDOWS_X64_URL', '/downloads/PeerOnQ-Windows-x64.msi');
    vi.stubEnv('VITE_PEERONQ_EMBEDDED_WINDOWS_VERSION', '0.9.76');
    vi.stubEnv('VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT', 'true');
    const user = userEvent.setup(); render(<DownloadsPage />);
    await user.click(screen.getByText('Choose another device or architecture'));
    await user.selectOptions(screen.getByLabelText('Architecture'), 'arm64');
    expect(screen.queryByRole('link', { name: /Download PeerOnQ/ })).not.toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Windows ARM64 is not currently available');
  });

  it('uses the exact paired ARM64 embedded package when explicitly provided', async () => {
    vi.stubEnv('VITE_PEERONQ_EMBEDDED_WINDOWS_X64_URL', '/downloads/PeerOnQ-Windows-x64.msi');
    vi.stubEnv('VITE_PEERONQ_EMBEDDED_WINDOWS_ARM64_URL', '/downloads/PeerOnQ-Windows-arm64.msi');
    vi.stubEnv('VITE_PEERONQ_EMBEDDED_WINDOWS_VERSION', '0.9.76');
    vi.stubEnv('VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT', 'true');
    const user = userEvent.setup(); render(<DownloadsPage />);
    await user.click(screen.getByText('Choose another device or architecture'));
    await user.selectOptions(screen.getByLabelText('Architecture'), 'arm64');
    expect(screen.getByRole('link', { name: 'Download PeerOnQ for Windows ARM64' })).toHaveAttribute('href', '/downloads/PeerOnQ-Windows-arm64.msi?v=0.9.76');
  });

  it('keeps a manual selection when a delayed hint response arrives', async () => {
    let finish!: (value: { architecture: string; bitness: string }) => void;
    hints(new Promise((resolve) => { finish = resolve; }));
    const user = userEvent.setup(); render(<DownloadsPage />);
    await user.click(screen.getByText('Choose another device or architecture'));
    await user.selectOptions(screen.getByLabelText('Architecture'), 'x64');
    await act(async () => { finish({ architecture: 'arm', bitness: '64' }); });
    await waitFor(() => expect(screen.getByRole('link', { name: 'Download PeerOnQ for Windows x64' })).toBeVisible());
    expect(screen.queryByText(/Recommended installer/)).not.toBeInTheDocument();
  });

  it.each([
    [{ architecture: 'x86', bitness: '64' }, 'x64'],
    [{ architecture: 'arm64' }, 'ARM64'],
    [{ architecture: 'aarch64' }, 'ARM64'],
    [{ architecture: 'x64' }, 'x64'],
    [{ architecture: 'x86_64' }, 'x64'],
    [{ architecture: 'AMD64' }, 'x64'],
    [{ architecture: 'x86', bitness: '32', wow64: true }, 'x64'],
  ])('automatically resolves supported local hints %j as %s', async (values, architecture) => {
    hints(Promise.resolve(values));
    render(<DownloadsPage />);
    expect(await screen.findByRole('link', { name: `Download PeerOnQ for Windows ${architecture}` })).toBeVisible();
    expect(screen.queryByText(/Recommended installer/)).not.toBeInTheDocument();
  });

  it.each(['arm', 'mips'])('does not reuse a frozen x64 signature for unsupported or incomplete %s hints', async (architecture) => {
    hints(Promise.resolve({ architecture }));
    render(<DownloadsPage />);
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Choose the target device'));
    expect(screen.queryByRole('link', { name: /Download PeerOnQ/ })).not.toBeInTheDocument();
  });

  it.each([{}, { architecture: 'arm' }, { architecture: 'x86', bitness: '64' }])('retains explicit ARM64 UA when hints are partial or contradictory: %j', async (values) => {
    vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('Mozilla/5.0 (Windows NT 10.0; ARM64)');
    hints(Promise.resolve(values));
    render(<DownloadsPage />);
    expect(await screen.findByRole('link', { name: 'Download PeerOnQ for Windows ARM64' })).toBeVisible();
    expect(screen.queryByRole('link', { name: 'Download PeerOnQ for Windows x64' })).not.toBeInTheDocument();
  });

  it('retains explicit ARM64 UA when hints are rejected', async () => {
    vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('Mozilla/5.0 (Windows NT 10.0; ARM64)');
    hints(Promise.reject(new DOMException('Denied', 'NotAllowedError')));
    render(<DownloadsPage />);
    expect(await screen.findByRole('link', { name: 'Download PeerOnQ for Windows ARM64' })).toBeVisible();
  });

  it('keeps a recommendation on restricted empty hints without labeling it verified', async () => {
    hints(Promise.resolve({}));
    render(<DownloadsPage />);
    expect(await screen.findByRole('link', { name: 'Download PeerOnQ for Windows x64' })).toBeVisible();
    expect(screen.getByText('Recommended installer: Windows x64')).toBeVisible();
  });

  it.each(['Mozilla/5.0 (Windows NT 10.0)', 'Mozilla/5.0 (Windows NT 6.1; x86)'])('does not force x64 for an ambiguous or 32-bit Windows signature %s', (userAgent) => {
    vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue(userAgent);
    render(<DownloadsPage />);
    expect(screen.queryByRole('link', { name: /Download PeerOnQ/ })).not.toBeInTheDocument();
  });

  it('automatically recommends x64 for legacy WOW64 without claiming native detection', () => {
    vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('Mozilla/5.0 (Windows NT 10.0; WOW64; rv:144.0) Gecko/20100101 Firefox/144.0');
    render(<DownloadsPage />);
    expect(screen.getByRole('link', { name: 'Download PeerOnQ for Windows x64' })).toBeVisible();
    expect(screen.getByText('Recommended installer: Windows x64')).toBeVisible();
    expect(screen.getByRole('status')).toHaveTextContent('Native architecture could not be verified');
  });

  it('uses local platform hints when the legacy platform is masked', async () => {
    vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('Mozilla/5.0');
    vi.spyOn(navigator, 'platform', 'get').mockReturnValue('');
    hints(Promise.resolve({ architecture: 'arm', bitness: '64' }), 'Windows');
    render(<DownloadsPage />);
    expect(await screen.findByRole('link', { name: 'Download PeerOnQ for Windows ARM64' })).toBeVisible();
  });

  it.each([
    ['x86_64', 'VITE_PEERONQ_LINUX_X64_DOWNLOAD_URL'],
    ['aarch64', 'VITE_PEERONQ_LINUX_ARM64_DOWNLOAD_URL'],
  ])('automatically selects the configured Linux %s package', (architecture, variable) => {
    vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue(`Mozilla/5.0 (X11; Linux ${architecture})`);
    vi.spyOn(navigator, 'platform', 'get').mockReturnValue(`Linux ${architecture}`);
    vi.stubEnv(variable, `https://downloads.example.test/linux/${architecture}`);
    render(<DownloadsPage />);
    expect(screen.getByRole('link', { name: 'Download PeerOnQ for Linux' })).toHaveAttribute('href', `https://downloads.example.test/linux/${architecture}`);
  });

  it('bounds stalled local hints and still accepts a later authoritative ARM64 response', async () => {
    vi.useFakeTimers();
    let finish!: (value: { architecture: string; bitness: string }) => void;
    hints(new Promise((resolve) => { finish = resolve; }));
    render(<DownloadsPage />);
    await act(async () => { await vi.advanceTimersByTimeAsync(1499); });
    expect(screen.queryByRole('link', { name: /Download PeerOnQ/ })).not.toBeInTheDocument();
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(screen.getByRole('link', { name: 'Download PeerOnQ for Windows x64' })).toBeVisible();
    expect(screen.getByRole('status')).toHaveTextContent('Native architecture could not be verified');
    await act(async () => { finish({ architecture: 'arm', bitness: '64' }); });
    expect(screen.getByRole('link', { name: 'Download PeerOnQ for Windows ARM64' })).toBeVisible();
  });

  it('does not choose a package after the metadata deadline without any 64-bit evidence', async () => {
    vi.useFakeTimers();
    vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('Mozilla/5.0 (Windows NT 10.0)');
    hints(new Promise(() => {}));
    render(<DownloadsPage />);
    await act(async () => { await vi.advanceTimersByTimeAsync(1500); });
    expect(screen.queryByRole('link', { name: /Download PeerOnQ/ })).not.toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Choose the target device');
  });

  it('clears the local metadata deadline when the component unmounts', () => {
    vi.useFakeTimers();
    hints(new Promise(() => {}));
    const page = render(<DownloadsPage />);
    expect(vi.getTimerCount()).toBe(1);
    page.unmount();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('does not substitute x64 when an automatically detected ARM64 package is absent', async () => {
    hints(Promise.resolve({ architecture: 'arm', bitness: '64' }));
    vi.stubEnv('VITE_PEERONQ_SERVER_WINDOWS_X64_AVAILABLE', 'true');
    render(<DownloadsPage />);
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Windows ARM64 is not currently available'));
    expect(screen.queryByRole('link', { name: /Download PeerOnQ/ })).not.toBeInTheDocument();
  });
});
