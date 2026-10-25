import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { request as httpsRequest } from 'node:https';
import type { IncomingHttpHeaders } from 'node:http';
import type { Plugin } from 'vite';

const LOCAL_MSI_PATTERN = /^\/downloads\/PeerOnQ-([0-9]+(?:\.[0-9]+){2,3})-(unsigned-(?:development|public-pilot))-(x64|arm64)\.msi$/i;
const MAXIMUM_RESPONSE_BYTES = 64 * 1024;
const RETRY_DELAY_MILLISECONDS = 150;

export type LocalDownloadResult = 'Completed' | 'Partial' | 'Cancelled' | 'Failed';

export interface LocalDownloadMetadata {
  architecture: 'X64' | 'Arm64';
  version: string;
  artifactQualifier: 'unsigned-development' | 'unsigned-public-pilot';
  userAgent?: string;
}

export interface LocalDownloadSession {
  downloadId: string;
  token: string;
}

export interface LocalDownloadTelemetryClient {
  start(metadata: LocalDownloadMetadata): Promise<LocalDownloadSession>;
  complete(session: LocalDownloadSession, result: LocalDownloadResult): Promise<void>;
  reportFailure?(error: unknown): void;
}

interface RequestLike {
  method?: string;
  url?: string;
  headers: IncomingHttpHeaders;
}

interface ResponseLike {
  statusCode: number;
  writableFinished: boolean;
  once(event: 'finish' | 'close', listener: () => void): unknown;
}

export interface TelemetryHttpResponse {
  statusCode?: number;
  headers: IncomingHttpHeaders;
  complete: boolean;
  on(event: 'data', listener: (chunk: Buffer) => void): unknown;
  on(event: 'end' | 'aborted' | 'close', listener: () => void): unknown;
  on(event: 'error', listener: (error: Error) => void): unknown;
  destroy(): void;
}

export function matchLocalMsi(rawUrl: string | undefined): Omit<LocalDownloadMetadata, 'userAgent'> | null {
  if (!rawUrl) return null;
  const pathname = new URL(rawUrl, 'http://peeronq.local').pathname;
  const match = LOCAL_MSI_PATTERN.exec(pathname);
  if (!match) return null;
  return {
    version: match[1],
    artifactQualifier: match[2].toLowerCase() as LocalDownloadMetadata['artifactQualifier'],
    architecture: match[3].toLowerCase() === 'arm64' ? 'Arm64' : 'X64',
  };
}

export function observeLocalMsiDownload(
  request: RequestLike,
  response: ResponseLike,
  client: LocalDownloadTelemetryClient,
): boolean {
  if (request.method !== 'GET') return false;
  const metadata = matchLocalMsi(request.url);
  if (!metadata) return false;

  const rawUserAgent = request.headers['user-agent'];
  const userAgent = Array.isArray(rawUserAgent) ? rawUserAgent[0] : rawUserAgent;
  const started = client.start({
    ...metadata,
    userAgent: userAgent?.slice(0, 512),
  }).catch((error: unknown) => {
    // Handle rejection while the MSI is still streaming, before finish/close.
    client.reportFailure?.(error);
    return null;
  });
  let settled = false;

  const settle = (result: LocalDownloadResult) => {
    if (settled) return;
    settled = true;
    void started
      .then((session) => session ? client.complete(session, result) : undefined)
      .catch((error: unknown) => client.reportFailure?.(error));
  };

  response.once('finish', () => {
    settle(response.statusCode === 206
      ? 'Partial'
      : response.statusCode >= 200 && response.statusCode < 300
        ? 'Completed'
        : 'Failed');
  });
  response.once('close', () => {
    if (!response.writableFinished) settle('Cancelled');
  });
  return true;
}

function createLocalDownloadTelemetryPlugin(
  client: LocalDownloadTelemetryClient,
): Plugin {
  return {
    name: 'peeronq-local-download-telemetry',
    apply: 'serve',
    configureServer(server) {
      server.middlewares.use((request, response, next) => {
        observeLocalMsiDownload(request, response, client);
        next();
      });
    },
  };
}

export function createLocalDownloadTelemetryPluginFromEnvironment(): Plugin[] {
  const rawBaseUrl = process.env.PEERONQ_DOWNLOAD_TELEMETRY_BASE_URL?.trim();
  if (!rawBaseUrl) return [];
  const baseUrl = validateBaseUrl(rawBaseUrl);
  const caPath = process.env.PEERONQ_DOWNLOAD_TELEMETRY_CA_FILE?.trim();
  const ca = caPath ? readFileSync(caPath) : undefined;
  let lastWarningAt = 0;
  const client: LocalDownloadTelemetryClient = {
    async start(metadata) {
      const downloadId = randomUUID();
      const publicPilot = metadata.artifactQualifier === 'unsigned-public-pilot';
      const response = await postJson(
        new URL('/v1/downloads/start', baseUrl),
        {
          downloadId,
          platform: 'Windows',
          architecture: metadata.architecture,
          version: metadata.version,
          channel: publicPilot ? 'Beta' : 'Development',
          source: publicPilot ? 'local-public-pilot-website' : 'local-development-website',
          campaign: null,
          countryCode: null,
          userAgentFamily: null,
          idempotencyKey: `local-static:${downloadId}`,
        },
        ca,
        metadata.userAgent,
      );
      const token = response.headers['x-peeronq-download-token'];
      if (typeof token !== 'string' || token.length < 32) {
        throw new Error('Download telemetry did not return a completion token.');
      }
      return { downloadId, token };
    },
    async complete(session, result) {
      await postJson(
        new URL('/v1/downloads/complete', baseUrl),
        { downloadId: session.downloadId, result, completedAtUtc: new Date().toISOString() },
        ca,
        undefined,
        session.token,
      );
    },
    reportFailure(error) {
      const now = Date.now();
      if (now - lastWarningAt < 30_000) return;
      lastWarningAt = now;
      const detail = error instanceof Error ? error.message : 'unknown error';
      console.warn(`[PeerOnQ] Local download telemetry unavailable: ${detail}`);
    },
  };
  return [createLocalDownloadTelemetryPlugin(client)];
}

function validateBaseUrl(value: string): URL {
  const url = new URL(value);
  if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash) {
    throw new Error('PEERONQ_DOWNLOAD_TELEMETRY_BASE_URL must be an HTTPS origin without credentials, query, or fragment.');
  }
  url.pathname = '/';
  return url;
}

function postJson(
  url: URL,
  body: unknown,
  ca: Buffer | undefined,
  userAgent?: string,
  completionToken?: string,
): Promise<{ headers: Record<string, string | string[] | undefined> }> {
  return withOneTelemetryRetry(() => postJsonOnce(url, body, ca, userAgent, completionToken));
}

export async function withOneTelemetryRetry<T>(operation: () => Promise<T>): Promise<T> {
  try {
    return await operation();
  } catch (error) {
    if (!(error instanceof RetryableTelemetryError)) throw error;
    await new Promise<void>((resolve) => setTimeout(resolve, RETRY_DELAY_MILLISECONDS));
    return operation();
  }
}

function postJsonOnce(
  url: URL,
  body: unknown,
  ca: Buffer | undefined,
  userAgent?: string,
  completionToken?: string,
): Promise<{ headers: Record<string, string | string[] | undefined> }> {
  const payload = Buffer.from(JSON.stringify(body));
  return new Promise((resolve, reject) => {
    const request = httpsRequest(url, {
      method: 'POST',
      ca,
      rejectUnauthorized: true,
      headers: {
        'Content-Type': 'application/json',
        'Content-Length': payload.length,
        'User-Agent': userAgent || 'PeerOnQ-Local-Website/1.0',
        ...(completionToken ? { 'X-PeerOnQ-Download-Token': completionToken } : {}),
      },
    }, (response) => {
      void readTelemetryResponse(response).then(resolve, reject);
    });
    request.setTimeout(3_000, () => request.destroy(
      new RetryableTelemetryError('Download telemetry timed out.'),
    ));
    request.on('error', (error) => reject(error instanceof RetryableTelemetryError
      ? error
      : new RetryableTelemetryError(`Download telemetry request failed: ${error.message}`)));
    request.end(payload);
  });
}

export function readTelemetryResponse(
  response: TelemetryHttpResponse,
): Promise<{ headers: Record<string, string | string[] | undefined> }> {
  return new Promise((resolve, reject) => {
    let length = 0;
    let settled = false;
    const fail = (error: Error) => {
      if (settled) return;
      settled = true;
      reject(error);
    };
    response.on('data', (chunk: Buffer) => {
      length += chunk.length;
      if (length <= MAXIMUM_RESPONSE_BYTES) return;
      fail(new Error('Download telemetry response exceeded the size limit.'));
      response.destroy();
    });
    response.on('end', () => {
      if (settled) return;
      settled = true;
      const status = response.statusCode ?? 0;
      if (status < 200 || status >= 300) {
        const message = `Download telemetry returned HTTP ${status}.`;
        reject(status === 429 || status >= 500
          ? new RetryableTelemetryError(message)
          : new Error(message));
        return;
      }
      resolve({ headers: response.headers });
    });
    response.on('aborted', () => fail(
      new RetryableTelemetryError('Download telemetry response was aborted.'),
    ));
    response.on('error', (error) => fail(
      new RetryableTelemetryError(`Download telemetry response failed: ${error.message}`),
    ));
    response.on('close', () => {
      if (!response.complete) {
        fail(new RetryableTelemetryError('Download telemetry response closed prematurely.'));
      }
    });
  });
}

class RetryableTelemetryError extends Error {}
