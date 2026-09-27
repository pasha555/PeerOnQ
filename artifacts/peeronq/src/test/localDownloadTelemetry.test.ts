import { EventEmitter } from 'node:events';
import { describe, expect, it, vi } from 'vitest';

import {
  matchLocalMsi,
  observeLocalMsiDownload,
  readTelemetryResponse,
  withOneTelemetryRetry,
  type LocalDownloadTelemetryClient,
  type TelemetryHttpResponse,
} from '../../server/localDownloadTelemetry';

class TestResponse extends EventEmitter {
  statusCode = 200;
  writableFinished = false;
}

class TestTelemetryHttpResponse extends EventEmitter implements TelemetryHttpResponse {
  statusCode = 200;
  headers = {};
  complete = false;
  destroy = vi.fn();
}

function client() {
  const start = vi.fn<LocalDownloadTelemetryClient['start']>().mockResolvedValue({
    downloadId: 'cb05d22e-e071-49af-ab32-40aa1d0d5b7e',
    token: 'completion-token-with-at-least-thirty-two-characters',
  });
  const complete = vi.fn<LocalDownloadTelemetryClient['complete']>().mockResolvedValue(undefined);
  return { start, complete };
}

async function flushPromises() {
  await new Promise<void>((resolve) => setImmediate(resolve));
}

describe('local download telemetry', () => {
  it('accepts only versioned local development MSI paths', () => {
    expect(matchLocalMsi('/downloads/PeerOnQ-0.9.56-unsigned-development-x64.msi?cache=1')).toEqual({
      version: '0.9.56',
      artifactQualifier: 'unsigned-development',
      architecture: 'X64',
    });
    expect(matchLocalMsi('/downloads/PeerOnQ-0.9.56-unsigned-development-arm64.msi')).toEqual({
      version: '0.9.56',
      artifactQualifier: 'unsigned-development',
      architecture: 'Arm64',
    });
    expect(matchLocalMsi('/downloads/PeerOnQ-0.9.66-unsigned-public-pilot-x64.msi')).toEqual({
      version: '0.9.66',
      artifactQualifier: 'unsigned-public-pilot',
      architecture: 'X64',
    });
    expect(matchLocalMsi('/downloads/other.msi')).toBeNull();
    expect(matchLocalMsi('/src/main.tsx')).toBeNull();
  });

  it('records a completed full response without delaying the download middleware', async () => {
    const telemetry = client();
    const response = new TestResponse();

    expect(observeLocalMsiDownload({
      method: 'GET',
      url: '/downloads/PeerOnQ-0.9.56-unsigned-development-x64.msi',
      headers: { 'user-agent': 'PeerOnQ test browser' },
    }, response, telemetry)).toBe(true);
    expect(telemetry.start).toHaveBeenCalledWith({
      version: '0.9.56',
      artifactQualifier: 'unsigned-development',
      architecture: 'X64',
      userAgent: 'PeerOnQ test browser',
    });

    response.writableFinished = true;
    response.emit('finish');
    await flushPromises();
    expect(telemetry.complete).toHaveBeenCalledWith(
      expect.objectContaining({ downloadId: expect.any(String) }),
      'Completed',
    );
  });

  it('records range responses as partial and disconnects as cancelled once', async () => {
    const partialTelemetry = client();
    const partialResponse = new TestResponse();
    partialResponse.statusCode = 206;
    observeLocalMsiDownload({
      method: 'GET',
      url: '/downloads/PeerOnQ-0.9.56-unsigned-development-arm64.msi',
      headers: {},
    }, partialResponse, partialTelemetry);
    partialResponse.writableFinished = true;
    partialResponse.emit('finish');
    await flushPromises();
    expect(partialTelemetry.complete).toHaveBeenCalledWith(expect.any(Object), 'Partial');

    const cancelledTelemetry = client();
    const cancelledResponse = new TestResponse();
    observeLocalMsiDownload({
      method: 'GET',
      url: '/downloads/PeerOnQ-0.9.56-unsigned-development-x64.msi',
      headers: {},
    }, cancelledResponse, cancelledTelemetry);
    cancelledResponse.emit('close');
    cancelledResponse.emit('finish');
    await flushPromises();
    expect(cancelledTelemetry.complete).toHaveBeenCalledTimes(1);
    expect(cancelledTelemetry.complete).toHaveBeenCalledWith(expect.any(Object), 'Cancelled');
  });

  it('ignores HEAD and unrelated requests', () => {
    const telemetry = client();
    const response = new TestResponse();
    expect(observeLocalMsiDownload({
      method: 'HEAD',
      url: '/downloads/PeerOnQ-0.9.56-unsigned-development-x64.msi',
      headers: {},
    }, response, telemetry)).toBe(false);
    expect(observeLocalMsiDownload({
      method: 'GET',
      url: '/downloads/SHA256SUMS.txt',
      headers: {},
    }, response, telemetry)).toBe(false);
    expect(telemetry.start).not.toHaveBeenCalled();
  });

  it('rejects truncated telemetry responses and retries them once', async () => {
    let attempts = 0;
    const operation = () => {
      attempts++;
      const response = new TestTelemetryHttpResponse();
      const result = readTelemetryResponse(response);
      response.emit('data', Buffer.from('{'));
      response.emit('aborted');
      response.emit('close');
      return result;
    };

    await expect(withOneTelemetryRetry(operation)).rejects.toThrow('response was aborted');
    expect(attempts).toBe(2);
  });
});
